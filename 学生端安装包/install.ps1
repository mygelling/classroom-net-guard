# 上网管控学生端 - 安装（先卸载旧版本，再安装新版本）
# 由 setup.bat 以管理员权限调用。流程：
#   卸载：停旧服务并删除 → 结束旧托盘进程 → 移除旧自启项 → 删除旧程序目录
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

# 7c) 创建受控 Edge 快捷方式（带远程调试端口 + 独立配置目录）：
#     教师端切回课堂管控时，学生端通过调试端口自动刷新已打开的页面，重新走拦截判定。
#     学生机请通过桌面"Edge 学生浏览器"打开网页；若用普通方式打开 Edge（无调试端口），
#     自动刷新不可用，管控切换时页面需手动刷新后才会被拦截。
$edgePaths = @(
    "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    "C:\Program Files\Microsoft\Edge\Application\msedge.exe"
)
$edgeExe = $edgePaths | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($edgeExe) {
    try {
        $desktop = [Environment]::GetFolderPath("CommonDesktopDirectory")
        $lnkPath = Join-Path $desktop "Edge 学生浏览器.lnk"
        $ws = New-Object -ComObject WScript.Shell
        $sc = $ws.CreateShortcut($lnkPath)
        $sc.TargetPath = $edgeExe
        $sc.Arguments = "--remote-debugging-port=9222 --user-data-dir=C:\ProgramData\NetGuard\edge-profile --no-first-run --no-default-browser-check"
        $sc.Description = "学生受控浏览器：教师端切换管控时自动刷新页面"
        $sc.Save()
        Write-Host "已创建受控 Edge 快捷方式（桌面：Edge 学生浏览器）"
    } catch {
        Write-Host "[警告] 创建受控 Edge 快捷方式失败：$($_.Exception.Message)"
    }
} else {
    Write-Host "[警告] 未找到 Edge，受控快捷方式未创建（自动刷新页面功能不可用）"
}

# 7d) 统一受控入口：把系统开始菜单 / 任务栏 / 公共桌面的 Edge 快捷方式也改为受控启动
#     学生无论从哪个常规入口打开 Edge，都带调试端口（切回管控时页面可自动刷新拦截）
$edgeLnkPaths = @(
    "C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Microsoft Edge.lnk",
    (Join-Path $env:APPDATA "Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\Microsoft Edge.lnk"),
    (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Pinned\Microsoft Edge.lnk"),
    "C:\Users\Public\Desktop\Microsoft Edge.lnk"
)
$edgeArgs = "--remote-debugging-port=9222 --user-data-dir=C:\ProgramData\NetGuard\edge-profile --no-first-run --no-default-browser-check"
$modified = 0
foreach ($lnkPath in $edgeLnkPaths) {
    if (-not (Test-Path $lnkPath)) { continue }
    try {
        $ws2 = New-Object -ComObject WScript.Shell
        $sc2 = $ws2.CreateShortcut($lnkPath)
        if ($sc2.Arguments -notmatch "remote-debugging-port") {
            $sc2.Arguments = ($edgeArgs + " " + $sc2.Arguments).Trim()
            $sc2.Save()
            $modified++
        }
    } catch { }
}
if ($modified -gt 0) { Write-Host "已将 $modified 个系统 Edge 快捷方式改为受控启动（开始菜单/任务栏/桌面）" }

# 8) 所有登录用户自启：公共启动文件夹（每次登录启动托盘并设置系统代理）
$vbsLine = 'CreateObject("WScript.Shell").Run """' + $tray + '""", 0, False'
Set-Content -Path $startupVbs -Value $vbsLine -Encoding ASCII

# 9) 立即启动托盘（当前用户，无需重启）→ 马上设置系统代理
#    注意：不能用临时 vbs + wscript 方式（wscript 异步读取文件，立即删除会产生竞态报错）
Start-Process -FilePath $tray -WindowStyle Hidden

Write-Host "OK: old version uninstalled, new version installed."
