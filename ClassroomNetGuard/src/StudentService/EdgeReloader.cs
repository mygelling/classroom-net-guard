using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StudentService
{
    /// <summary>
    /// 受控 Edge 自动刷新器：教师端切回课堂管控（或解锁被撤销）时，
    /// 通过 Edge 远程调试端口（CDP）对已完全加载的页面执行 Page.reload(ignoreCache=true)，
    /// 使页面重新发起请求、重新走代理拦截判定——非白名单页面刷新后即显示"访问已被拦截"。
    /// 若学生用普通方式打开的 Edge（无调试端口）无法自动刷新：
    /// 强制结束这些未受控的 Edge 进程（页面立即关闭），学生重新打开 Edge 时即走受控入口。
    /// </summary>
    public static class EdgeReloader
    {
        const string ListUrl = "http://127.0.0.1:9222/json/list";

        public static void ReloadAll() => _ = ReloadAllAsync();

        static async Task ReloadAllAsync()
        {
            // 重试多次：覆盖 Edge 刚启动 / 调试端口尚未就绪的时序
            List<CdpTarget> targets = null;
            for (int i = 0; i < 4; i++)
            {
                try
                {
                    using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                    var json = await hc.GetStringAsync(ListUrl);
                    targets = JsonSerializer.Deserialize<List<CdpTarget>>(json);
                    break;
                }
                catch
                {
                    await Task.Delay(400);
                }
            }

            var pages = targets?
                .Where(t => t != null && t.Type == "page" && !string.IsNullOrEmpty(t.WebSocketDebuggerUrl) && !IsBlankUrl(t.Url))
                .ToList() ?? new List<CdpTarget>();

            if (pages.Count > 0)
            {
                foreach (var t in pages)
                {
                    _ = ReloadOneAsync(t.WebSocketDebuggerUrl);
                }
                LogWriter.Info("管控恢复：已向受控 Edge 发送页面自动刷新指令（页面数 " + pages.Count + "）");
                return;
            }

            // 受控 Edge 无页面 / 调试端口不可用：学生可能用普通 Edge 打开了网页。
            // 强制关闭未受控 Edge 进程（页面立即关闭），并提示重开 Edge 使用受控入口。
            var killed = KillUnmanagedEdge();
            if (targets != null)
                LogWriter.Warn("管控恢复：受控 Edge 上没有打开的页面，已强制关闭未受控 Edge 进程 " + killed + " 个（学生重新打开 Edge 将使用受控模式）");
            else
                LogWriter.Warn("管控恢复：Edge 调试端口不可用，已强制关闭未受控 Edge 进程 " + killed + " 个（请学生使用桌面'Edge 学生浏览器'或重新打开 Edge）");
        }

        /// <summary>空白/起始页不视为真实页面（受控 Edge 仅开着新标签页时仍需处理未受控 Edge）。</summary>
        static bool IsBlankUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return true;
            var u = url.Trim().ToLowerInvariant();
            return u == "about:blank"
                || u.StartsWith("edge://newtab")
                || u.StartsWith("edge://start")
                || u.StartsWith("chrome://newtab")
                || u.StartsWith("about:newtab")
                || u.StartsWith("data:,");
        }

        /// <summary>结束未受控 Edge 的浏览器主进程（命令行不含 --remote-debugging-port 且非子进程），返回结束数量。
        /// 只杀主进程：子进程（--type=renderer 等）随主进程一并退出，避免误伤受控 Edge 的子进程。</summary>
        static int KillUnmanagedEdge()
        {
            int killed = 0;
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='msedge.exe'");
                var targets = new List<(int Pid, string Cmd)>();
                foreach (var o in searcher.Get())
                {
                    var cmd = o["CommandLine"] as string ?? "";
                    if (cmd.Contains("remote-debugging-port", StringComparison.OrdinalIgnoreCase)) continue; // 受控 Edge 主进程
                    if (cmd.Contains("--type=", StringComparison.Ordinal)) continue; // 子进程
                    targets.Add(((int)(uint)o["ProcessId"], cmd));
                }
                foreach (var t in targets)
                {
                    try
                    {
                        Process.GetProcessById(t.Pid).Kill();
                        killed++;
                    }
                    catch { }
                }
            }
            catch { }
            return killed;
        }

        static async Task ReloadOneAsync(string wsUrl)
        {
            try
            {
                using var ws = new ClientWebSocket();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await ws.ConnectAsync(new Uri(wsUrl), cts.Token);
                var msg = "{\"id\":1,\"method\":\"Page.reload\",\"params\":{\"ignoreCache\":true}}";
                var bytes = Encoding.UTF8.GetBytes(msg);
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);
            }
            catch { }
        }

        sealed class CdpTarget
        {
            public string Type { get; set; }
            public string Url { get; set; }
            public string WebSocketDebuggerUrl { get; set; }
        }
    }
}
