using System;
using System.Text;

namespace TeacherConsole
{
    /// <summary>
    /// 默认解锁密码算法：计算机名逐字符转数字求和 × 当天日期(yyyyMMdd)逐字符转数字求和，
    /// 乘积取后 6 位（不足 6 位前补 0）。每天每个计算机名生成独立密码。
    /// </summary>
    public static class DefaultPassword
    {
        public static string Generate(string computerName, DateTime date)
        {
            long sumName = 0;
            foreach (var c in computerName ?? "") sumName += c;

            var ds = date.ToString("yyyyMMdd");
            long sumDate = 0;
            foreach (var c in ds) sumDate += c;

            var product = sumName * sumDate;
            return (product % 1000000).ToString("D6");
        }
    }
}
