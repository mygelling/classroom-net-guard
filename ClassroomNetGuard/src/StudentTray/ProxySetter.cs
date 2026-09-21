using System;
using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace StudentTray
{
    /// <summary>
    /// 系统代理设置：把当前用户（学生）的 WinINET 代理指向本地管控代理 127.0.0.1:8888，
    /// 使所有浏览器流量都经过白名单过滤。退出管控时恢复用户原代理设置。
    /// </summary>
    public static class ProxySetter
    {
        const string SubKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
        const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
        const int INTERNET_OPTION_REFRESH = 37;

        [DllImport("wininet.dll", SetLastError = true)]
        static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        static (bool enabled, string server, string overrides) _saved;
        static bool _savedOnce;

        public static void Enable(string proxyAddress)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(SubKey, true))
            {
                if (k == null) return;
                if (!_savedOnce)
                {
                    _saved = (
                        Convert.ToInt32(k.GetValue("ProxyEnable", 0)) != 0,
                        (string)k.GetValue("ProxyServer", "") ?? "",
                        (string)k.GetValue("ProxyOverride", "") ?? "");
                    _savedOnce = true;
                }
                k.SetValue("ProxyEnable", 1);
                k.SetValue("ProxyServer", proxyAddress);
                // 仅本机回环直连（本地策略 API/解锁页面），其余全部走代理——
                // 不能使用 <local>（否则所有局域网地址直连、白名单对内网完全失效）
                k.SetValue("ProxyOverride", "127.0.0.1;localhost");
            }
            Refresh();
        }

        /// <summary>检查当前系统代理是否仍指向本地管控代理（防学生手动关闭/篡改代理导致绕过白名单）。</summary>
        public static bool IsEnabled(string proxyAddress)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(SubKey, false))
                {
                    if (k == null) return false;
                    var en = Convert.ToInt32(k.GetValue("ProxyEnable", 0)) != 0;
                    var server = ((string)k.GetValue("ProxyServer", "") ?? "").Trim();
                    var over = ((string)k.GetValue("ProxyOverride", "") ?? "").Trim();
                    return en && server.Equals(proxyAddress, StringComparison.OrdinalIgnoreCase)
                        && over.Contains("127.0.0.1");
                }
            }
            catch { return false; }
        }

        public static void Restore()
        {
            if (!_savedOnce) return;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(SubKey, true))
                {
                    if (k == null) return;
                    k.SetValue("ProxyEnable", _saved.enabled ? 1 : 0);
                    k.SetValue("ProxyServer", _saved.server);
                    k.SetValue("ProxyOverride", _saved.overrides);
                }
                Refresh();
            }
            catch { }
        }

        static void Refresh()
        {
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
        }
    }
}
