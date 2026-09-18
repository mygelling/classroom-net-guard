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
            try
            {
                using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var json = await hc.GetStringAsync(ListUrl);
                var targets = JsonSerializer.Deserialize<List<CdpTarget>>(json);
                if (targets == null) return;
                foreach (var t in targets.Where(t => t != null && t.Type == "page" && !string.IsNullOrEmpty(t.WebSocketDebuggerUrl)))
                {
                    _ = ReloadOneAsync(t.WebSocketDebuggerUrl);
                }
                if (targets.Any(t => t != null && t.Type == "page"))
                    LogWriter.Info("管控恢复：已向受控 Edge 发送页面自动刷新指令");
            }
            catch
            {
                // Edge 未以调试模式启动（未使用受控快捷方式）→ 自动刷新不可用，静默跳过
            }
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
