using System;
using System.Collections.Generic;
using System.Linq;
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
    /// 前提：学生机 Edge 以 --remote-debugging-port=9222 启动（安装包已创建受控快捷方式）。
    /// Edge 未以调试模式运行时，此步骤静默跳过（退化为断开连接，学生刷新即拦截）。
    /// </summary>
    public static class EdgeReloader
    {
        const string ListUrl = "http://127.0.0.1:9222/json/list";

        public static void ReloadAll() => _ = ReloadAllAsync();

        static async Task ReloadAllAsync()
        {
            // 重试多次：覆盖 Edge 刚启动 / 调试端口尚未就绪的时序
            List<CdpTarget> targets = null;
            Exception lastErr = null;
            for (int i = 0; i < 4; i++)
            {
                try
                {
                    using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                    var json = await hc.GetStringAsync(ListUrl);
                    targets = JsonSerializer.Deserialize<List<CdpTarget>>(json);
                    break;
                }
                catch (Exception ex)
                {
                    lastErr = ex;
                    await Task.Delay(400);
                }
            }

            if (targets == null || targets.Count == 0)
            {
                // Edge 未以调试模式启动（未使用"Edge 学生浏览器"快捷方式）→ 自动刷新不可用
                LogWriter.Warn("管控恢复：Edge 调试端口不可用，已加载页面无法自动刷新（请确认学生机使用桌面'Edge 学生浏览器'打开网页）");
                return;
            }

            var pages = targets.Where(t => t != null && t.Type == "page" && !string.IsNullOrEmpty(t.WebSocketDebuggerUrl)).ToList();
            foreach (var t in pages)
            {
                _ = ReloadOneAsync(t.WebSocketDebuggerUrl);
            }
            LogWriter.Info("管控恢复：已向受控 Edge 发送页面自动刷新指令（页面数 " + pages.Count + "）");
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
