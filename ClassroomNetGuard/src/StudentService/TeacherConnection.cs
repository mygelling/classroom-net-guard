using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClassroomNetGuard.Shared;

namespace StudentService
{
    /// <summary>
    /// 学生端与教师端的连接：UDP 自动发现教师机 → TCP 连接 → 心跳保活 →
    /// 接收策略 → 断线重连（退避 1s~30s）。断线期间沿用最后策略（离线兜底默认拦截）。
    /// </summary>
    public sealed class TeacherConnection
    {
        readonly ServiceConfig _cfg;
        readonly UnlockState _unlock;
        readonly CancellationTokenSource _cts = new CancellationTokenSource();
        TcpClient _client;
        Task _loop;
        long _tick;
        volatile string _currentUrl = "";
        volatile int _blocked;

        public NetPolicy Policy { get; private set; } = NetPolicy.Default();
        public bool Connected { get; private set; }
        public string TeacherHost { get; private set; } = "";
        public string Seat => Environment.MachineName;
        public string StudentName => Environment.UserName;

        public event Action OnConnectionChanged;

        public TeacherConnection(ServiceConfig cfg, UnlockState unlock)
        {
            _cfg = cfg;
            _unlock = unlock;
        }

        /// <summary>由代理记录当前访问域名（随心跳上报教师端）。</summary>
        public void SetCurrentUrl(string url) => _currentUrl = url ?? "";

        /// <summary>由代理记录拦截次数。</summary>
        public void AddBlocked() => Interlocked.Increment(ref _blocked);

        /// <summary>
        /// 把新策略复制进共享 Policy 实例（不能整体替换引用！）——
        /// 代理/策略 API 在启动时持有的是同一对象的引用，替换引用会让它们永远读到旧策略。
        /// </summary>
        void ApplyPolicy(NetPolicy p)
        {
            Policy.Version = p.Version;
            Policy.ClassroomOn = p.ClassroomOn;
            Policy.AllowDomains = p.AllowDomains ?? new List<string>();
            Policy.AllowResourceDomains = p.AllowResourceDomains ?? new List<string>();
            Policy.ReferrerAllowEnabled = p.ReferrerAllowEnabled;
            Policy.Download = p.Download ?? new DownloadPolicy();
            Policy.UnlockPassword = p.UnlockPassword ?? "";
            Policy.UpdatedAt = p.UpdatedAt;
            // 教师端下发任何新策略都立即撤销网页解锁放行，恢复课堂管控；
            // 已建立的网页连接由代理侧主动断开，学生刷新后重新走拦截判定。
            _unlock.Lock();
        }

        public void Start() => _loop = Task.Run(LoopAsync);

        public void Stop()
        {
            _cts.Cancel();
            try { _client?.Close(); } catch { }
        }

        async Task LoopAsync()
        {
            var delayMs = 1000;
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var host = await ResolveTeacherHostAsync();
                    if (string.IsNullOrEmpty(host)) { await DelayAsync(delayMs); continue; }
                    TeacherHost = host;

                    using var client = new TcpClient { NoDelay = true };
                    _client = client;
                    await client.ConnectAsync(host, _cfg.TeacherPort);
                    var stream = client.GetStream();

                    await FrameProtocol.WriteAsync(stream, WireMessage.Create(WireMessage.THello, new HelloData
                    {
                        Seat = Seat,
                        Name = StudentName,
                        Ip = GetLocalIp(),
                        Os = Environment.OSVersion.VersionString
                    }).Serialize());

                    Connected = true;
                    delayMs = 1000;
                    LogWriter.Info("已连接教师端 " + host + ":" + _cfg.TeacherPort);
                    OnConnectionChanged?.Invoke();

                    var recv = Task.Run(() => RecvLoopAsync(client, stream));
                    var hb = Task.Run(() => HeartbeatLoopAsync(client, stream));
                    await Task.WhenAny(recv, hb);

                    Connected = false;
                    OnConnectionChanged?.Invoke();
                    LogWriter.Warn("与教师端连接断开，稍后重连");
                }
                catch (Exception ex)
                {
                    Connected = false;
                    LogWriter.Warn("连接教师端失败：" + ex.Message);
                }
                await DelayAsync(delayMs);
                delayMs = Math.Min(delayMs * 2, 30000);
            }
        }

        async Task RecvLoopAsync(TcpClient client, NetworkStream stream)
        {
            while (true)
            {
                string line;
                try { line = await FrameProtocol.ReadAsync(stream); }
                catch { return; }
                if (line == null) return;

                var msg = WireMessage.Parse(line);
                if (msg == null) continue;
                switch (msg.T)
                {
                    case WireMessage.TPolicy:
                        var p = msg.DataAs<NetPolicy>();
                        if (p != null)
                        {
                            ApplyPolicy(p);
                            // 收到新策略后本地直接应用，不再向教师端回 ack——
                            // 避免大量学生机同时收到策略后集中回执造成教师机网络堵塞
                            LogWriter.Info("收到新策略 v" + Policy.Version + (Policy.ClassroomOn ? "（课堂管控）" : "（自由模式）"));
                        }
                        break;

                    case WireMessage.TCmd:
                        var cmd = msg.DataAs<CmdData>();
                        if (cmd != null && cmd.Action == "lock")
                        {
                            _unlock.Lock();
                            LogWriter.Info("收到教师端手动重新拦截指令");
                            _ = SendLogAsync(Environment.MachineName, "UNLOCK:OFF", "教师端手动重新拦截");
                        }
                        break;
                }
            }
        }

        async Task HeartbeatLoopAsync(TcpClient client, NetworkStream stream)
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(5000);
                try
                {
                    if (!client.Connected) return;
                    var hb = new HeartbeatData
                    {
                        Tick = ++_tick,
                        CurrentUrl = _currentUrl,
                        BlockedCount = _blocked,
                        PolicyVersion = Policy.Version
                    };
                    await FrameProtocol.WriteAsync(stream, WireMessage.Create(WireMessage.THeartbeat, hb).Serialize());
                }
                catch { return; }
            }
        }

        /// <summary>上报一条访问/下载日志到教师端。</summary>
        public async Task SendLogAsync(string device, string evt, string result)
        {
            if (!Connected || _client == null) return;
            try
            {
                var s = _client.GetStream();
                await FrameProtocol.WriteAsync(s, WireMessage.Create(WireMessage.TLog,
                    new LogData { Device = device, Event = evt, Result = result }).Serialize());
            }
            catch { }
        }

        // ===== 教师机发现 =====

        async Task<string> ResolveTeacherHostAsync()
        {
            if (!string.IsNullOrWhiteSpace(_cfg.TeacherHost)) return _cfg.TeacherHost.Trim();
            var found = await DiscoverTeacherAsync();
            if (!string.IsNullOrEmpty(found)) return found;
            return "127.0.0.1"; // 本机调试兜底
        }

        static async Task<string> DiscoverTeacherAsync()
        {
            try
            {
                using var udp = new UdpClient();
                udp.EnableBroadcast = true;
                var magic = Encoding.UTF8.GetBytes(NetGuardConstants.DiscoverMagic);
                await udp.SendAsync(magic, magic.Length,
                    new IPEndPoint(IPAddress.Broadcast, NetGuardConstants.DiscoveryPort));
                var recv = udp.ReceiveAsync();
                var done = await Task.WhenAny(recv, Task.Delay(1500));
                if (done != recv) return null;
                var result = recv.Result;
                var text = Encoding.UTF8.GetString(result.Buffer);
                if (text.Contains(NetGuardConstants.TeacherMagic))
                    return result.RemoteEndPoint.Address.ToString();
            }
            catch { }
            return null;
        }

        static string GetLocalIp()
        {
            try
            {
                return Dns.GetHostAddresses(Dns.GetHostName())
                    .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    ?.ToString() ?? "127.0.0.1";
            }
            catch { return "127.0.0.1"; }
        }

        async Task DelayAsync(int ms)
        {
            try { await Task.Delay(ms, _cts.Token); } catch { }
        }
    }
}
