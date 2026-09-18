namespace ClassroomNetGuard.Shared
{
    /// <summary>全局常量。</summary>
    public static class NetGuardConstants
    {
        /// <summary>教师端 TCP 监听端口（策略下发/心跳/日志）。</summary>
        public const int TeacherTcpPort = 9999;
        /// <summary>教师端 UDP 发现端口（学生端广播寻找教师机）。</summary>
        public const int DiscoveryPort = 9998;
        /// <summary>学生端本地代理端口（浏览器流量入口）。</summary>
        public const int StudentProxyPort = 8888;
        /// <summary>学生端本地策略 API 端口（供浏览器扩展读取策略）。</summary>
        public const int LocalApiPort = 8890;
        /// <summary>学生端命名管道名（托盘程序与服务通信）。</summary>
        public const string StudentPipeName = "NetGuardStudentPipe";
        /// <summary>UDP 发现魔法串。</summary>
        public const string DiscoverMagic = "NETGUARD_DISCOVER";
        /// <summary>UDP 发现回应串。</summary>
        public const string TeacherMagic = "NETGUARD_TEACHER";
    }
}
