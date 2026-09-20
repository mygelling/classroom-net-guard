# 上网管控学生端 - 安装（先卸载旧版本，再安装新版本）
# 由 setup.bat 以管理员权限调用。流程：
#   卸载：停旧服务并删除 → 结束旧托盘进程 → 移除旧自启项 → 删除旧程序目录 → 还原受控 Edge 快捷方式
#   安装：解压新程序包 → 安装并启动服务 → 设置所有用户自启 → 立即启动托盘
param(
    [string]$Dest = "C:\Program Files\NetGuard",
    [string]$Zip  = "",
    [string]$TeacherHost = ""
)
$ErrorActionPreference = "Stop"

$serviceName = "NetGuardStudentService"
$exe     = Join-Path $Dest "StudentService.exe"
$tray    = Join-Path $Dest "StudentTray.exe"
$startupVbs = Join-Path (Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\StartUp") "NetGuardStudentTray.vbs"

Write-Host "== 卸载旧版本 =="

# 1) 停止并删除旧服务
$svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($svc) {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Milliseconds 500
}

# 2) 结束旧托盘进程（否则旧 exe 被占用无法删除/覆盖）
Stop-Process -Name "StudentTray" -Force -ErrorAction SilentlyContinue
# 2b) 结束残留的服务进程（sc delete 后进程可能未完全退出，会占用端口/管道）
Stop-Process -Name "StudentService" -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

# 3) 移除旧自启项（按用户注册 + 公共启动文件夹）
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" `
    -Name "NetGuardStudentTray" -ErrorAction SilentlyContinue
Remove-Item -Path $startupVbs -Force -ErrorAction SilentlyContinue

# 4) 删除旧程序目录（等待服务/托盘进程完全退出后重试）
for ($i = 0; $i -lt 10; $i++) {
    Remove-Item -Path $Dest -Recurse -Force -ErrorAction SilentlyContinue
    if (-not (Test-Path $Dest)) { break }
    Start-Sleep -Milliseconds 500
}
if (Test-Path $Dest) {
    Write-Host "[错误] 旧版本目录无法删除（可能有进程占用）：$Dest" -ForegroundColor Red
    exit 1
}

# 4b) 还原受控 Edge（旧版曾把 Edge 快捷方式改为带远程调试端口启动）：
#     删除桌面"Edge 学生浏览器"快捷方式，去掉系统 Edge 快捷方式参数里的调试参数，删除独立配置目录
Remove-Item -Path (Join-Path ([Environment]::GetFolderPath("CommonDesktopDirectory")) "Edge 学生浏览器.lnk") `
    -Force -ErrorAction SilentlyContinue
$edgeLnkPaths = @(
    "C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Microsoft Edge.lnk",
    (Join-Path $env:APPDATA "Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\Microsoft Edge.lnk"),
    (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Pinned\Microsoft Edge.lnk"),
    "C:\Users\Public\Desktop\Microsoft Edge.lnk"
)
foreach ($lnkPath in $edgeLnkPaths) {
    if (-not (Test-Path $lnkPath)) { continue }
    try {
        $ws = New-Object -ComObject WScript.Shell
        $sc = $ws.CreateShortcut($lnkPath)
        $args2 = $sc.Arguments
        if ($args2 -match "remote-debugging-port") {
            $cleaned = ($args2 -replace "--remote-debugging-port=9222", "" -replace "--user-data-dir=C:\ProgramData\NetGuard\edge-profile", "" -replace "--no-first-run", "" -replace "--no-default-browser-check", "").Trim()
            $sc.Arguments = $cleaned
            $sc.Save()
        }
    } catch { }
}
Remove-Item -Path "C:\ProgramData\NetGuard\edge-profile" -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "== 安装新版本 =="

# 5) 解压新程序包
New-Item -ItemType Directory -Path $Dest | Out-Null
if ($Zip -and (Test-Path $Zip)) {
    Expand-Archive -LiteralPath $Zip -DestinationPath $Dest -Force
} else {
    Write-Host "[错误] 找不到程序包：$Zip" -ForegroundColor Red
    exit 1
}

# 6) 写入教师端 IP 配置（用户指定时；留空则服务启动时自动搜索教师机）
#    配置文件：%ProgramData%\NetGuard\config.json（服务启动时读取，改后需重启服务）
if ($TeacherHost) {
    $cfgDir = Join-Path $env:ProgramData "NetGuard"
    if (-not (Test-Path $cfgDir)) { New-Item -ItemType Directory -Path $cfgDir | Out-Null }
    $json = @{ TeacherHost = $TeacherHost } | ConvertTo-Json
    Set-Content -Path (Join-Path $cfgDir "config.json") -Value $json -Encoding UTF8
    Write-Host "教师端 IP 已配置：$TeacherHost"
} else {
    Write-Host "教师端 IP 未指定：服务启动时将自动搜索教师机"
}

# 7) 安装并启动服务（开机自动启动）
New-Service -Name $serviceName `
    -DisplayName "NetGuard Student Service" `
    -Description "Classroom internet control: connects to teacher, enforces whitelist proxy and download policy." `
    -BinaryPathName ('"' + $exe + '"') `
    -StartupType Automatic | Out-Null
Start-Service -Name $serviceName

# 7b) 导入 HTTPS 管控证书到本机信任根（服务首启已生成 CA）
#     让学生机的浏览器信任拦截证书，从而 HTTPS 被拦时也能显示"访问已被拦截"页面
$caPfx = Join-Path $env:ProgramData "NetGuard\cert\ca.pfx"
for ($i = 0; $i -lt 6; $i++) {
    if (Test-Path $caPfx) { break }
    Start-Sleep -Milliseconds 500
}
if (Test-Path $caPfx) {
    try {
        $caCert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($caPfx, "NetGuardCA!2026", "Exportable")
        $tmpCrt = Join-Path $env:TEMP "ng-ca.crt"
        [IO.File]::WriteAllBytes($tmpCrt, $caCert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
        & certutil -addstore -f Root $tmpCrt | Out-Null
        Remove-Item $tmpCrt -Force -ErrorAction SilentlyContinue
        Write-Host "HTTPS 管控证书已装入本机信任根"
    } catch {
        Write-Host "[警告] HTTPS 证书导入失败：$($_.Exception.Message)"
    }
}

# 8) 所有登录用户自启：公共启动文件夹（每次登录启动托盘并设置系统代理）
$vbsLine = 'CreateObject("WScript.Shell").Run """' + $tray + '""", 0, False'
Set-Content -Path $startupVbs -Value $vbsLine -Encoding ASCII

# 9) 立即启动托盘（当前用户，无需重启）→ 马上设置系统代理
#    注意：不能用临时 vbs + wscript 方式（wscript 异步读取文件，立即删除会产生竞态报错）
Start-Process -FilePath $tray -WindowStyle Hidden

Write-Host "OK: old version uninstalled, new version installed."
