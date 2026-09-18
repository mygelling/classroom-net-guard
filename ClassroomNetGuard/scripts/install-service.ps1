# 安装学生端管控服务 + 托盘自启
# 用法（管理员 PowerShell）：
#   .\install-service.ps1 -ServiceExe "C:\path\StudentService.exe" -TrayExe "C:\path\StudentTray.exe"
param(
    [string]$ServiceExe = ".\StudentService.exe",
    [string]$TrayExe = ".\StudentTray.exe"
)
$ErrorActionPreference = "Stop"

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "[错误] 请以管理员身份运行本脚本" -ForegroundColor Red
    exit 1
}

$ServiceExe = (Resolve-Path $ServiceExe).Path
$TrayExe = (Resolve-Path $TrayExe).Path

# 卸载旧服务
$svc = Get-Service -Name "NetGuardStudentService" -ErrorAction SilentlyContinue
if ($svc) {
    Stop-Service -Name "NetGuardStudentService" -Force -ErrorAction SilentlyContinue
    sc.exe delete NetGuardStudentService | Out-Null
    Start-Sleep -Milliseconds 500
}

# 安装并启动服务（开机自动启动）
New-Service -Name "NetGuardStudentService" `
    -DisplayName "上网管控学生端服务" `
    -Description "连接教师端获取白名单策略，通过本地代理管控学生上网与下载。" `
    -BinaryPathName "`"$ServiceExe`"" `
    -StartupType Automatic | Out-Null
Start-Service -Name "NetGuardStudentService"
Write-Host "[OK] 学生端服务已安装并启动" -ForegroundColor Green

# 托盘程序开机自启
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
Set-ItemProperty -Path $runKey -Name "NetGuardStudentTray" -Value "`"$TrayExe`""
Write-Host "[OK] 托盘程序已设为开机自启" -ForegroundColor Green
Write-Host "完成。学生机重启后管控自动生效；浏览器需安装上网管控扩展（extension 目录）。"
