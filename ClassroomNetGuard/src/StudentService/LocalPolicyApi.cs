using System;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ClassroomNetGuard.Shared;

namespace StudentService
{
    /// <summary>
    /// 本地策略 API（127.0.0.1 HTTP）：提供当前策略查询与网页解锁。
    /// 仅绑定回环地址，不对外网暴露。
    /// </summary>
    public sealed class LocalPolicyApi
    {
        readonly NetPolicy _policy;
        readonly UnlockState _unlock;
        HttpListener _listener;

        public LocalPolicyApi(NetPolicy policy, UnlockState unlock)
        {
            _policy = policy;
            _unlock = unlock;
        }

        public void Start(int port)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
            _listener.Start();
            _ = Task.Run(LoopAsync);
            LogWriter.Info("本地策略 API 已启动 http://127.0.0.1:" + port + "/policy");
        }

        public void Stop()
        {
            try { _listener?.Stop(); } catch { }
        }

        async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { break; }
                _ = Task.Run(() => HandleAsync(ctx));
            }
        }

        async Task HandleAsync(HttpListenerContext ctx)
        {
            try
            {
                ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
                var path = ctx.Request.Url.AbsolutePath;
                if (path == "/policy")
                {
                    var json = JsonSerializer.Serialize(_policy, JsonOpts.Options);
                    await WriteAsync(ctx, 200, "application/json", json);
                }
                else if (path == "/status")
                {
                    await WriteAsync(ctx, 200, "application/json", "{\"ok\":true}");
                }
                else if (path == "/unlock" && ctx.Request.HttpMethod == "POST")
                {
                    await HandleUnlockAsync(ctx);
                }
                else
                {
                    await WriteAsync(ctx, 404, "text/plain", "Not Found");
                }
            }
            catch { }
        }

        /// <summary>网页解锁：校验密码（教师端按设备设置），成功后临时放行 30 分钟。</summary>
        async Task HandleUnlockAsync(HttpListenerContext ctx)
        {
            string pwd = "";
            try
            {
                using var reader = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                // 兼容 form 编码（password=xxx）与 JSON（{"password":"xxx"}）
                if (body.StartsWith("{"))
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("password", out var el)) pwd = el.GetString() ?? "";
                }
                else
                {
                    var idx = body.IndexOf("password=", StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        var rest = body.Substring(idx + 9);
                        var amp = rest.IndexOf('&');
                        pwd = (amp >= 0 ? rest.Substring(0, amp) : rest);
                        pwd = System.Net.WebUtility.UrlDecode(pwd);
                    }
                }
            }
            catch { }

            var expected = _policy.UnlockPassword ?? "";
            if (expected.Length > 0 && string.Equals(pwd, expected, StringComparison.Ordinal))
            {
                _unlock.Unlock();
                LogWriter.Info("网页解锁成功，临时放行 " + (int)UnlockState.Duration.TotalMinutes + " 分钟");
                await WriteAsync(ctx, 200, "text/html; charset=utf-8", UnlockOkHtml);
            }
            else
            {
                LogWriter.Warn("网页解锁密码错误");
                await WriteAsync(ctx, 200, "text/html; charset=utf-8", UnlockFailHtml);
            }
        }

        const string UnlockOkHtml =
            "<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><title>解锁成功</title></head>" +
            "<body style=\"font-family:'Microsoft YaHei',sans-serif;background:#F0F9F0;display:flex;align-items:center;justify-content:center;height:100vh;margin:0\">" +
            "<div style=\"text-align:center;background:#fff;padding:40px 48px;border-radius:16px;box-shadow:0 8px 30px rgba(0,0,0,.08)\">" +
            "<div style=\"font-size:42px\">&#9989;</div>" +
            "<h2 style=\"font-size:20px;color:#0E7C66;margin:10px 0 6px\">解锁成功！</h2>" +
            "<p style=\"font-size:14px;color:#555;margin:0 0 8px\">本次临时放行 30 分钟，到期自动恢复课堂管控。</p>" +
            "<p style=\"font-size:13px;color:#888\">请关闭本页面，返回浏览器重新打开网页。</p>" +
            "</div></body></html>";

        const string UnlockFailHtml =
            "<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><title>密码错误</title></head>" +
            "<body style=\"font-family:'Microsoft YaHei',sans-serif;background:#FDEBEA;display:flex;align-items:center;justify-content:center;height:100vh;margin:0\">" +
            "<div style=\"text-align:center;background:#fff;padding:40px 48px;border-radius:16px;box-shadow:0 8px 30px rgba(0,0,0,.08)\">" +
            "<div style=\"font-size:42px\">&#10060;</div>" +
            "<h2 style=\"font-size:20px;color:#B91C1C;margin:10px 0 6px\">解锁密码错误</h2>" +
            "<p style=\"font-size:14px;color:#555;margin:0 0 8px\">请重新输入教师下发的解锁密码。</p>" +
            "<p style=\"font-size:13px;color:#888\">返回上一页重新输入，或联系教师。</p>" +
            "</div></body></html>";

        static async Task WriteAsync(HttpListenerContext ctx, int code, string type, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = code;
            ctx.Response.ContentType = type;
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }
    }
}
