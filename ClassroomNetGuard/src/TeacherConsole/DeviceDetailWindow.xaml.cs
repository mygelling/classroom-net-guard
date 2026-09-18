using System;
using System.Windows;
using System.Windows.Media;

namespace TeacherConsole
{
    /// <summary>设备详情：显示计算机名/IP/状态，并提供解锁密码设置与重新拦截。</summary>
    public partial class DeviceDetailWindow : Window
    {
        readonly DeviceViewModel _dev;
        readonly Action<DeviceViewModel> _applyPassword;
        readonly Action<DeviceViewModel> _relock;
        readonly Action _closeNotify;

        public DeviceDetailWindow(DeviceViewModel dev, Action<DeviceViewModel> applyPassword,
            Action<DeviceViewModel> relock, Action closeNotify)
        {
            InitializeComponent();
            _dev = dev;
            _applyPassword = applyPassword;
            _relock = relock;
            _closeNotify = closeNotify;

            TxtTitle.Text = dev.Name + " 的设备";
            TxtSeat.Text = dev.Seat;
            TxtIp.Text = dev.Ip;
            TxtUser.Text = dev.Name;
            TxtLastSeen.Text = dev.LastSeenText;
            TxtStatus.Text = dev.StatusText;
            TxtStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dev.StatusBrush));
            TxtPwd.Text = dev.UnlockPassword;
            BtnRelock.Visibility = dev.Unlocked ? Visibility.Visible : Visibility.Collapsed;
            RefreshPwdState();
        }

        void RefreshPwdState()
        {
            TxtPwdState.Text = string.IsNullOrWhiteSpace(_dev.UnlockPassword)
                ? "未设置解锁密码：学生访问被拦网站时无法自行解锁，会提示联系教师。"
                : "已设置解锁密码：学生输入该密码后临时放行 30 分钟，教师端显示“放行中”。";
        }

        void SetPwd_Click(object sender, RoutedEventArgs e)
        {
            _dev.UnlockPassword = TxtPwd.Text;
            _applyPassword(_dev);
            RefreshPwdState();
            BtnRelock.Visibility = _dev.Unlocked ? Visibility.Visible : Visibility.Collapsed;
        }

        void GenDefaultPwd_Click(object sender, RoutedEventArgs e)
        {
            TxtPwd.Text = DefaultPassword.Generate(_dev.Seat, DateTime.Now);
            RefreshPwdState();
            TxtPwdState.Text = "已生成默认密码（计算机名 × 当天日期取后 6 位）：" + TxtPwd.Text +
                               "。点“设置”后生效；该密码每天自动变化，需重新生成。";
        }

        void Relock_Click(object sender, RoutedEventArgs e)
        {
            _relock(_dev);
            BtnRelock.Visibility = Visibility.Collapsed;
        }

        void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
            _closeNotify?.Invoke();
        }
    }
}
