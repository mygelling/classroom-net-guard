using System;

namespace StudentService
{
    /// <summary>
    /// 网页解锁状态（共享）：学生在拦截页输入教师下发的密码后临时放行上网，
    /// 到期自动恢复管控。教师端重新下发策略不会清除解锁（解锁以到期为准）。
    /// </summary>
    public sealed class UnlockState
    {
        public static readonly TimeSpan Duration = TimeSpan.FromMinutes(30);

        readonly object _gate = new object();
        long _untilTicks;

        public bool IsActive { get { lock (_gate) return DateTime.UtcNow.Ticks < _untilTicks; } }

        public DateTime UntilUtc { get { lock (_gate) return new DateTime(_untilTicks, DateTimeKind.Utc); } }

        public void Unlock()
        {
            lock (_gate) _untilTicks = DateTime.UtcNow.Add(Duration).Ticks;
        }

        public void Lock()
        {
            lock (_gate) _untilTicks = 0;
        }
    }
}
