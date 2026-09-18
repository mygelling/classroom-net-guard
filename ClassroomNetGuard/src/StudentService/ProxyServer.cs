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
        // 活跃客户端连接跟踪：管控恢复（教师端切回课堂管控/解锁被撤销）时主动断开，
        // 学生端当前已打开的网页立即失效，刷新后重新走拦截判定。
        readonly ConcurrentDictionary<TcpClient, byte> _liveConns = new ConcurrentDictionary<TcpClient, byte>();
        System.Threading.Timer _unlockWatch;
        bool _lastRelaxed;

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
                // 任一解除（教师端切回课堂管控 / 解锁被撤销）→ 断开所有已建立连接，
                // 学生端当前打开的网页立即失效，刷新后重新走拦截判定。
                var relaxed = !_policy.ClassroomOn || _unlock.IsActive;
                if (_lastRelaxed && !relaxed)
                {
                    foreach (var c in _liveConns.Keys)
                    {
                        try { c.Close(); } catch { }
                    }
                    LogWriter.Info("管控恢复，断开全部活跃连接");
                }
                _lastRelaxed = relaxed;
            }
            catch { }
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
            _liveConns.TryAdd(client, 0);
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();

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
                _lastUrl = host;
                _conn.SetCurrentUrl(host);

                // 解锁期间（学生输对密码后 30 分钟）临时放行全部网站
                if (_policy.IsDomainAllowed(host, hp.port) || _unlock.IsActive)
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
                        var headBytes = Encoding.ASCII.GetBytes(head + "\r\n\r\n");
                        await up.WriteAsync(headBytes, 0, headBytes.Length);
                        if (rest != null && rest.Length > 0)
                            await up.WriteAsync(rest, 0, rest.Length);
                        await PumpHttpWithDownloadCheckAsync(stream, up, host);
                    }
                }
                else
                {
                    Interlocked.Increment(ref _blockedCount);
                    _conn.AddBlocked();
                    _ = _conn.SendLogAsync(Environment.MachineName, "访问 " + host, "拦截");
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

        // ===== 转发与响应 =====

        /// <summary>
        /// http 响应转发 + 下载管控（原浏览器扩展职责，现由代理承担）：
        /// 读响应头判定是否下载及是否符合下载策略，拦截则返回提示页，放行则继续转发。
        /// </summary>
        async Task PumpHttpWithDownloadCheckAsync(Stream dst, Stream src, string host)
        {
            var (head, rest) = await ReadHeadAsync(src);
            if (head == null) return;

            // 解锁期间下载管控一并放行（自由上网）
            if (!_unlock.IsActive && DownloadResponseBlocked(head, out var reason))
            {
                Interlocked.Increment(ref _blockedCount);
                _conn.AddBlocked();
                _ = _conn.SendLogAsync(Environment.MachineName, "下载 " + host, "拦截:" + reason);
                await WriteBlockedAsync(dst, host + "（下载被拦截：" + reason + "）");
                return;
            }

            var hb = Encoding.ASCII.GetBytes(head + "\r\n\r\n");
            await dst.WriteAsync(hb, 0, hb.Length);
            if (rest != null && rest.Length > 0)
                await dst.WriteAsync(rest, 0, rest.Length);

            var t1 = PumpAsync(src, dst);
            await t1;
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
