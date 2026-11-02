using System;

namespace TeacherConsole
{
    /// <summary>
    /// 解锁密码生成：随机 8 位数字（首位非 0，范围 10000000–99999999）。
    /// 教师端每次启动时为所有学生机重新随机生成；新学生机上线时自动生成。
    /// 保留 (computerName, date) 签名以便沿用既有调用点。
    /// </summary>
    public static class DefaultPassword
    {
        public static string Generate(string computerName, DateTime date)
        {
            return Random.Shared.Next(10000000, 100000000).ToString();
        }
    }
}
