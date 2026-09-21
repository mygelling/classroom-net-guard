using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ClassroomNetGuard.Shared;

namespace StudentService
{
    /// <summary>
    /// 本地 HTTP/HTTPS 代理（学生端核心管控层）：
    /// 监听 127.0.0.1，按白名单策略判定每个请求。HTTPS 走 CONNECT 隧道，在建立隧道前按目标域名判定；
    /// HTTP 按 Host 头判定。白名单制下默认全拦，命中白名单才转发。断线时沿用最后策略（兜底拦截）。
    /// </summary>
    public sealed class ProxyServer
    {
        const string BlockedHtml =
            "<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><title>访问被拦截</title></head>" +
            "<body style=\"font-family:'Microsoft YaHei',sans-serif;background:#FDEBEA;color:#7F1D1D;display:flex;align-items:center;justify-content:center;height:100vh;margin:0\">" +
            "<div style=\"text-align:center;background:#fff;padding:40px 48px;border-radius:16px;box-shadow:0 8px 30px rgba(0,0,0,.08)\">" +
            "<div style=\"font-size:42px\">&#128274;</div>" +
            "<h1 style=\"font-size:22px;margin:10px 0 6px\">访问已被拦截</h1>" +
            "<p style=\"font-size:14px;color:#555;margin:0 0 14px\">该网站不在教师白名单内，本次访问已上报教师端。</p>" +
            "<code style=\"background:#F5F5F5;padding:4px 10px;border-radius:6px;font-size:13px\">{0}</code>" +
            "{1}" +
            "</div></body></html>";

        // 有解锁密码时：显示解锁表单（提交到本地策略 API 校验）
        const string UnlockFormHtml =
            "<div style=\"margin-top:16px\">" +
            "<form action=\"http://127.0.0.1:8890/unlock\" method=\"post\" style=\"display:flex;gap:8px;justify-content:center;align-items:center\">" +
            "<input type=\"password\" name=\"password\" placeholder=\"输入教师下发的解锁密码\" " +
            "style=\"padding:9px 12px;border:1px solid #D8D8D8;border-radius:8px;font-size:13px;width:220px\"/> " +
            "<button type=\"submit\" style=\"padding:9px 18px;background:#0E7C66;color:#fff;border:0;border-radius:8px;font-size:13px;font-weight:bold;cursor:pointer\">解锁上网</button>" +
            "</form>" +
            "<p style=\"font-size:12px;color:#999;margin:10px 0 0\">解锁后 30 分钟内可自由上网，到期自动恢复课堂管控。</p>" +
            "</div>";

        // 未设置密码时：提示联系教师
        const string NoUnlockHtml =
            "<div style=\"margin-top:16px;font-size:13px;color:#888\">如需临时解锁，请联系教师为该学生机设置解锁密码。</div>";

        readonly NetPolicy _policy;
        readonly TeacherConnection _conn;
        readonly CertManager _certMgr;
        readonly UnlockState _unlock;
        readonly CancellationTokenSource _cts = new CancellationTokenSource();
        TcpListener _listener;
        int _blockedCount;
        volatile string _lastUrl = "";
        // 活跃客户端连接跟踪：记录每个连接的 host/类型/响应是否已开始，
        // 管控恢复（教师端切回课堂管控/解锁被撤销）时：未开始响应的 HTTP 连接注入拦截页（自动显示"访问已被拦截"），
        // 其余连接断开，刷新后重新走拦截判定。
        readonly ConcurrentDictionary<TcpClient, LiveConn> _liveConns = new ConcurrentDictionary<TcpClient, LiveConn>();
        System.Threading.Timer _unlockWatch;
        bool _lastRelaxed;

        /// <summary>活跃代理连接的状态：用于管控恢复时对未完成响应的 HTTP 连接注入拦截页。</summary>
        sealed class LiveConn
        {
            public NetworkStream Stream;
            public string Host = "";
            public int Port;
            public bool IsConnect;
            /// <summary>请求头中的来源值（Referer/Origin 原始值），用于白名单页面第三方子资源的关联放行。</summary>
            public volatile string ReferrerHost;
            /// <summary>是否为"文档导航"请求（顶层页面/iframe 加载）：true 时不做来源关联放行，必须域名本身命中白名单。</summary>
            public volatile bool Navigation;
            public volatile bool ResponseStarted; // 已开始向浏览器写响应（之后只能断开，不能注入）
            public readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1); // 串行化对该连接的写入
        }

        public ProxyServer(NetPolicy policy, TeacherConnection conn, CertManager certMgr, UnlockState unlock)
        {
            _policy = policy;
            _conn = conn;
            _certMgr = certMgr;
            _unlock = unlock;
        }

        public int BlockedCount => _blockedCount;
        public string LastUrl => _lastUrl;

        public void Start(int port)
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            _ = Task.Run(AcceptLoopAsync);
            _unlockWatch = new System.Threading.Timer(UnlockWatchTick, null, 1000, 500);
            LogWriter.Info("本地代理已启动 127.0.0.1:" + port);
        }

        public void Stop()
        {
            _cts.Cancel();
            _unlockWatch?.Dispose();
            foreach (var c in _liveConns.Keys) { try { c.Close(); } catch { } }
            _liveConns.Clear();
            try { _listener?.Stop(); } catch { }
        }

        void UnlockWatchTick(object state)
        {
            try
            {
                // 宽松状态 = 自由模式（全部放行）或 网页解锁中（临时放行）。
                // 任一解除（教师端切回课堂管控 / 解锁被撤销）→
                //   未开始响应的 HTTP 连接注入 403 拦截页（当前网页自动显示"访问已被拦截"）；
                //   已开始响应或 HTTPS 隧道连接直接断开（浏览器刷新后重新判定）。
                var relaxed = !_policy.ClassroomOn || _unlock.IsActive;
                if (_lastRelaxed && !relaxed)
                {
                    foreach (var kv in _liveConns)
                    {
                        var st = kv.Value;
                        if (!st.IsConnect && !st.ResponseStarted && !string.IsNullOrEmpty(st.Host))
                        {
                            // 白名单站点 / 资源放行域名 / 白名单页面引用的第三方子资源：不注入，等正常响应返回；
                            // 其余（含来源关联放行的文档导航）：注入拦截页（自动显示"访问已被拦截"）
                            if (IsAllowedWithReferrer(st.Host, st.Port, st.ReferrerHost, st.Navigation))
                                continue;
                            _ = InjectBlockedAsync(st);
                        }
                        else
                        {
                            try { kv.Key.Close(); } catch { }
                        }
                    }
                    LogWriter.Info("管控恢复：未在白名单的当前页面注入拦截页，其余连接断开");
                    // 学生端使用默认 Edge（无调试端口）：已完全加载的页面无法自动刷新，
                    // 需由学生手动刷新后重新走拦截判定；新发起的访问与未响应连接由上面注入逻辑直接拦截。
                }
                _lastRelaxed = relaxed;
            }
            catch { }
        }

        /// <summary>向尚未开始响应的 HTTP 放行连接注入 403 拦截页（原请求响应被替换），随后断开连接。</summary>
        async Task InjectBlockedAsync(LiveConn st)
        {
            try
            {
                var unlockArea = string.IsNullOrWhiteSpace(_policy.UnlockPassword) ? NoUnlockHtml : UnlockFormHtml;
                var body = Encoding.UTF8.GetBytes(string.Format(BlockedHtml,
                    System.Net.WebUtility.HtmlEncode(st.Host), unlockArea));
                var header = "HTTP/1.1 403 Forbidden\r\n" +
                             "Content-Type: text/html; charset=utf-8\r\n" +
                             "Content-Length: " + body.Length + "\r\n" +
                             "Connection: close\r\n" +
                             "Cache-Control: no-store\r\n\r\n";
                var hb = Encoding.ASCII.GetBytes(header);
                await st.Gate.WaitAsync();
                try
                {
                    await st.Stream.WriteAsync(hb, 0, hb.Length);
                    await st.Stream.WriteAsync(body, 0, body.Length);
                    await st.Stream.FlushAsync();
                }
                finally { st.Gate.Release(); }
            }
            catch { }
            try { st.Stream.Close(); } catch { }
        }

        async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _listener.AcceptTcpClientAsync(); }
                catch { break; }
                _ = Task.Run(() => HandleClientAsync(c));
            }
        }

        async Task HandleClientAsync(TcpClient client)
        {
            var st = new LiveConn { Stream = client.GetStream() };
            _liveConns.TryAdd(client, st);
            try
            {
                client.NoDelay = true;
                var stream = st.Stream;

                var (head, rest) = await ReadHeadAsync(stream);
                if (head == null) return;

                var isConnect = head.StartsWith("CONNECT ", StringComparison.OrdinalIgnoreCase);
                var hp = isConnect ? ParseConnect(head) : ParseHttpHost(head);
                if (hp.host == null)
                {
                    await WriteBlockedAsync(stream, "(无法识别请求)");
                    return;
                }

                var host = hp.host;
                st.Host = host;
                st.Port = hp.port;
                st.IsConnect = isConnect;
                st.ReferrerHost = ParseReferrerValue(head); // 白名单页面引用的第三方子资源据此放行
                st.Navigation = IsDocumentNavigation(head); // 文档导航（顶层/iframe）不做来源关联放行
                _lastUrl = host;
                _conn.SetCurrentUrl(host);

                // 放行条件：白名单站点 / 资源放行域名 / 白名单页面引用的第三方子资源（非文档导航时的来源关联）/ 解锁期间临时放行
                if (IsAllowedWithReferrer(host, hp.port, st.ReferrerHost, st.Navigation) || _unlock.IsActive)
                {
                    using var upstream = new TcpClient { NoDelay = true };
                    await upstream.ConnectAsync(host, hp.port);
                    var up = upstream.GetStream();

                    if (isConnect)
                    {
                        // https 白名单：直连隧道（不解密），下载管控不适用于隧道内流量
                        await WriteAsciiAsync(stream, "HTTP/1.1 200 Connection Established\r\n\r\n");
                        var t1 = PumpAsync(stream, up);
                        var t2 = PumpAsync(up, stream);
                        await Task.WhenAny(t1, t2);
                    }
                    else
                    {
                        // http 白名单：转发请求，并对响应做下载管控（替代浏览器扩展）
                        // 注意：ReadHeadAsync 剥离了头部终止符，转发时必须补回 \r\n\r\n，否则上游等不到完整请求
                        // 注意：浏览器发给代理的是“绝对形式”请求行（GET http://host/...），
                        // 上游服务器通常只接受“原始形式”（GET /path...），必须改写后再转发，否则返回 400/404。
                        // 注意：rest 是请求头之后已读到的字节（请求体开头），必须完整转发给上游，
                        // 且 POST/PUT 等带请求体的请求剩余 body 也要从客户端流读完后转发，否则上游等 body 超时断开。
                        var rewrittenHead = RewriteRequestLine(head);
                        var headBytes = Encoding.ASCII.GetBytes(rewrittenHead + "\r\n\r\n");
                        await up.WriteAsync(headBytes, 0, headBytes.Length);
                        await ForwardRequestBodyAsync(stream, up, head, rest ?? Array.Empty<byte>());
                        await PumpHttpWithDownloadCheckAsync(st, up, host, head);
                    }
                }
                else
                {
                    Interlocked.Increment(ref _blockedCount);
                    _conn.AddBlocked();
                    if (isConnect)
                    {
                        // HTTPS：先应答 CONNECT 成功，代理扮演服务器完成 TLS 握手后返回拦截页
                        // （浏览器信任本机 CA，因此能正常渲染拦截页而不是显示“无法访问”）
                        await WriteBlockedHttpsAsync(stream, host);
                    }
                    else
                    {
                        await WriteBlockedAsync(stream, host);
                    }
                }
            }
            catch { }
            finally
            {
                _liveConns.TryRemove(client, out _);
                try { client.Close(); } catch { }
            }
        }

        // ===== 头部解析 =====

        /// <summary>放行判定（含来源关联）：白名单站点 / 资源放行域名 / 白名单页面引用的第三方子资源。
        /// 文档导航（顶层页面/iframe 加载、无 Sec-Fetch-Mode 的程序请求）不做来源关联放行，
        /// 防止学生从白名单页面点击外链跳转到任意网站、或伪造 Referer 绕过白名单。</summary>
        bool IsAllowedWithReferrer(string host, int port, string referrerHost, bool navigation)
        {
            if (_policy.IsDomainAllowed(host, port)) return true;
            if (_policy.IsResourceDomainAllowed(host, port)) return true;
            if (!navigation && _policy.ReferrerAllowEnabled && !string.IsNullOrWhiteSpace(referrerHost))
                return _policy.IsReferrerAllowed(referrerHost);
            return false;
        }

        /// <summary>判断请求是否为"文档导航"（顶层页面跳转 / iframe 加载）。
        /// 现代浏览器所有请求都带 Sec-Fetch-Mode：navigate=顶层导航、nested-navigate=iframe；
        /// 其余值（no-cors/cors/same-origin/websocket 等）为页面引用的子资源。
        /// 无该头（非浏览器程序，如 curl）一律视为导航——不允许来源关联放行。</summary>
        static bool IsDocumentNavigation(string head)
        {
            try
            {
                var lines = head.Split('\n');
                foreach (var raw in lines)
                {
                    var line = raw.TrimEnd('\r');
                    if (line.Length == 0) continue;
                    var colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    if (!line.Substring(0, colon).Trim().Equals("Sec-Fetch-Mode", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var mode = line.Substring(colon + 1).Trim().ToLowerInvariant();
                    return mode == "navigate" || mode == "nested-navigate";
                }
            }
            catch { }
            return true; // 无 Sec-Fetch-Mode：保守视为导航
        }

        /// <summary>从请求头提取 Referer/Origin 的原始值（供第三方子资源关联放行）。无来源或无法解析返回 null。</summary>
        static string ParseReferrerValue(string head)
        {
            try
            {
                var lines = head.Split('\n');
                foreach (var raw in lines)
                {
                    var line = raw.TrimEnd('\r');
                    if (line.Length == 0) continue;
                    var colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    var name = line.Substring(0, colon).Trim();
                    if (!name.Equals("Referer", StringComparison.OrdinalIgnoreCase) &&
                        !name.Equals("Origin", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var value = line.Substring(colon + 1).Trim();
                    if (value.Length == 0) continue;
                    return value;
                }
            }
            catch { }
            return null;
        }

        static async Task<(string head, byte[] rest)> ReadHeadAsync(Stream stream, int max = 64 * 1024)
        {
            var pending = new List<byte>();
            var buf = new byte[2048];
            while (pending.Count < max)
            {
                int n;
                try { n = await stream.ReadAsync(buf, 0, buf.Length); }
                catch { return (null, null); }
                if (n <= 0) return (null, null);
                pending.AddRange(buf.Take(n));

                var idx = IndexOfHeaderEnd(pending);
                if (idx >= 0)
                {
                    var head = Encoding.ASCII.GetString(pending.Take(idx).ToArray());
                    var restBytes = pending.Skip(idx + 4).ToArray();
                    return (head, restBytes);
                }
            }
            return (null, null);
        }

        static int IndexOfHeaderEnd(List<byte> data)
        {
            for (var i = 0; i + 3 < data.Count; i++)
            {
                if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                    return i;
            }
            return -1;
        }

        static (string host, int port) ParseConnect(string head)
        {
            // CONNECT host:port HTTP/1.1
            var parts = head.Split(' ');
            if (parts.Length < 2) return (null, 0);
            return SplitHostPort(parts[1], 443);
        }

        static (string host, int port) ParseHttpHost(string head)
        {
            var lines = head.Split("\r\n");
            string host = null;
            var port = 80;
            foreach (var line in lines)
            {
                if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                {
                    var hv = line.Substring(5).Trim();
                    var hp = SplitHostPort(hv, 80);
                    host = hp.host;
                    port = hp.port;
                    break;
                }
            }
            if (host == null && lines.Length > 0)
            {
                var parts = lines[0].Split(' ');
                if (parts.Length > 1 && parts[1].StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var u = new Uri(parts[1]);
                        host = u.Host;
                        port = u.Port;
                    }
                    catch { }
                }
            }
            return (host, port);
        }

        static (string host, int port) SplitHostPort(string authority, int defaultPort)
        {
            var idx = authority.LastIndexOf(':');
            if (idx > 0 && !authority.Contains(']')) // 跳过 IPv6 括号
            {
                var h = authority.Substring(0, idx);
                var p = authority.Substring(idx + 1);
                if (int.TryParse(p, out var port)) return (h, port);
            }
            return (authority, defaultPort);
        }

        /// <summary>把“绝对形式”请求行改写为“原始形式”：GET http://host/path → GET /path。</summary>
        static string RewriteRequestLine(string head)
        {
            var lines = head.Split("\r\n");
            if (lines.Length == 0) return head;
            var parts = lines[0].Split(' ');
            if (parts.Length >= 3 &&
                (parts[1].StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 parts[1].StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var u = new Uri(parts[1]);
                    var target = u.PathAndQuery;
                    if (string.IsNullOrEmpty(target)) target = "/";
                    lines[0] = parts[0] + " " + target + " " + parts[2];
                    return string.Join("\r\n", lines);
                }
                catch { }
            }
            return head;
        }

        // ===== 转发与响应 =====

        /// <summary>
        /// 转发请求体剩余部分到上游（POST/PUT 等）。rest 是请求头之后已读到的字节（请求体开头），
        /// 按 Content-Length / Transfer-Encoding: chunked / EOF 三种界定把完整请求体送到上游。
        /// </summary>
        async Task ForwardRequestBodyAsync(Stream client, Stream up, string requestHead, byte[] rest)
        {
            var lower = requestHead.ToLowerInvariant();
            var cl = Regex.Match(lower, "content-length:\\s*(\\d+)");
            if (cl.Success && long.TryParse(cl.Groups[1].Value, out var total))
            {
                if (rest.Length > 0)
                    await up.WriteAsync(rest, 0, rest.Length);
                var remain = total - rest.Length;
                if (remain > 0)
                {
                    var buf = new byte[16384];
                    while (remain > 0)
                    {
                        var n = await client.ReadAsync(buf, 0, (int)Math.Min(buf.Length, remain));
                        if (n <= 0) return; // 客户端中断：上游将因 body 不完整而断开
                        await up.WriteAsync(buf, 0, n);
                        remain -= n;
                    }
                }
            }
            else if (lower.Contains("transfer-encoding: chunked"))
            {
                await PumpRequestChunkedAsync(client, up, rest);
            }
            else if (rest.Length > 0)
            {
                // 无长度头（EOF 界定请求体，罕见）
                await up.WriteAsync(rest, 0, rest.Length);
                var buf = new byte[16384];
                int n;
                while ((n = await client.ReadAsync(buf, 0, buf.Length)) > 0)
                    await up.WriteAsync(buf, 0, n);
            }
        }

        /// <summary>chunked 请求体转发：解析客户端各 chunk（含 trailer）原样转发到上游。
        /// 数据源统一为 FillAsync（rest 优先、精确 count），行逐字节、数据按 size 精确取，无预读竞争。</summary>
        async Task PumpRequestChunkedAsync(Stream src, Stream dst, byte[] rest)
        {
            int pos = 0;

            async Task<int> FillAsync(byte[] buffer, int offset, int count)
            {
                if (pos < rest.Length)
                {
                    var take = Math.Min(rest.Length - pos, count);
                    Array.Copy(rest, pos, buffer, offset, take);
                    pos += take;
                    return take;
                }
                return await src.ReadAsync(buffer, offset, count);
            }

            async Task<string> ReadLineAsync()
            {
                var sb = new System.Text.StringBuilder();
                var one = new byte[1];
                while (true)
                {
                    var n = await FillAsync(one, 0, 1);
                    if (n <= 0) return null;
                    var b = one[0];
                    if (b == 10)
                    {
                        if (sb.Length > 0 && sb[sb.Length - 1] == '\r') sb.Length--;
                        return sb.ToString();
                    }
                    sb.Append((char)b);
                }
            }

            async Task CopyDataAsync(long size)
            {
                var buf = new byte[16384];
                var remaining = size;
                while (remaining > 0)
                {
                    var n = await FillAsync(buf, 0, (int)Math.Min(buf.Length, remaining));
                    if (n <= 0) return;
                    await dst.WriteAsync(buf, 0, n);
                    remaining -= n;
                }
            }

            while (true)
            {
                var line = await ReadLineAsync();
                if (line == null) return;
                var hex = line.Trim();
                var semi = hex.IndexOf(';');
                if (semi >= 0) hex = hex.Substring(0, semi).Trim();
                if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var size))
                    return;
                await dst.WriteAsync(Encoding.ASCII.GetBytes(line + "\r\n"));
                if (size == 0)
                {
                    while (true)
                    {
                        var tl = await ReadLineAsync();
                        if (tl == null) return;
                        await dst.WriteAsync(Encoding.ASCII.GetBytes(tl + "\r\n"));
                        if (tl.Length == 0) return;
                    }
                }
                await CopyDataAsync(size);
                if (await ReadLineAsync() == null) return; // 消费 chunk 后 CRLF
                await dst.WriteAsync(Encoding.ASCII.GetBytes("\r\n"));
            }
        }

        /// <summary>
        /// http 响应转发 + 下载管控（原浏览器扩展职责，现由代理承担）：
        /// 读响应头判定是否下载及是否符合下载策略，拦截则返回提示页，放行则继续转发。
        /// 代理为"单连接单请求"模型：转发完完整响应体（Content-Length / chunked / EOF 界定）即结束，
        /// 并把响应头改写为 Connection: close，避免浏览器 keep-alive 复用同一连接导致后续请求无人处理而转圈。
        /// 对客户端的写入经 LiveConn.Gate 串行化，管控恢复时可由 UnlockWatchTick 注入拦截页。
        /// </summary>
        async Task PumpHttpWithDownloadCheckAsync(LiveConn st, Stream src, string host, string requestHead)
        {
            var dst = st.Stream;
            var (head, rest) = await ReadHeadAsync(src);
            if (head == null) return;

            // 解锁期间下载管控一并放行（自由上网）
            if (!_unlock.IsActive && DownloadResponseBlocked(head, out var reason))
            {
                Interlocked.Increment(ref _blockedCount);
                _conn.AddBlocked();
                await WriteBlockedAsync(dst, host + "（下载被拦截：" + reason + "）");
                return;
            }

            // 改写 Connection 头为 close：代理单连接只服务一个请求，连接保持反而会让浏览器复用后卡住
            head = Regex.Replace(head, "(?im)^Connection:.*$", "Connection: close");
            head = Regex.Replace(head, "(?im)^Keep-Alive:.*$", "");

            var hb = Encoding.ASCII.GetBytes(head + "\r\n\r\n");
            await st.Gate.WaitAsync();
            try
            {
                st.ResponseStarted = true; // 开始写响应：之后管控恢复只能断开，不能注入拦截页
                await dst.WriteAsync(hb, 0, hb.Length);
            }
            finally { st.Gate.Release(); }

            // HEAD 请求无响应体
            if (requestHead.StartsWith("HEAD ", StringComparison.OrdinalIgnoreCase))
                return;

            var lower = head.ToLowerInvariant();

            long cl = -1;
            var clm = Regex.Match(lower, "content-length:\\s*(\\d+)");
            if (clm.Success) long.TryParse(clm.Groups[1].Value, out cl);

            if (cl >= 0)
            {
                // rest 是 body 开头（读响应头时已多读的字节），写出一次后补齐剩余 Content-Length 字节
                if (rest != null && rest.Length > 0)
                    await WriteToClientAsync(st, rest, 0, rest.Length);
                var remaining = cl - (rest?.Length ?? 0);
                if (remaining > 0) await CopyExactlyToClientAsync(src, st, remaining);
                return;
            }

            if (lower.Contains("transfer-encoding: chunked"))
            {
                // rest 交给 chunked 解析器统一消费写出（绝不能在写头时重复写出）
                await PumpChunkedToClientAsync(src, st, rest ?? Array.Empty<byte>());
                return;
            }

            // 无长度头：EOF 界定（上游会关闭连接）；rest 是 body 开头，先写出再持续转发
            if (rest != null && rest.Length > 0)
                await WriteToClientAsync(st, rest, 0, rest.Length);
            await PumpToClientAsync(src, st);
        }

        /// <summary>经连接 Gate 向浏览器写入并刷新（与管控恢复注入互斥）。</summary>
        static async Task WriteToClientAsync(LiveConn st, byte[] data) => await WriteToClientAsync(st, data, 0, data.Length);

        static async Task WriteToClientAsync(LiveConn st, byte[] data, int offset, int count)
        {
            await st.Gate.WaitAsync();
            try
            {
                await st.Stream.WriteAsync(data, offset, count);
                await st.Stream.FlushAsync();
            }
            finally { st.Gate.Release(); }
        }

        async Task CopyExactlyToClientAsync(Stream src, LiveConn st, long count)
        {
            var buf = new byte[16384];
            while (count > 0)
            {
                var n = await src.ReadAsync(buf, 0, (int)Math.Min(buf.Length, count));
                if (n <= 0) return;
                await WriteToClientAsync(st, buf, 0, n);
                count -= n;
            }
        }

        /// <summary>
        /// chunked 响应体转发：解析并原样转发各 chunk，读到 0 终止块（含 trailer）后结束。
        /// 数据源统一为 FillAsync（rest 优先、精确 count），行读取逐字节、数据拷贝按 size 精确取，
        /// 不存在预读缓冲与数据拷贝的游标竞争，避免 chunk 错位导致浏览器 ERR_INCOMPLETE_CHUNKED_ENCODING。
        /// </summary>
        async Task PumpChunkedToClientAsync(Stream src, LiveConn st, byte[] rest)
        {
            int pos = 0; // rest 消费位置

            async Task<int> FillAsync(byte[] buffer, int offset, int count)
            {
                if (pos < rest.Length)
                {
                    var take = Math.Min(rest.Length - pos, count);
                    Array.Copy(rest, pos, buffer, offset, take);
                    pos += take;
                    return take;
                }
                return await src.ReadAsync(buffer, offset, count);
            }

            async Task<string> ReadLineAsync()
            {
                var sb = new System.Text.StringBuilder();
                var one = new byte[1];
                while (true)
                {
                    var n = await FillAsync(one, 0, 1);
                    if (n <= 0) return null;
                    var b = one[0];
                    if (b == 10)
                    {
                        if (sb.Length > 0 && sb[sb.Length - 1] == '\r') sb.Length--;
                        return sb.ToString();
                    }
                    sb.Append((char)b);
                }
            }

            // 精确取 size 字节：rest 优先，否则从上游读；一次不多读
            async Task<bool> CopyChunkDataAsync(long size)
            {
                var buf = new byte[16384];
                var remaining = size;
                while (remaining > 0)
                {
                    var n = await FillAsync(buf, 0, (int)Math.Min(buf.Length, remaining));
                    if (n <= 0) return false;
                    await WriteToClientAsync(st, buf, 0, n);
                    remaining -= n;
                }
                return true;
            }

            var totalWritten = 0L;
            while (true)
            {
                var line = await ReadLineAsync();
                if (line == null) return;
                var hex = line.Trim();
                var semi = hex.IndexOf(';');
                if (semi >= 0) hex = hex.Substring(0, semi).Trim();
                if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var size))
                    return;
                var lb = Encoding.ASCII.GetBytes(line + "\r\n");
                await WriteToClientAsync(st, lb);
                totalWritten += lb.Length;
                if (size == 0)
                {
                    // trailer 直到空行
                    while (true)
                    {
                        var tl = await ReadLineAsync();
                        if (tl == null) return;
                        var tb = Encoding.ASCII.GetBytes(tl + "\r\n");
                        await WriteToClientAsync(st, tb);
                        totalWritten += tb.Length;
                        if (tl.Length == 0) return;
                    }
                }
                if (!await CopyChunkDataAsync(size)) return;
                totalWritten += size;
                if (await ReadLineAsync() == null) return; // 消费 chunk 后 CRLF
                var cb = Encoding.ASCII.GetBytes("\r\n");
                await WriteToClientAsync(st, cb);
                totalWritten += cb.Length;
            }
        }

        /// <summary>EOF 界定响应：上游数据持续转发到浏览器（经 Gate 串行化）。</summary>
        async Task PumpToClientAsync(Stream src, LiveConn st)
        {
            var buf = new byte[16384];
            try
            {
                int n;
                while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
                {
                    await st.Gate.WaitAsync();
                    try
                    {
                        await st.Stream.WriteAsync(buf, 0, n);
                        await st.Stream.FlushAsync();
                    }
                    finally { st.Gate.Release(); }
                }
            }
            catch { }
        }

        /// <summary>按响应头判断是否为下载，并对照下载策略决定是否拦截。</summary>
        bool DownloadResponseBlocked(string head, out string reason)
        {
            reason = null;
            var lower = head.ToLowerInvariant();
            string fileName = null;
            var isDownload = false;

            var dispIdx = lower.IndexOf("content-disposition:");
            if (dispIdx >= 0)
            {
                var lineEnd = lower.IndexOf("\r\n", dispIdx);
                var disp = lineEnd > 0 ? head.Substring(dispIdx, lineEnd - dispIdx) : head.Substring(dispIdx);
                var fn = Regex.Match(disp, "filename\\s*=\\s*\"?([^\";\\r\\n]+)", RegexOptions.IgnoreCase);
                if (fn.Success) fileName = fn.Groups[1].Value.Trim();
                isDownload = disp.Contains("attachment", StringComparison.OrdinalIgnoreCase);
                if (!isDownload) return false; // inline 预览不算下载
            }
            else
            {
                var ctIdx = lower.IndexOf("content-type:");
                if (ctIdx < 0) return false;
                var lineEnd = lower.IndexOf("\r\n", ctIdx);
                var ct = (lineEnd > 0 ? lower.Substring(ctIdx, lineEnd - ctIdx) : lower.Substring(ctIdx)).ToLowerInvariant();
                isDownload = ct.Contains("application/octet-stream") || ct.Contains("application/zip") ||
                             ct.Contains("x-msdownload") || ct.Contains("application/x-rar") ||
                             ct.Contains("application/x-7z") || ct.Contains("application/x-gzip") ||
                             ct.Contains("application/x-tar") || ct.Contains("application/x-msdownload");
                if (!isDownload) return false;
            }

            if (!_policy.IsDownloadAllowed(fileName ?? "download.bin", out reason))
                return true;

            if (_policy.Download.MaxSizeMB > 0)
            {
                var cl = Regex.Match(lower, "content-length:\\s*(\\d+)");
                if (cl.Success && long.TryParse(cl.Groups[1].Value, out var len) &&
                    len > _policy.Download.MaxSizeMB * 1024L * 1024L)
                {
                    reason = "超过单文件上限 " + _policy.Download.MaxSizeMB + " MB";
                    return true;
                }
            }
            return false;
        }

        static async Task PumpAsync(Stream src, Stream dst)
        {
            var buf = new byte[16384];
            try
            {
                int n;
                while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
                {
                    await dst.WriteAsync(buf, 0, n);
                    await dst.FlushAsync();
                }
            }
            catch { }
        }

        static async Task WriteAsciiAsync(Stream stream, string s)
        {
            var b = Encoding.ASCII.GetBytes(s);
            await stream.WriteAsync(b, 0, b.Length);
            await stream.FlushAsync();
        }

        /// <summary>HTTPS 拦截：应答 CONNECT 成功 → 用 CA 签发的站点证书完成 TLS 握手 → 返回拦截页。</summary>
        async Task WriteBlockedHttpsAsync(Stream stream, string host)
        {
            try
            {
                await WriteAsciiAsync(stream, "HTTP/1.1 200 Connection Established\r\n\r\n");
                using var ssl = new SslStream(stream, false);
                ssl.ReadTimeout = 10000;
                ssl.WriteTimeout = 10000;
                var cert = _certMgr.GetSiteCertificate(host);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = cert,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                });
                var (head, rest) = await ReadHeadAsync(ssl);
                if (head == null) return;
                await WriteBlockedAsync(ssl, host);
            }
            catch
            {
                // 握手失败（如浏览器不信任/异常客户端）→ 按原拒绝处理
            }
        }

        async Task WriteBlockedAsync(Stream stream, string host)
        {
            var unlockArea = string.IsNullOrWhiteSpace(_policy.UnlockPassword) ? NoUnlockHtml : UnlockFormHtml;
            var body = Encoding.UTF8.GetBytes(string.Format(BlockedHtml,
                System.Net.WebUtility.HtmlEncode(host), unlockArea));
            var header = "HTTP/1.1 403 Forbidden\r\n" +
                         "Content-Type: text/html; charset=utf-8\r\n" +
                         "Content-Length: " + body.Length + "\r\n" +
                         "Connection: close\r\n" +
                         "Cache-Control: no-store\r\n\r\n";
            var head = Encoding.ASCII.GetBytes(header);
            await stream.WriteAsync(head, 0, head.Length);
            await stream.WriteAsync(body, 0, body.Length);
            await stream.FlushAsync();
        }
    }
}
