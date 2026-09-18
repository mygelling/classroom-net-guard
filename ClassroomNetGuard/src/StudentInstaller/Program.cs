using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace StudentInstaller
{
    /// <summary>
    /// 上网管控学生端 自解压安装器（单文件）。
    /// 内嵌 student.zip（管控服务 + 托盘 + Edge/Chrome 扩展），
    /// 以管理员权限安装服务、设置托盘自启，并引导加载浏览器扩展。
    /// 支持 --extract-to &lt;dir&gt; 参数：仅解压程序包（供自检/手动部署）。
    /// </summary>
    internal static class Program
    {
        const string Dest = @"C:\Program Files\NetGuard";
        const string ServiceName = "NetGuardStudentService";
        const string ResourceName = "student.zip";

        static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            if (args.Length >= 2 && args[0] == "--extract-to")
                return ExtractOnly(args[1]);

            if (!IsAdmin())
            {
                Console.WriteLine("正在请求管理员权限，请在弹出窗口中选择“是”...");
                try
                {
                    var psi = new ProcessStartInfo(Environment.ProcessPath)
                    {
                        UseShellExecute = true,
                        Verb = "runas",
                        WorkingDirectory = Environment.CurrentDirectory
                    };
                    Process.Start(psi);
                }
                catch
                {
                    Console.WriteLine("[错误] 提权被取消，安装无法继续。");
                    Pause();
                    return 1;
                }
                return 0;
            }

            return Install();
        }

        static bool IsAdmin() =>
            new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        static int Install()
        {
            Console.WriteLine("========== 上网管控学生端 安装程序 ==========");
            Console.WriteLine("目标目录: " + Dest);
            try { Directory.CreateDirectory(Dest); }
            catch (Exception ex) { Console.WriteLine("[错误] 无法创建目录: " + ex.Message); Pause(); return 1; }

            Console.WriteLine("[1/4] 解压程序文件...");
            try
            {
                using var stream = OpenZipStream();
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                archive.ExtractToDirectory(Dest, true);
            }
            catch (Exception ex) { Console.WriteLine("[错误] 解压失败: " + ex.Message); Pause(); return 1; }

            var svcExe = Path.Combine(Dest, "StudentService.exe");
            if (!File.Exists(svcExe))
            {
                Console.WriteLine("[错误] 解压后未找到 StudentService.exe");
                Pause();
                return 1;
            }

            Console.WriteLine("[2/4] 安装管控服务...");
            RunSc("stop", ServiceName);
            RunSc("delete", ServiceName);
            int rc = RunSc("create", ServiceName, "binPath=", Quote(svcExe),
                "start=", "auto", "DisplayName=", "NetGuard Student Service");
            if (rc != 0)
            {
                Console.WriteLine("[错误] 服务创建失败 (sc create 返回 " + rc + ")");
                Pause();
                return 1;
            }
            RunSc("start", ServiceName);

            Console.WriteLine("[3/4] 设置托盘开机自启...");
            var tray = Path.Combine(Dest, "StudentTray.exe");
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true);
                k?.SetValue("NetGuardStudentTray", Quote(tray), RegistryValueKind.String);
            }
            catch (Exception ex) { Console.WriteLine("(托盘自启设置失败: " + ex.Message + ")"); }

            Console.WriteLine("[4/4] 打开 Edge 扩展页...");
            try
            {
                Process.Start(new ProcessStartInfo("msedge", "chrome://extensions") { UseShellExecute = true });
            }
            catch
            {
                Console.WriteLine("(未能自动打开 Edge，请手动打开 chrome://extensions)");
            }

            Console.WriteLine();
            Console.WriteLine("============================================================");
            Console.WriteLine("  上网管控学生端 安装完成！");
            Console.WriteLine("  - 管控服务  NetGuardStudentService  已安装并启动");
            Console.WriteLine("  - 托盘程序  已设置开机自启");
            Console.WriteLine("  - 程序目录  " + Dest);
            Console.WriteLine();
            Console.WriteLine("  最后一步（只需做一次）：请在 Edge 页面中");
            Console.WriteLine("    1. 打开右上角「开发人员模式」开关");
            Console.WriteLine("    2. 点击「加载解压缩的扩展」");
            Console.WriteLine("    3. 选择文件夹: " + Path.Combine(Dest, "extension"));
            Console.WriteLine("============================================================");
            Pause();
            return 0;
        }

        static int ExtractOnly(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                using var stream = OpenZipStream();
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                archive.ExtractToDirectory(dir, true);
                Console.WriteLine("OK: extracted to " + dir);
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[错误] " + ex.Message);
                return 1;
            }
        }

        static Stream OpenZipStream()
        {
            var asm = Assembly.GetExecutingAssembly();
            using var res = asm.GetManifestResourceStream(ResourceName)
                ?? throw new FileNotFoundException("内嵌 student.zip 不存在");
            var ms = new MemoryStream();
            res.CopyTo(ms);
            ms.Position = 0;
            return ms;
        }

        static int RunSc(params string[] args)
        {
            var psi = new ProcessStartInfo("sc.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            try
            {
                using var p = Process.Start(psi);
                if (p == null) return -1;
                if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return -1; }
                return p.ExitCode;
            }
            catch { return -1; }
        }

        static string Quote(string path) => "\"" + path + "\"";

        static void Pause()
        {
            Console.WriteLine();
            Console.WriteLine("按回车键退出...");
            Console.ReadLine();
        }
    }
}
