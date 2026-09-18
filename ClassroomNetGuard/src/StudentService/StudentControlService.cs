using System;
using System.ServiceProcess;

namespace StudentService
{
    /// <summary>学生端管控服务：连接教师端 + 本地白名单代理 + 本地策略 API + 托盘状态管道。</summary>
    public sealed class StudentControlService : ServiceBase
    {
        TeacherConnection _conn;
        ProxyServer _proxy;
        LocalPolicyApi _api;
        PipeServer _pipe;
        System.Threading.Timer _unlockReporter;

        public StudentControlService()
        {
            ServiceName = "NetGuardStudentService";
            CanStop = true;
            CanShutdown = true;
            AutoLog = true;
        }

        protected override void OnStart(string[] args)
        {
            StartCore();
            base.OnStart(args);
        }

        protected override void OnStop()
        {
            StopCore();
            base.OnStop();
        }

        protected override void OnShutdown() => StopCore();

        // ===== 前台调试模式 =====

        public void StartDebug()
        {
            ServiceName = "NetGuardStudentService(调试)";
            StartCore();
        }

        public void StopDebug() => StopCore();

        void StartCore()
        {
            LogWriter.Info("===== 学生端管控服务启动 =====");
            var cfg = ServiceConfig.Load();

            var unlock = new UnlockState();
            _conn = new TeacherConnection(cfg, unlock);
            _conn.Start();

            // HTTPS 拦截证书：生成 CA 并装入本机信任根（失败则 http 拦截仍可用，https 退化为拒绝连接）
            var certMgr = new CertManager();
            try { certMgr.EnsureCa(); LogWriter.Info("HTTPS 管控证书就绪"); }
            catch (Exception ex) { LogWriter.Warn("HTTPS 证书初始化失败：" + ex.Message); }

            _proxy = new ProxyServer(_conn.Policy, _conn, certMgr, unlock);
            _proxy.Start(cfg.ProxyPort);

            _api = new LocalPolicyApi(_conn.Policy, unlock);
            _api.Start(cfg.LocalApiPort);

            _pipe = new PipeServer(_conn);
            _pipe.Start();

            // 解锁状态变化上报教师端：解锁/到期/被锁定都会同步教师端"放行/管控"显示
            var lastUnlockActive = false;
            _unlockReporter = new System.Threading.Timer(_ =>
            {
                var active = unlock.IsActive;
                if (active != lastUnlockActive)
                {
                    lastUnlockActive = active;
                    if (active) _ = _conn.SendLogAsync(Environment.MachineName, "UNLOCK:ON", "网页解锁，临时放行 30 分钟");
                    else _ = _conn.SendLogAsync(Environment.MachineName, "UNLOCK:OFF", "解锁结束，恢复课堂管控");
                }
            }, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));

            LogWriter.Info("组件全部启动：代理 :" + cfg.ProxyPort + " · API :" + cfg.LocalApiPort);
        }

        void StopCore()
        {
            LogWriter.Info("===== 学生端管控服务停止 =====");
            try { _unlockReporter?.Dispose(); } catch { }
            try { _pipe?.Stop(); } catch { }
            try { _api?.Stop(); } catch { }
            try { _proxy?.Stop(); } catch { }
            try { _conn?.Stop(); } catch { }
        }
    }
}
