using System;
using System.IO;
using System.Text.Json;

namespace StudentService
{
    /// <summary>
    /// 学生端配置：%ProgramData%\NetGuard\config.json。
    /// teacherHost 留空时，服务启动会通过 UDP 广播自动发现教师机（教室即插即用）；
    /// 指定 IP 时直接使用该地址（跨网段或多教室时用）。
    /// </summary>
    public sealed class ServiceConfig
    {
        public string TeacherHost { get; set; } = "";
        public int TeacherPort { get; set; } = ClassroomNetGuard.Shared.NetGuardConstants.TeacherTcpPort;
        public int ProxyPort { get; set; } = ClassroomNetGuard.Shared.NetGuardConstants.StudentProxyPort;
        public int LocalApiPort { get; set; } = ClassroomNetGuard.Shared.NetGuardConstants.LocalApiPort;

        static readonly string _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetGuard");
        static readonly string _filePath = Path.Combine(_dir, "config.json");

        public static ServiceConfig Load()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    var cfg = JsonSerializer.Deserialize<ServiceConfig>(File.ReadAllText(_filePath));
                    if (cfg != null) return cfg;
                }
            }
            catch { }
            return new ServiceConfig();
        }
    }
}
