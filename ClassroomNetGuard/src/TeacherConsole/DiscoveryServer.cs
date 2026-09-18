using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace TeacherConsole
{
    /// <summary>
    /// UDP 发现服务：监听 9998 端口，学生端广播 "NETGUARD_DISCOVER" 时
    /// 回包 "NETGUARD_TEACHER"，让学生端自动找到教师机 IP（教室即插即用）。
    /// </summary>
    public sealed class DiscoveryServer
    {
        public const int DiscoveryPort = 9998;
        const string DiscoverMagic = "NETGUARD_DISCOVER";
        const string TeacherMagic = "NETGUARD_TEACHER";

        UdpClient _udp;
        readonly object _sync = new object();

        public void Start()
        {
            lock (_sync)
            {
                if (_udp != null) return;
                _udp = new UdpClient(DiscoveryPort);
                _ = Task.Run(LoopAsync);
            }
        }

        public void Stop()
        {
            lock (_sync)
            {
                try { _udp?.Close(); } catch { }
                _udp = null;
            }
        }

        async Task LoopAsync()
        {
            UdpClient udp;
            lock (_sync) udp = _udp;
            if (udp == null) return;
            while (true)
            {
                UdpReceiveResult result;
                try
                {
                    result = await udp.ReceiveAsync();
                }
                catch
                {
                    break; // 已停止
                }
                var text = Encoding.UTF8.GetString(result.Buffer);
                if (text.Contains(DiscoverMagic))
                {
                    var resp = Encoding.UTF8.GetBytes(TeacherMagic);
                    try { await udp.SendAsync(resp, resp.Length, result.RemoteEndPoint); } catch { }
                }
            }
        }
    }
}
