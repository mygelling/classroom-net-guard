using System;
using System.ComponentModel;
using System.Windows;

namespace TeacherConsole
{
    /// <summary>设备列表行模型，绑定到 WPF 界面。</summary>
    public sealed class DeviceViewModel : INotifyPropertyChanged
    {
        public string Seat { get; set; }
        public string Name { get; set; }
        public string Ip { get; set; }
        public string Key => Seat + "|" + Ip;

        bool _online;
        public bool Online
        {
            get => _online;
            set
            {
                _online = value;
                OnChanged(nameof(Online));
                OnChanged(nameof(StatusText));
                OnChanged(nameof(StatusBrush));
                OnChanged(nameof(CardBrush));
                OnChanged(nameof(CardBrushBg));
            }
        }

        DateTime _lastSeen = DateTime.MinValue;
        public DateTime LastSeen
        {
            get => _lastSeen;
            set { _lastSeen = value; OnChanged(nameof(LastSeen)); OnChanged(nameof(LastSeenText)); }
        }

        string _currentUrl = "";
        public string CurrentUrl
        {
            get => _currentUrl;
            set { _currentUrl = value ?? ""; OnChanged(nameof(CurrentUrl)); }
        }

        int _blockedCount;
        public int BlockedCount
        {
            get => _blockedCount;
            set { _blockedCount = value; OnChanged(nameof(BlockedCount)); }
        }

        bool _classroomOn = true;
        /// <summary>该设备是否课堂管控模式（true=管控/白名单，false=自由/全放行）。</summary>
        public bool ClassroomOn
        {
            get => _classroomOn;
            set { _classroomOn = value; OnChanged(nameof(ClassroomOn)); OnChanged(nameof(ModeText)); OnChanged(nameof(ModeBrush)); }
        }

        public string ModeText => ClassroomOn ? "管控" : "自由";
        public string ModeBrush => ClassroomOn ? "#0E7C66" : "#E0912F";

        string _unlockPassword = "";
        /// <summary>该设备网页解锁密码（教师端设置，按设备独立）。</summary>
        public string UnlockPassword
        {
            get => _unlockPassword;
            set { _unlockPassword = value ?? ""; OnChanged(nameof(UnlockPassword)); }
        }

        bool _unlocked;
        /// <summary>该设备当前是否处于解锁放行状态（学生输对密码后 30 分钟）。</summary>
        public bool Unlocked
        {
            get => _unlocked;
            set
            {
                _unlocked = value;
                OnChanged(nameof(Unlocked));
                OnChanged(nameof(UnlockStatusText));
                OnChanged(nameof(UnlockStatusBrush));
                OnChanged(nameof(RelockVisibility));
                OnChanged(nameof(CardBrush));
                OnChanged(nameof(CardBrushBg));
            }
        }

        public string UnlockStatusText => Unlocked ? "放行中" : "管控中";
        public string UnlockStatusBrush => Unlocked ? "#0E7C66" : "#7C8A85";
        public Visibility RelockVisibility => Unlocked ? Visibility.Visible : Visibility.Collapsed;

        public string StatusText => Online ? "在线" : "离线";
        public string StatusBrush => Online ? "#16A34A" : "#B9C4C0";
        public string LastSeenText => LastSeen == DateTime.MinValue ? "-" : LastSeen.ToString("HH:mm:ss");

        /// <summary>图标主色：在线绿 / 离线灰 / 放行中橙。</summary>
        public string CardBrush => !Online ? "#9AA7A2" : Unlocked ? "#E0912F" : "#0E7C66";
        /// <summary>图标底色。</summary>
        public string CardBrushBg => !Online ? "#EEF1F0" : Unlocked ? "#FDF3E6" : "#E4F1EC";

        public event PropertyChangedEventHandler PropertyChanged;
        void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
