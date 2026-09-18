using System;
using System.Collections.Generic;
using ClassroomNetGuard.Shared;

class Program
{
    static int _fail;

    static void Check(string label, bool got, bool want)
    {
        var ok = got == want;
        if (!ok) _fail++;
        Console.WriteLine((ok ? "PASS " : "FAIL ") + label + "  => " + got + (ok ? "" : " (want " + want + ")"));
    }

    static NetPolicy P(params string[] rules) => new NetPolicy
    {
        ClassroomOn = true,
        AllowDomains = new List<string>(rules)
    };

    static void Main()
    {
        // 用户场景：白名单 10.114.105.150，访问 10.114.105.5:8000 → 不匹配（拦截）
        Check("白名单150/访问5:8000应拦", P("10.114.105.150").IsDomainAllowed("10.114.105.5", 8000), false);
        // IP:端口 精确匹配
        Check("白名单5:8000/访问5:8000放行", P("10.114.105.5:8000").IsDomainAllowed("10.114.105.5", 8000), true);
        Check("白名单5:8000/访问5:9000应拦", P("10.114.105.5:8000").IsDomainAllowed("10.114.105.5", 9000), false);
        Check("白名单5:8000/访问5无端口默认80应拦", P("10.114.105.5:8000").IsDomainAllowed("10.114.105.5", 80), false);
        // 纯 IP 任意端口
        Check("白名单5/访问5:8000放行", P("10.114.105.5").IsDomainAllowed("10.114.105.5", 8000), true);
        Check("白名单5/访问5:任意端口放行", P("10.114.105.5").IsDomainAllowed("10.114.105.5", 443), true);
        // IP 段通配 + 端口
        Check("白名单105.*/访问5:8000放行", P("10.114.105.*").IsDomainAllowed("10.114.105.5", 8000), true);
        Check("白名单105.*:8000/访问6:8000放行", P("10.114.105.*:8000").IsDomainAllowed("10.114.105.6", 8000), true);
        Check("白名单105.*:8000/访问6:9000应拦", P("10.114.105.*:8000").IsDomainAllowed("10.114.105.6", 9000), false);
        // CIDR
        Check("白名单0/24/访问5放行", P("10.114.105.0/24").IsDomainAllowed("10.114.105.5", 8000), true);
        Check("白名单0/24/访问200.1.1.1应拦", P("10.114.105.0/24").IsDomainAllowed("200.1.1.1", 80), false);
        // 域名回归
        Check("域名baidu.com/访问www.baidu.com放行", P("baidu.com").IsDomainAllowed("www.baidu.com", 443), true);
        Check("通配*.edu.cn/访问a.edu.cn放行", P("*.edu.cn").IsDomainAllowed("a.b.edu.cn", 80), true);
        Check("域名/访问IP不误伤", P("baidu.com").IsDomainAllowed("10.114.105.5", 80), false);
        // 空白名单全拦
        Check("空白名单/任意域名应拦", P().IsDomainAllowed("www.baidu.com", 443), false);
        // 自由模式全放行
        Check("自由模式全放行", new NetPolicy { ClassroomOn = false }.IsDomainAllowed("www.baidu.com", 443), true);

        Console.WriteLine(_fail == 0 ? "=== 全部通过 ===" : "=== " + _fail + " 项失败 ===");
        Environment.Exit(_fail == 0 ? 0 : 1);
    }
}
