using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using ClassroomNetGuard.Shared;
using Application = System.Windows.Application;
using MessageBox = System.Windows.Forms.MessageBox;

namespace StudentTray
{
    /// <summary>
    /// 学生端托盘程序：开机自启、把系统代理指向本地管控代理、显示管控状态。
    /// 管控服务未运行时自动恢复代理，避免学生无法上网。
    /// </summary>
    public partial class MainWindow : Window
    {
        NotifyIcon _icon;
        ToolStripMenuItem _statusItem;
        System.Threading.Timer _timer;
        bool _proxySet;

        public MainWindow()
        {
            InitializeComponent();
        }

        void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Hide();

            _icon = new NotifyIcon
            {
                Icon = SystemIcons.Shield,
                Text = "上网管控学生端：初始化中...",
                Visible = true
            };
            _icon.DoubleClick += (s, a) => ShowStatus();

            var menu = new ContextMenuStrip();
            _statusItem = new ToolStripMenuItem("状态：读取中...") { Enabled = false };
            menu.Items.Add(_statusItem);
            menu.Items.Add("打开日志目录", null, (s, a) => OpenLogs());
            // 注意：不提供"退出"菜单，防止学生自行退出托盘导致管控失效
            _icon.ContextMenuStrip = menu;

            _timer = new System.Threading.Timer(PollTick, null, 0, 5000);
        }

        void PollTick(object state)
        {
            var st = PipeClient.GetStatus();
            var serviceOk = st != null;
            var proxy = "127.0.0.1:" + NetGuardConstants.StudentProxyPort;

            if (serviceOk)
            {
                if (!_proxySet)
                {
                    ProxySetter.Enable(proxy);
                    _proxySet = true;
                }
                else if (!ProxySetter.IsEnabled(proxy))
                {
                    // 学生关闭/篡改了系统代理（会导致流量绕过白名单）→ 立即恢复
                    ProxySetter.Enable(proxy);
                }
            }
            else if (_proxySet)
            {
                ProxySetter.Restore();
                _proxySet = false;
            }

            string text;
            if (!serviceOk)
            {
                text = "上网管控：服务未运行（代理未生效）";
            }
            else
            {
                text = "上网管控：" + (st.Connected ? "已连接教师端 " + st.TeacherHost : "未连接教师端")
                     + " · " + (st.ClassroomOn ? "课堂管控" : "自由模式")
                     + " · 策略 v" + st.PolicyVersion;
            }
            try
            {
                if (text.Length > 60) text = text.Substring(0, 60);
                _icon.Text = text;
                _statusItem.Text = text;
            }
            catch { }
        }

        void ShowStatus()
        {
            var st = PipeClient.GetStatus();
            var msg = st == null
                ? "管控服务未运行。\n请确认已安装并启动 NetGuardStudentService。"
                : string.Format(
                    "连接教师端：{0}\n教师机：{1}\n管控模式：{2}\n策略版本：v{3}",
                    st.Connected ? "已连接" : "未连接",
                    string.IsNullOrEmpty(st.TeacherHost) ? "-" : st.TeacherHost,
                    st.ClassroomOn ? "课堂管控（白名单生效）" : "自由模式（全部放行）",
                    st.PolicyVersion);
            MessageBox.Show(msg, "上网管控学生端");
        }

        void OpenLogs()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "NetGuard", "logs");
            try
            {
                Directory.CreateDirectory(dir);
                Process.Start("explorer.exe", dir);
            }
            catch { }
        }

        void ExitApp()
        {
            ProxySetter.Restore();
            _timer?.Dispose();
            if (_icon != null) { _icon.Visible = false; _icon.Dispose(); }
            Application.Current.Shutdown();
        }

        void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // 退出时恢复系统代理并清理托盘资源
            ProxySetter.Restore();
            _timer?.Dispose();
            if (_icon != null) { _icon.Visible = false; _icon.Dispose(); }
        }
    }
}
