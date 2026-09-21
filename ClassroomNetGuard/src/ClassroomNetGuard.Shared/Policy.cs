using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassroomNetGuard.Shared
{
    /// <summary>下载策略：Enabled=true 表示允许下载（按类型/大小放行），false 表示全局禁止。</summary>
    public sealed class DownloadPolicy
    {
        public bool Enabled { get; set; }
        public List<string> AllowTypes { get; set; } = new List<string> { "pdf", "docx", "pptx" };
        public int MaxSizeMB { get; set; } = 50; // 0 表示不限

        public DownloadPolicy Clone() => new DownloadPolicy
        {
            Enabled = Enabled,
            AllowTypes = AllowTypes.ToList(),
            MaxSizeMB = MaxSizeMB
        };
    }

    /// <summary>白名单管控策略（教师端下发、学生端执行）。</summary>
    public sealed class NetPolicy
    {
        public int Version { get; set; } = 1;
        /// <summary>true=课堂管控（白名单生效）；false=自由模式（全部放行）。</summary>
        public bool ClassroomOn { get; set; } = true;
        /// <summary>白名单域名，支持 *.example.com 通配符。</summary>
        public List<string> AllowDomains { get; set; } = new List<string>();
        /// <summary>资源放行域名：白名单页面加载的第三方资源（CDN 图片/JS/字体、接口域名等）也放行。格式与白名单一致。</summary>
        public List<string> AllowResourceDomains { get; set; } = new List<string>();
        /// <summary>true=放行白名单页面引用的第三方资源（请求带 Referer/Origin 且来源是白名单/资源域名时放行）；false=严格白名单。</summary>
        public bool ReferrerAllowEnabled { get; set; } = true;
        /// <summary>按设备（座位号）单独设置的模式：seat → 是否课堂管控。仅教师端本地使用，不下发给学生端。</summary>
        public Dictionary<string, bool> DeviceModes { get; set; } = new Dictionary<string, bool>();
        /// <summary>按设备（座位号）单独设置的网页解锁密码：seat → 密码。仅教师端本地使用，不下发给学生端。</summary>
        public Dictionary<string, string> DevicePasswords { get; set; } = new Dictionary<string, string>();
        /// <summary>单设备下发专用：该学生机的解锁密码（教师端按设备注入，全局策略中为空）。</summary>
        public string UnlockPassword { get; set; } = "";
        public DownloadPolicy Download { get; set; } = new DownloadPolicy();
        public DateTime UpdatedAt { get; set; } = DateTime.Now;

        public static NetPolicy Default() => new NetPolicy
        {
            Version = 1,
            ClassroomOn = true,
            AllowDomains = new List<string>(), // 纯空白名单：默认全部拦截，由教师端按需添加
            AllowResourceDomains = new List<string>(),
            ReferrerAllowEnabled = true,
            Download = new DownloadPolicy { Enabled = false, AllowTypes = new List<string> { "pdf", "docx", "pptx" }, MaxSizeMB = 50 }
        };

        public NetPolicy Clone() => new NetPolicy
        {
            Version = Version,
            ClassroomOn = ClassroomOn,
            AllowDomains = AllowDomains.ToList(),
            AllowResourceDomains = AllowResourceDomains == null ? new List<string>() : AllowResourceDomains.ToList(),
            ReferrerAllowEnabled = ReferrerAllowEnabled,
            DeviceModes = DeviceModes == null ? null : new Dictionary<string, bool>(DeviceModes),
            DevicePasswords = DevicePasswords == null ? null : new Dictionary<string, string>(DevicePasswords),
            UnlockPassword = UnlockPassword,
            Download = Download.Clone(),
            UpdatedAt = UpdatedAt
        };

        /// <summary>放行判定：支持域名（baidu.com 含子域）、通配域（*.edu.cn）、
        /// IP 地址（192.168.1.50）、IP:端口（192.168.1.50:8000，仅放行该端口）、
        /// IP 段通配（192.168.1.*）、CIDR（192.168.1.0/24）。自由模式下全部放行。</summary>
        public bool IsDomainAllowed(string host, int port = 0)
        {
            if (!ClassroomOn) return true;
            return MatchRuleList(AllowDomains, host, port);
        }

        /// <summary>资源放行域名判定：与白名单相同格式。自由模式下全部放行。</summary>
        public bool IsResourceDomainAllowed(string host, int port = 0)
        {
            if (!ClassroomOn) return true;
            return MatchRuleList(AllowResourceDomains, host, port);
        }

        /// <summary>来源关联放行：请求的 Referer/Origin 指向的域名是白名单或资源放行域名时放行。
        /// 用于白名单页面加载的第三方子资源（图片/JS/接口/字体等）。带端口匹配，兼容带端口白名单条目。</summary>
        public bool IsReferrerAllowed(string refererOrOrigin)
        {
            if (!ClassroomOn) return true;
            var (host, port) = ExtractHostPort(refererOrOrigin);
            if (host == null) return false;
            return MatchRuleList(AllowDomains, host, port) || MatchRuleList(AllowResourceDomains, host, port);
        }

        /// <summary>从 Referer / Origin 值提取主机名（小写）与端口（无端口为 0），无法解析时 host 为 null。</summary>
        public static (string host, int port) ExtractHostPort(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return (null, 0);
            var v = value.Trim();
            if (v.Equals("null", StringComparison.OrdinalIgnoreCase)) return (null, 0);
            var idx = v.IndexOf("://", StringComparison.Ordinal);
            if (idx >= 0) v = v.Substring(idx + 3);
            var slash = v.IndexOfAny(new[] { '/', '?' });
            if (slash >= 0) v = v.Substring(0, slash);
            if (v.Length == 0) return (null, 0);
            var (host, port) = SplitHostPort(v.ToLowerInvariant());
            if (host.Length == 0) return (null, 0);
            return (host, port);
        }

        static bool MatchRuleList(List<string> rules, string host, int port)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            host = host.Trim().ToLowerInvariant();
            var isIp = System.Net.IPAddress.TryParse(host, out _);
            foreach (var raw in rules ?? new List<string>())
            {
                var pat = (raw ?? "").Trim().ToLowerInvariant();
                if (pat.Length == 0) continue;
                // 白名单条目可带端口（如 10.114.105.5:8000）：条目指定端口时必须与请求端口一致才放行；未指定端口则任意端口
                var (patHost, patPort) = SplitHostPort(pat);
                if (patPort != 0 && patPort != port) continue;
                // IP 段通配：192.168.1.*
                if (patHost.EndsWith(".*") && IsIpLike(patHost.Substring(0, patHost.Length - 2)))
                {
                    var prefix = patHost.Substring(0, patHost.Length - 1); // "192.168.1."
                    if (host.StartsWith(prefix, StringComparison.Ordinal)) return true;
                    continue;
                }
                // CIDR：192.168.1.0/24
                if (isIp && patHost.IndexOf('/') > 0)
                {
                    if (IpInCidr(host, patHost)) return true;
                    continue;
                }
                if (patHost.StartsWith("*."))
                {
                    var core = patHost.Substring(2);
                    if (host == core || host.EndsWith("." + core, StringComparison.Ordinal)) return true;
                }
                else
                {
                    if (host == patHost || host.EndsWith("." + patHost, StringComparison.Ordinal)) return true;
                }
            }
            return false;
        }

        /// <summary>拆分白名单条目中的端口：仅当形如 "host:port"（端口 1-65535）时返回端口，否则端口为 0（不限制）。</summary>
        static (string host, int port) SplitHostPort(string s)
        {
            if (s.Contains('[')) return (s, 0); // IPv6 字面量，不做端口拆分
            var idx = s.LastIndexOf(':');
            if (idx > 0 && int.TryParse(s.Substring(idx + 1), out var p) && p > 0 && p <= 65535)
                return (s.Substring(0, idx), p);
            return (s, 0);
        }

        static bool IsIpLike(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (var c in s)
                if (!char.IsDigit(c) && c != '.') return false;
            return true;
        }

        static bool IpInCidr(string ip, string cidr)
        {
            var parts = cidr.Split('/');
            if (parts.Length != 2 || !int.TryParse(parts[1], out var bits)) return false;
            if (!System.Net.IPAddress.TryParse(parts[0], out var net)) return false;
            if (!System.Net.IPAddress.TryParse(ip, out var addr)) return false;
            var n = net.GetAddressBytes();
            var a = addr.GetAddressBytes();
            if (n.Length != a.Length) return false;
            var fullBytes = bits / 8;
            var remBits = bits % 8;
            for (var i = 0; i < fullBytes && i < n.Length; i++)
                if (n[i] != a[i]) return false;
            if (remBits > 0 && fullBytes < n.Length)
            {
                var mask = (byte)(0xFF << (8 - remBits));
                if ((n[fullBytes] & mask) != (a[fullBytes] & mask)) return false;
            }
            return true;
        }

        /// <summary>下载判定：返回是否允许。不允许时给出原因。</summary>
        public bool IsDownloadAllowed(string fileName, out string reason)
        {
            reason = null;
            if (!Download.Enabled)
            {
                reason = "教师端已开启\u201c禁止下载\u201d";
                return false;
            }
            var ext = System.IO.Path.GetExtension(fileName ?? "").TrimStart('.').ToLowerInvariant();
            if (Download.AllowTypes.All(t => !string.Equals(t, ext, StringComparison.OrdinalIgnoreCase)))
            {
                reason = "文件类型 ." + ext + " 不在允许列表内";
                return false;
            }
            return true;
        }
    }
}
