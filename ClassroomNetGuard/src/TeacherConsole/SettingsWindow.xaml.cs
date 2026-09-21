using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using ClassroomNetGuard.Shared;

namespace TeacherConsole
{
    /// <summary>设置窗口：网站白名单（支持域名/IP/CIDR/IP 段通配）与下载策略。</summary>
    public partial class SettingsWindow : Window
    {
        static readonly Regex DomainRegex = new Regex(
            @"^(\*\.)?[a-z0-9\u4e00-\u9fa5]([a-z0-9\u4e00-\u9fa5\-]*[a-z0-9\u4e00-\u9fa5])?(\.[a-z0-9\u4e00-\u9fa5]([a-z0-9\u4e00-\u9fa5\-]*[a-z0-9\u4e00-\u9fa5])?)*$",
            RegexOptions.IgnoreCase);
        static readonly Regex IpRegex = new Regex(
            @"^((25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(/\d{1,2})?(:([1-9]\d{0,3}|[1-5]\d{4}|6[0-4]\d{3}|65[0-4]\d{2}|655[0-2]\d|6553[0-5]))?$|^((25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}\*(:([1-9]\d{0,3}|[1-5]\d{4}|6[0-4]\d{3}|65[0-4]\d{2}|655[0-2]\d|6553[0-5]))?$");

        readonly NetPolicy _policy;
        readonly ObservableCollection<string> _allowDomains;
        readonly ObservableCollection<string> _resourceDomains;
        readonly Action<string> _applyAndPush;
        bool _suppress;

        public SettingsWindow(NetPolicy policy, ObservableCollection<string> allowDomains,
            ObservableCollection<string> resourceDomains, Action<string> applyAndPush)
        {
            InitializeComponent();
            _policy = policy;
            _allowDomains = allowDomains;
            _resourceDomains = resourceDomains;
            _applyAndPush = applyAndPush;

            AllowList.ItemsSource = _allowDomains;
            ResList.ItemsSource = _resourceDomains;
            _suppress = true;
            SyncPolicyToUi();
            _suppress = false;
        }

        bool IsValidRule(string v)
        {
            if (DomainRegex.IsMatch(v)) return true;
            return IpRegex.IsMatch(v);
        }

        void AddAllow_Click(object sender, RoutedEventArgs e)
        {
            var v = AllowInput.Text.Trim();
            if (string.IsNullOrEmpty(v)) { TxtAllowHint.Text = "请输入域名或 IP"; return; }
            if (!IsValidRule(v))
            {
                TxtAllowHint.Text = "格式不正确：支持域名(baidu.com / *.edu.cn)、IP(10.114.105.5)、IP:端口(10.114.105.5:8000)、IP段(10.114.105.*)、网段(10.114.105.0/24)，不要带协议和路径";
                return;
            }
            var lower = v.ToLowerInvariant();
            if (_allowDomains.Contains(lower)) { TxtAllowHint.Text = "该规则已在白名单中"; return; }
            TxtAllowHint.Text = "";
            _allowDomains.Add(lower);
            AllowInput.Text = "";
            _policy.AllowDomains = _allowDomains.ToList();
            _applyAndPush("添加白名单 " + lower);
        }

        void AllowList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            RemoveAllowBtn.IsEnabled = AllowList.SelectedItem != null;
        }

        void RemoveAllow_Click(object sender, RoutedEventArgs e)
        {
            var d = AllowList.SelectedItem as string;
            if (d == null) return;
            _allowDomains.Remove(d);
            _policy.AllowDomains = _allowDomains.ToList();
            _applyAndPush("移除白名单 " + d);
        }

        // ===== 资源放行 =====

        void Referrer_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppress || _policy == null) return;
            _policy.ReferrerAllowEnabled = ChkReferrer.IsChecked == true;
            UpdateResState();
            _applyAndPush(_policy.ReferrerAllowEnabled
                ? "开启：放行白名单页面引用的第三方资源"
                : "关闭：严格按白名单放行（第三方资源拦截）");
        }

        void AddRes_Click(object sender, RoutedEventArgs e)
        {
            var v = ResInput.Text.Trim();
            if (string.IsNullOrEmpty(v)) { TxtResHint.Text = "请输入域名或 IP"; return; }
            if (!IsValidRule(v))
            {
                TxtResHint.Text = "格式不正确：支持域名(baidu.com / *.edu.cn)、IP(10.114.105.5)、IP:端口(10.114.105.5:8000)、IP段(10.114.105.*)、网段(10.114.105.0/24)，不要带协议和路径";
                return;
            }
            var lower = v.ToLowerInvariant();
            if (_resourceDomains.Contains(lower)) { TxtResHint.Text = "该规则已在资源放行列表中"; return; }
            TxtResHint.Text = "";
            _resourceDomains.Add(lower);
            ResInput.Text = "";
            _policy.AllowResourceDomains = _resourceDomains.ToList();
            UpdateResState();
            _applyAndPush("添加资源放行 " + lower);
        }

        void ResList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            RemoveResBtn.IsEnabled = ResList.SelectedItem != null;
        }

        void RemoveRes_Click(object sender, RoutedEventArgs e)
        {
            var d = ResList.SelectedItem as string;
            if (d == null) return;
            _resourceDomains.Remove(d);
            _policy.AllowResourceDomains = _resourceDomains.ToList();
            UpdateResState();
            _applyAndPush("移除资源放行 " + d);
        }

        void UpdateResState()
        {
            TxtResState.Text = (_policy.ReferrerAllowEnabled ? "来源关联放行 · 开启" : "来源关联放行 · 关闭")
                + (_policy.AllowResourceDomains?.Count > 0 ? " · 固定资源 " + _policy.AllowResourceDomains.Count + " 条" : "");
        }

        void DlPolicy_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppress || _policy == null) return;
            _policy.Download.Enabled = DlEnable.IsChecked == true;
            DlDetail.IsEnabled = _policy.Download.Enabled;
            DlDetail.Opacity = _policy.Download.Enabled ? 1.0 : 0.6;
            UpdateDlState();
            _applyAndPush(_policy.Download.Enabled ? "开启下载放行" : "开启“禁止下载”");
        }

        void Type_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppress || _policy == null) return;
            _policy.Download.AllowTypes = TypeBox.Children.OfType<System.Windows.Controls.CheckBox>()
                .Where(c => c.IsChecked == true)
                .Select(c => (string)c.Tag)
                .ToList();
            UpdateDlState();
            _applyAndPush("更新下载类型：" + string.Join("/", _policy.Download.AllowTypes));
        }

        void SizeSel_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_suppress || _policy == null || SizeSel.SelectedItem == null) return;
            var tag = (SizeSel.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string;
            if (tag == null) return;
            _policy.Download.MaxSizeMB = int.Parse(tag);
            UpdateDlState();
            _applyAndPush("单文件上限调整为 " + (_policy.Download.MaxSizeMB == 0 ? "不限" : _policy.Download.MaxSizeMB + " MB"));
        }

        void UpdateDlState()
        {
            TxtDlState.Text = _policy.Download.Enabled
                ? "允许：" + string.Join("/", _policy.Download.AllowTypes) + " · 上限 " +
                  (_policy.Download.MaxSizeMB == 0 ? "不限" : _policy.Download.MaxSizeMB + " MB")
                : "全局禁止下载";
        }

        void SyncPolicyToUi()
        {
            ChkReferrer.IsChecked = _policy.ReferrerAllowEnabled;
            DlEnable.IsChecked = _policy.Download.Enabled;
            DlDetail.IsEnabled = _policy.Download.Enabled;
            DlDetail.Opacity = _policy.Download.Enabled ? 1.0 : 0.6;
            TypePdf.IsChecked = _policy.Download.AllowTypes.Contains("pdf");
            TypeDocx.IsChecked = _policy.Download.AllowTypes.Contains("docx");
            TypePptx.IsChecked = _policy.Download.AllowTypes.Contains("pptx");
            TypeZip.IsChecked = _policy.Download.AllowTypes.Contains("zip");
            TypeXlsx.IsChecked = _policy.Download.AllowTypes.Contains("xlsx");
            var idx = _policy.Download.MaxSizeMB switch { 10 => 0, 50 => 1, 200 => 2, _ => 3 };
            if (SizeSel.Items.Count > idx) SizeSel.SelectedIndex = idx;
            UpdateDlState();
            UpdateResState();
        }

        void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
