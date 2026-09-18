# 卸载学生端管控服务与托盘自启
# 用法（管理员 PowerShell）：.\uninstall-service.ps1
$ErrorActionPreference = "Stop"

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "[错误] 请以管理员身份运行本脚本" -ForegroundColor Red
    exit 1
}

$svc = Get-Service -Name "NetGuardStudentService" -ErrorAction SilentlyContinue
if ($svc) {
    Stop-Service -Name "NetGuardStudentService" -Force -ErrorAction SilentlyContinue
    sc.exe delete NetGuardStudentService | Out-Null
    Write-Host "[OK] 学生端服务已卸载" -ForegroundColor Green
} else {
    Write-Host "未发现已安装的学生端服务"
}

Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" `
    -Name "NetGuardStudentTray" -ErrorAction SilentlyContinue
Write-Host "[OK] 托盘自启已移除" -ForegroundColor Green
Write-Host "提示：如需立即恢复上网，请在浏览器中关闭系统代理，或重启学生机。"
