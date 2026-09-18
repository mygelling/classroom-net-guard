using System;
using System.IO;
using System.Text.Json;
using ClassroomNetGuard.Shared;

namespace TeacherConsole
{
    /// <summary>
    /// 教师端策略持久化。优先 %ProgramData%\NetGuard\teacher-policy.json（管理员运行）；
    /// 无权限时自动回退 %LOCALAPPDATA%\NetGuard\teacher-policy.json，读取时两者都尝试。
    /// </summary>
    public static class PolicyStore
    {
        public static NetPolicy Load()
        {
            foreach (var path in CandidatePaths())
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var p = JsonSerializer.Deserialize<NetPolicy>(File.ReadAllText(path), JsonOpts.Options);
                    if (p != null) return p;
                }
                catch { }
            }
            return NetPolicy.Default();
        }

        public static void Save(NetPolicy policy)
        {
            var dir = EnsureWritableDir();
            if (dir == null) return;
            try
            {
                File.WriteAllText(
                    Path.Combine(dir, "teacher-policy.json"),
                    JsonSerializer.Serialize(policy, JsonOpts.Options));
            }
            catch { }
        }

        static string[] CandidatePaths()
        {
            return new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "NetGuard", "teacher-policy.json"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NetGuard", "teacher-policy.json")
            };
        }

        static string EnsureWritableDir()
        {
            var primary = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetGuard");
            try
            {
                Directory.CreateDirectory(primary);
                return primary;
            }
            catch
            {
                var fallback = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetGuard");
                try { Directory.CreateDirectory(fallback); return fallback; }
                catch { return null; }
            }
        }
    }
}
