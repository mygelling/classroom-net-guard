using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ClassroomNetGuard.Shared;

namespace TeacherConsole
{
    /// <summary>一个学生端的 TCP 会话。</summary>
    public sealed class ClientSession
    {
        public TcpClient Client { get; }
        public DeviceViewModel Device { get; }
        readonly NetworkStream _stream;
        readonly object _sync = new object();
        public bool Closed { get; private set; }

        public ClientSession(TcpClient client, DeviceViewModel device)
        {
            Client = client;
            Device = device;
            _stream = client.GetStream();
        }

        public Task SendAsync(WireMessage msg)
        {
            lock (_sync)
            {
                if (Closed) return Task.CompletedTask;
                return FrameProtocol.WriteAsync(_stream, msg.Serialize());
            }
        }

        public Task<string> ReadFrameAsync() => FrameProtocol.ReadAsync(_stream);

        public void Close()
        {
            lock (_sync)
            {
                if (Closed) return;
                Closed = true;
                try { _stream.Close(); } catch { }
                try { Client.Close(); } catch { }
            }
        }
    }

    /// <summary>教师端 TCP 服务器：接受学生端连接、心跳保活、策略广播、日志上报。</summary>
    public sealed class TcpServer
    {
        public const int DefaultPort = 9999;
        const int OfflineTimeoutSeconds = 15;

        readonly int _port;
        TcpListener _listener;
        CancellationTokenSource _cts = new CancellationTokenSource();

        readonly ConcurrentDictionary<string, DeviceViewModel> _devices = new ConcurrentDictionary<string, DeviceViewModel>();
        readonly ConcurrentDictionary<string, ClientSession> _sessions = new ConcurrentDictionary<string, ClientSession>();

        public ConcurrentDictionary<string, ClientSession> Sessions => _sessions;

        public event Action<ClientSession> DeviceOnline;
        public event Action<DeviceViewModel> DeviceOffline;
        public event Action<LogData> LogReceived;
        public event Action<ClientSession, int> AckReceived;

        public TcpServer(int port = DefaultPort) { _port = port; }

        public async Task StartAsync()
        {
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start();
            _ = Task.Run(WatchdogAsync);
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(); }
                catch { break; }
                _ = Task.Run(() => HandleAsync(client));
            }
        }

        public void Stop()
        {
            _cts.Cancel();
            foreach (var kv in _sessions) kv.Value.Close();
            try { _listener?.Stop(); } catch { }
        }

        async Task HandleAsync(TcpClient client)
        {
            ClientSession session = null;
            string key = null;
            try
            {
                client.NoDelay = true;
                var first = await FrameProtocol.ReadAsync(client.GetStream());
                if (first == null) return;
                var msg = WireMessage.Parse(first);
                if (msg == null || msg.T != WireMessage.THello) return;
                var hello = msg.DataAs<HelloData>();

                key = (hello.Seat ?? "?") + "|" + (hello.Ip ?? "?");
                var device = _devices.GetOrAdd(key, _ => new DeviceViewModel());
                device.Seat = hello.Seat;
                device.Name = hello.Name;
                device.Ip = hello.Ip;
                device.LastSeen = DateTime.Now;
                device.Online = true;

                session = new ClientSession(client, device);
                _sessions[key] = session;
                DeviceOnline?.Invoke(session);

                while (true)
                {
                    var line = await session.ReadFrameAsync();
                    if (line == null) break;
                    await DispatchAsync(session, line);
                }
            }
            catch
            {
                // 连接异常，按断开处理
            }
            finally
            {
                if (key != null && session != null)
                {
                    _sessions.TryRemove(key, out _);
                    session.Close();
                    DeviceOffline?.Invoke(session.Device);
                }
                else
                {
                    try { client.Close(); } catch { }
                }
            }
        }

        async Task DispatchAsync(ClientSession s, string json)
        {
            var msg = WireMessage.Parse(json);
            if (msg == null) return;
            switch (msg.T)
            {
                case WireMessage.THeartbeat:
                    var hb = msg.DataAs<HeartbeatData>();
                    s.Device.LastSeen = DateTime.Now;
                    s.Device.Online = true;
                    s.Device.CurrentUrl = hb?.CurrentUrl ?? "";
                    if (hb != null && hb.BlockedCount > s.Device.BlockedCount) s.Device.BlockedCount = hb.BlockedCount;
                    break;
                case WireMessage.TLog:
                    LogReceived?.Invoke(msg.DataAs<LogData>());
                    break;
                case WireMessage.TAck:
                    var ack = msg.DataAs<AckData>();
                    if (ack != null) AckReceived?.Invoke(s, ack.Version);
                    break;
            }
        }

        /// <summary>向全部在线学生端广播消息（如策略更新）。</summary>
        public void Broadcast(WireMessage msg)
        {
            foreach (var kv in _sessions)
            {
                if (!kv.Value.Closed) _ = kv.Value.SendAsync(msg);
            }
        }

        /// <summary>心跳看门狗：超过 15 秒未收到心跳的设备标记为离线。</summary>
        async Task WatchdogAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(3000);
                foreach (var kv in _sessions)
                {
                    var dev = kv.Value.Device;
                    if (dev.Online && (DateTime.Now - dev.LastSeen).TotalSeconds > OfflineTimeoutSeconds)
                    {
                        dev.Online = false;
                    }
                }
            }
        }
    }
}
