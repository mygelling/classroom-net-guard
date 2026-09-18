using System;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ClassroomNetGuard.Shared;

namespace StudentTray
{
    public sealed class StatusData
    {
        public bool Ok { get; set; }
        public bool Connected { get; set; }
        public string TeacherHost { get; set; }
        public int PolicyVersion { get; set; }
        public bool ClassroomOn { get; set; }
    }

    /// <summary>通过命名管道向管控服务查询状态。</summary>
    public static class PipeClient
    {
        static readonly JsonSerializerOptions Opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        public static StatusData GetStatus()
        {
            try
            {
                using var c = new NamedPipeClientStream(".", NetGuardConstants.StudentPipeName,
                    PipeDirection.InOut, PipeOptions.Asynchronous);
                c.Connect(1500);
                var req = Encoding.UTF8.GetBytes("status");
                c.Write(req, 0, req.Length);
                c.Flush();
                var buf = new byte[4096];
                var n = c.Read(buf, 0, buf.Length);
                if (n <= 0) return null;
                return JsonSerializer.Deserialize<StatusData>(Encoding.UTF8.GetString(buf, 0, n), Opts);
            }
            catch { return null; }
        }
    }
}
