using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using ClassroomNetGuard.Shared;

namespace TeacherConsole
{
    public sealed class LogRow
    {
        public string Time { get; set; }
        public string Device { get; set; }
        public string Event { get; set; }
        public string Result { get; set; }
    }

    public partial class MainWindow : Window
    {
        readonly TcpServer _server = new TcpServer(TcpServer.DefaultPort);
        readonly DiscoveryServer _discovery = new DiscoveryServer();
        readonly ObservableCollection<DeviceViewModel> _devices = new ObservableCollection<DeviceViewModel>();
        readonly ObservableCollection<LogRow> _logs = new ObservableCollection<LogRow>();
        readonly ObservableCollection<string> _allowDomains = new ObservableCollection<string>();
        readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        ICollectionView _logView;
        NetPolicy _policy;
        string _logFilter = "全部";
        bool _suppress; // 防止代码初始化控件时触发策略变更事件

        public MainWindow()
        {
            InitializeComponent();
        }

        void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _policy = PolicyStore.Load();

            DevList.ItemsSource = _devices;
            _logView = CollectionViewSource.GetDefaultView(_logs);
            _logView.Filter = row => FilterLog((LogRow)row);
            LogList.ItemsSource = _logView;

            _suppress = true;
            SyncPolicyToUi();
            _policy.AllowDomains.ForEach(d => _allowDomains.Add(d));
            _suppress = false;

            _server.DeviceOnline += Server_DeviceOnline;
            _server.DeviceOffline += Server_DeviceOffline;
            _server.LogReceived += Server_LogReceived;
            _timer.Tick += (_, __) => UpdateStats();
            _timer.Start();

            _ = _server.StartAsync();
            _discovery.Start();
            TxtServer.Text = "服务运行中 · TCP " + TcpServer.DefaultPort + " · UDP " + DiscoveryServer.DiscoveryPort;
            TxtServer.Foreground = new SolidColorBrush(Color.FromRgb(0x7F, 0xE0, 0xC4));
            UpdateStats();
            AddLog("系统", "教师端启动，服务已就绪", "策略");
        }

        void Window_Closing(object sender, CancelEventArgs e)
        {
            _server.Stop();
            _discovery.Stop();
        }

        // ===== 服务器事件 =====

        void Server_DeviceOnline(ClientSession session)
        {
            Dispatcher.Invoke(() =>
            {
                var dev = session.Device;
                if (_devices.All(d => d.Key != dev.Key)) _devices.Add(dev);
                dev.Online = true;
                dev.LastSeen = DateTime.Now;
                // 自动设置默认解锁密码：设备上线且从未设置过密码时，按“计算机名×当天日期取后6位”自动生成
                // （每机独立、每天变化），并随策略下发，无需教师逐台手动设置。
                if (_policy.DevicePasswords == null) _policy.DevicePasswords = new Dictionary<string, string>();
                if (!_policy.DevicePasswords.ContainsKey(dev.Seat))
                {
                    _policy.DevicePasswords[dev.Seat] = DefaultPassword.Generate(dev.Seat, DateTime.Now);
                    PolicyStore.Save(_policy);
                    AddLog("教师端", dev.Seat + " 自动设置默认解锁密码", "策略");
                }
                // 同步该设备单独设置的模式（未单独设置时跟随全局模式）与解锁密码
                dev.ClassroomOn = _policy.DeviceModes != null && _policy.DeviceModes.TryGetValue(dev.Seat, out var on)
                    ? on : _policy.ClassroomOn;
                dev.UnlockPassword = _policy.DevicePasswords != null && _policy.DevicePasswords.TryGetValue(dev.Seat, out var pw)
                    ? pw : "";
                // 新设备上线即下发按设备定制的当前策略
                _ = session.SendAsync(WireMessage.Create(WireMessage.TPolicy, PolicyFor(dev)));
                AddLog("教师端", dev.Seat + " 上线" + (dev.ClassroomOn ? "（管控）" : "（自由）"), "策略");
                UpdateStats();
            });
        }

        void Server_DeviceOffline(DeviceViewModel dev)
        {
            Dispatcher.Invoke(() =>
            {
                dev.Online = false;
                AddLog("教师端", dev.Seat + " 离线", "策略");
                UpdateStats();
            });
        }

        void Server_LogReceived(LogData log)
        {
            Dispatcher.Invoke(() =>
            {
                // 解锁状态上报：UNLOCK:ON = 学生输对密码临时放行，UNLOCK:OFF = 到期/被锁定恢复管控
                if (!string.IsNullOrEmpty(log.Event) && log.Event.StartsWith("UNLOCK:"))
                {
                    var dev = _devices.FirstOrDefault(d => d.Seat == log.Device || d.Name == log.Device);
                    if (dev != null)
                        dev.Unlocked = log.Event == "UNLOCK:ON";
                    AddLog(log.Device, log.Event == "UNLOCK:ON" ? "网页解锁" : "恢复管控", log.Result);
                    return;
                }
                AddLog(log.Device, log.Event, log.Result);
            });
        }

        // ===== 日志 =====

        void AddLog(string device, string evt, string result)
        {
            _logs.Insert(0, new LogRow
            {
                Time = DateTime.Now.ToString("HH:mm:ss"),
                Device = device,
                Event = evt,
                Result = result
            });
            while (_logs.Count > 500) _logs.RemoveAt(_logs.Count - 1);
            _logView?.Refresh();
        }

        bool FilterLog(LogRow row)
        {
            switch (_logFilter)
            {
                case "拦截": return row.Result == "拦截";
                case "放行": return row.Result == "放行";
                case "策略": return row.Result == "策略";
                default: return true;
            }
        }

        void Filter_Changed(object sender, RoutedEventArgs e)
        {
            _logFilter = FAll.IsChecked == true ? "全部" : (FBlock.IsChecked == true ? "拦截" : "放行");
            _logView?.Refresh();
        }

        void ClearLogs_Click(object sender, RoutedEventArgs e)
        {
            _logs.Clear();
            _logView?.Refresh();
        }

        // ===== 策略操作 =====

        /// <summary>按设备定制策略：全局策略 + 该设备的单独模式/解锁密码；设备模式表与密码表不下发。</summary>
        NetPolicy PolicyFor(DeviceViewModel dev)
        {
            var p = _policy.Clone();
            if (dev != null && p.DeviceModes != null && p.DeviceModes.TryGetValue(dev.Seat, out var on))
                p.ClassroomOn = on;
            if (dev != null && p.DevicePasswords != null && p.DevicePasswords.TryGetValue(dev.Seat, out var pw))
                p.UnlockPassword = pw;
            p.DeviceModes = null;
            p.DevicePasswords = null;
            return p;
        }

        void PushPolicy(string reason)
        {
            _policy.Version++;
            _policy.UpdatedAt = DateTime.Now;
            PolicyStore.Save(_policy);
            foreach (var kv in _server.Sessions)
            {
                if (!kv.Value.Closed)
                    _ = kv.Value.SendAsync(WireMessage.Create(WireMessage.TPolicy, PolicyFor(kv.Value.Device)));
            }
            AddLog("教师端", reason, "策略");
        }

        /// <summary>某台设备单独切换 管控/自由 模式（立即对该设备生效并持久化）。</summary>
        void DevModeToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_policy == null) return;
            var dev = (sender as System.Windows.FrameworkElement)?.DataContext as DeviceViewModel;
            if (dev == null) return;
            _policy.DeviceModes[dev.Seat] = dev.ClassroomOn;
            PolicyStore.Save(_policy); // 保存设备模式表（不改全局策略版本，避免全广播）
            foreach (var kv in _server.Sessions)
            {
                if (!kv.Value.Closed && kv.Value.Device.Key == dev.Key)
                    _ = kv.Value.SendAsync(WireMessage.Create(WireMessage.TPolicy, PolicyFor(dev)));
            }
            AddLog("教师端", dev.Seat + " 切换为" + (dev.ClassroomOn ? "课堂管控" : "自由模式"), "策略");
        }

        void ModeToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppress || _policy == null) return;
            _policy.ClassroomOn = ModeToggle.IsChecked == true;
            ModeToggle.Content = _policy.ClassroomOn ? "课堂管控" : "自由模式";
            ModeToggle.Background = new SolidColorBrush(Color.FromRgb(
                _policy.ClassroomOn ? (byte)0x0E : (byte)0xE0,
                _policy.ClassroomOn ? (byte)0x7C : (byte)0x91,
                _policy.ClassroomOn ? (byte)0x66 : (byte)0x2F));
            PushPolicy(_policy.ClassroomOn ? "切换为课堂管控（白名单生效）" : "切换为自由模式（全部放行）");
            // 全局模式切换后同步各设备卡片状态：未单独设置模式的设备跟随全局，已单独设置的保持单独模式
            foreach (var d in _devices)
            {
                if (_policy.DeviceModes == null || !_policy.DeviceModes.ContainsKey(d.Seat))
                    d.ClassroomOn = _policy.ClassroomOn;
            }
        }

        void SyncPolicyToUi()
        {
            ModeToggle.IsChecked = _policy.ClassroomOn;
            ModeToggle.Content = _policy.ClassroomOn ? "课堂管控" : "自由模式";
            ModeToggle.Background = new SolidColorBrush(Color.FromRgb(
                _policy.ClassroomOn ? (byte)0x0E : (byte)0xE0,
                _policy.ClassroomOn ? (byte)0x7C : (byte)0x91,
                _policy.ClassroomOn ? (byte)0x66 : (byte)0x2F));
        }

        /// <summary>打开设置窗口：网站白名单（支持域名/IP）与下载策略。</summary>
        void Settings_Click(object sender, RoutedEventArgs e)
        {
            var win = new SettingsWindow(_policy, _allowDomains, reason => PushPolicy(reason));
            win.Owner = this;
            win.ShowDialog();
        }

        /// <summary>点击设备卡片打开详情：解锁密码设置与重新拦截。</summary>
        void DevCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var dev = (sender as FrameworkElement)?.DataContext as DeviceViewModel;
            if (dev == null) return;
            var win = new DeviceDetailWindow(dev, ApplyUnlockPassword, ApplyRelock, UpdateStats);
            win.Owner = this;
            win.ShowDialog();
        }

        /// <summary>设置某台设备的网页解锁密码（按设备独立，立即下发并持久化）。</summary>
        void DevUnlockPwd_Click(object sender, RoutedEventArgs e)
        {
            var dev = (sender as System.Windows.FrameworkElement)?.DataContext as DeviceViewModel;
            if (dev != null) ApplyUnlockPassword(dev);
        }

        void ApplyUnlockPassword(DeviceViewModel dev)
        {
            if (_policy == null || dev == null) return;
            if (string.IsNullOrWhiteSpace(dev.UnlockPassword))
            {
                _policy.DevicePasswords.Remove(dev.Seat);
                AddLog("教师端", dev.Seat + " 清除解锁密码", "策略");
            }
            else
            {
                _policy.DevicePasswords[dev.Seat] = dev.UnlockPassword.Trim();
                AddLog("教师端", dev.Seat + " 设置解锁密码", "策略");
            }
            PolicyStore.Save(_policy); // 持久化密码表（不下发全局版本）
            foreach (var kv in _server.Sessions)
            {
                if (!kv.Value.Closed && kv.Value.Device.Key == dev.Key)
                    _ = kv.Value.SendAsync(WireMessage.Create(WireMessage.TPolicy, PolicyFor(dev)));
            }
        }

        /// <summary>手动重新拦截：向该学生机下发锁定指令，立即恢复管控。</summary>
        async void DevRelock_Click(object sender, RoutedEventArgs e)
        {
            var dev = (sender as System.Windows.FrameworkElement)?.DataContext as DeviceViewModel;
            if (dev != null) await ApplyRelockAsync(dev);
        }

        async Task ApplyRelockAsync(DeviceViewModel dev)
        {
            foreach (var kv in _server.Sessions)
            {
                if (!kv.Value.Closed && kv.Value.Device.Key == dev.Key)
                    await kv.Value.SendAsync(WireMessage.Create(WireMessage.TCmd, new CmdData { Action = "lock" }));
            }
            dev.Unlocked = false;
            AddLog("教师端", dev.Seat + " 手动重新拦截", "策略");
        }

        void ApplyRelock(DeviceViewModel dev) => _ = ApplyRelockAsync(dev);

        /// <summary>导出全部学生机的解锁密码清单（CSV）：已设置密码用实际值，未设置的生成当天默认密码。</summary>
        void ExportPasswords_Click(object sender, RoutedEventArgs e)
        {
            if (_devices.Count == 0)
            {
                MessageBox.Show(this, "当前没有设备，无法导出。", "导出密码");
                return;
            }
            var today = DateTime.Now;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("计算机名,IP,解锁密码,状态");
            foreach (var d in _devices)
            {
                var has = !string.IsNullOrWhiteSpace(d.UnlockPassword);
                var pwd = has ? d.UnlockPassword : DefaultPassword.Generate(d.Seat, today);
                var state = has ? "已设置" : "默认密码(未应用)";
                sb.AppendLine(d.Seat + "," + d.Ip + "," + pwd + "," + state);
            }
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "CSV 文件|*.csv",
                FileName = "解锁密码清单_" + today.ToString("yyyyMMdd") + ".csv"
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                System.IO.File.WriteAllText(dlg.FileName, sb.ToString(), new System.Text.UTF8Encoding(true));
                MessageBox.Show(this, "已导出到：\n" + dlg.FileName +
                    "\n\n共 " + _devices.Count + " 台设备。" +
                    "\n提示：默认密码 = 计算机名转数字 × 当天日期转数字 取后 6 位，每天自动变化，需要分发给学生请当天导出。",
                    "导出完成");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "导出失败：" + ex.Message, "导出密码");
            }
        }

        void UpdateStats()
        {
            var online = _devices.Count(d => d.Online);
            TxtOnline.Text = "在线 " + online + "/" + _devices.Count;
            TxtDevSub.Text = online + "/" + _devices.Count + " 台在线";
            TxtBlocked.Text = "拦截 " + _devices.Sum(d => d.BlockedCount);
        }
    }
}
