using System.ComponentModel;
using System.Windows;
using System.Windows.Data;

namespace TeacherConsole
{
    /// <summary>实时日志窗口：与主界面共享同一日志集合，支持筛选与清空。</summary>
    public partial class LogWindow : Window
    {
        readonly ICollectionView _view;
        string _filter = "全部";

        public LogWindow(MainWindow owner)
        {
            InitializeComponent();
            _view = CollectionViewSource.GetDefaultView(owner.Logs);
            _view.Filter = row => FilterLog((LogRow)row);
            LogList.ItemsSource = _view;
        }

        bool FilterLog(LogRow row)
        {
            switch (_filter)
            {
                case "拦截": return row.Result == "拦截";
                case "放行": return row.Result == "放行";
                case "策略": return row.Result == "策略";
                default: return true;
            }
        }

        void Filter_Changed(object sender, RoutedEventArgs e)
        {
            _filter = FAll.IsChecked == true ? "全部"
                : FBlock.IsChecked == true ? "拦截"
                : FAllow.IsChecked == true ? "放行" : "策略";
            _view?.Refresh();
        }

        void ClearLogs_Click(object sender, RoutedEventArgs e)
        {
            ((MainWindow)Owner).Logs.Clear();
            _view?.Refresh();
        }
    }
}
