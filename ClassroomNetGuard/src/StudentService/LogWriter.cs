using System;
using System.IO;

namespace StudentService
{
    /// <summary>
    /// 学生端本地日志。优先写 %ProgramData%\NetGuard\logs；
    /// 若当前用户无写权限（如普通用户前台调试），自动回退到 %LOCALAPPDATA%\NetGuard\logs。
    /// 回退判定以实际写入失败为准，避免"可建目录但不可写文件"的假成功。
    /// </summary>
    public static class LogWriter
    {
        static readonly object Sync = new object();
        static string _dir;              // 当前实际使用的日志目录
        static bool _resolved;           // 是否已完成首次解析
        static bool _fallbackTried;

        public static string LogDirectory
        {
            get
            {
                lock (Sync) { Resolve(); return _dir; }
            }
        }

        public static void Info(string msg) => Write("INFO", msg);
        public static void Warn(string msg) => Write("WARN", msg);
        public static void Error(string msg) => Write("ERROR", msg);

        static void Write(string level, string msg)
        {
            try
            {
                lock (Sync)
                {
                    Resolve();
                    if (TryAppend(_dir, level, msg)) return;
                    if (_fallbackTried) return;
                    _fallbackTried = true;
                    var fb = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "NetGuard", "logs");
                    try { Directory.CreateDirectory(fb); } catch { return; }
                    _dir = fb;
                    TryAppend(_dir, level, msg);
                }
            }
            catch { }
        }

        static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            var primary = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetGuard", "logs");
            try { Directory.CreateDirectory(primary); _dir = primary; }
            catch { _dir = null; }
        }

        static bool TryAppend(string dir, string level, string msg)
        {
            try
            {
                var file = Path.Combine(dir, "student-service-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                File.AppendAllText(file,
                    $"[{DateTime.Now:HH:mm:ss}] [{level}] {msg}{Environment.NewLine}");
                return true;
            }
            catch { return false; }
        }
    }
}
