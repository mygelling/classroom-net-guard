using System;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StudentService
{
    /// <summary>
    /// 命名管道服务：供托盘程序查询服务状态（是否连接教师端、策略版本、管控模式等）。
    /// 协议：客户端写入 "status"，服务返回 JSON。
    /// </summary>
    public sealed class PipeServer
    {
        readonly TeacherConnection _conn;
        readonly CancellationTokenSource _cts = new CancellationTokenSource();

        public PipeServer(TeacherConnection conn)
        {
            _conn = conn;
        }

        public void Start() => _ = Task.Run(LoopAsync);

        public void Stop() => _cts.Cancel();

        async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        ClassroomNetGuard.Shared.NetGuardConstants.StudentPipeName,
                        PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(_cts.Token);
                    // 所有权转移给后台任务：绝不能在循环体结束时释放刚连接的管道，
                    // 否则客户端 Connect 成功但永远读不到响应（await using 会随作用域立即 Dispose）
                    var owned = server;
                    server = null;
                    _ = Task.Run(() => ServeAsync(owned));
                }
                catch
                {
                    if (server != null) { try { server.Dispose(); } catch { } }
                    break;
                }
            }
        }

        async Task ServeAsync(NamedPipeServerStream server)
        {
            try
            {
                var buf = new byte[4096];
                var n = await server.ReadAsync(buf, 0, buf.Length);
                var req = Encoding.UTF8.GetString(buf, 0, n).Trim();

                string resp;
                if (req == "status")
                {
                    resp = JsonSerializer.Serialize(new
                    {
                        ok = true,
                        connected = _conn.Connected,
                        teacherHost = _conn.TeacherHost,
                        policyVersion = _conn.Policy.Version,
                        classroomOn = _conn.Policy.ClassroomOn,
                        blocked = 0
                    });
                }
                else
                {
                    resp = "{\"ok\":false,\"error\":\"unknown-command\"}";
                }

                var bytes = Encoding.UTF8.GetBytes(resp);
                await server.WriteAsync(bytes, 0, bytes.Length);
            }
            catch { }
            finally
            {
                try { server.Dispose(); } catch { }
            }
        }
    }
}
