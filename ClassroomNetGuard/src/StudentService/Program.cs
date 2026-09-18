using System;
using System.ServiceProcess;

namespace StudentService
{
    internal static class Program
    {
        static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--console")
            {
                // 前台调试模式：方便在开发机直接运行观察日志
                Console.WriteLine("===== 上网管控学生端服务（前台调试模式）=====");
                var svc = new StudentControlService();
                svc.StartDebug();
                Console.WriteLine("服务已启动。日志：%ProgramData%\\NetGuard\\logs");
                Console.WriteLine("按 Ctrl+C 退出...");
                var wait = new ManualResetEventSlim(false);
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; wait.Set(); };
                wait.Wait();
                svc.StopDebug();
                return;
            }

            ServiceBase.Run(new ServiceBase[] { new StudentControlService() });
        }
    }
}
