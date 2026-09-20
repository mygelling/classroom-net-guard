$ErrorActionPreference = 'Continue'
[IO.File]::WriteAllText('C:\ProgramData\NetGuard\config.json', '{"TeacherHost":"127.0.0.1","TeacherPort":19999,"ProxyPort":8888,"LocalApiPort":8890}')

$edge = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
if (-not (Test-Path $edge)) { $edge = "C:\Program Files\Microsoft\Edge\Application\msedge.exe" }

# 普通 Edge（无调试端口）：模拟学生平时打开的 Edge，打开本地页面
$normalProfile = "$env:TEMP\ng-normal-profile"
Remove-Item $normalProfile -Recurse -Force -ErrorAction SilentlyContinue
$normalPid = (Start-Process $edge -ArgumentList "--user-data-dir=$normalProfile","http://127.0.0.1:9102/" -PassThru -WindowStyle Hidden).Id
Write-Host ("normal edge pid=" + $normalPid)

# 受控 Edge（9222 + 独立 profile）：只开新标签页（模拟学生没在受控 Edge 上开页面）
$ctrlProfile = "$env:TEMP\ng-ctrl-profile"
Remove-Item $ctrlProfile -Recurse -Force -ErrorAction SilentlyContinue
$ctrlPid = (Start-Process $edge -ArgumentList "--remote-debugging-port=9222","--user-data-dir=$ctrlProfile","edge://newtab" -PassThru -WindowStyle Hidden).Id
Write-Host ("controlled edge pid=" + $ctrlPid)
Start-Sleep 7

$fake = Start-Job -ScriptBlock {
    $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Any, 19999)
    $listener.Start()
    $c = $listener.AcceptTcpClient()
    $s = $c.GetStream()
    function Read-Frame($stream) {
        $lb = New-Object byte[] 4
        $n = $stream.Read($lb,0,4); if ($n -lt 4) { return $null }
        $len = ($lb[0] -shl 24) -bor ($lb[1] -shl 16) -bor ($lb[2] -shl 8) -bor $lb[3]
        if ($len -le 0 -or $len -gt 1048576) { return $null }
        $buf = New-Object byte[] $len; $got = 0
        while ($got -lt $len) { $r = $stream.Read($buf, $got, $len-$got); if ($r -le 0) { return $null }; $got += $r }
        return [Text.Encoding]::UTF8.GetString($buf)
    }
    function Write-Frame($stream, $json) {
        $b = [Text.Encoding]::UTF8.GetBytes($json)
        $h = New-Object byte[] 4
        $h[0] = ($b.Length -shr 24) -band 0xFF; $h[1] = ($b.Length -shr 16) -band 0xFF
        $h[2] = ($b.Length -shr 8) -band 0xFF;  $h[3] = $b.Length -band 0xFF
        $stream.Write($h,0,4); $stream.Write($b,0,$b.Length); $stream.Flush()
    }
    $hello = Read-Frame $s
    $v1 = '{"t":"policy","data":{"version":401,"classroomOn":false,"allowDomains":[],"deviceModes":null,"devicePasswords":null,"unlockPassword":"","download":{"enabled":false,"allowTypes":[],"maxSizeMB":0},"updatedAt":"2026-09-20T10:00:00+08:00"}}'
    Write-Frame $s $v1
    Write-Output "v1 free"
    Start-Sleep 5
    $v2 = '{"t":"policy","data":{"version":402,"classroomOn":true,"allowDomains":[],"deviceModes":null,"devicePasswords":null,"unlockPassword":"","download":{"enabled":false,"allowTypes":[],"maxSizeMB":0},"updatedAt":"2026-09-20T10:00:10+08:00"}}'
    Write-Frame $s $v2
    Write-Output "v2 control"
    Start-Sleep 12
    $listener.Stop()
}

Start-Sleep 1
$svc = Start-Process "C:\Users\admin\Desktop\上网控制\ClassroomNetGuard\publish\student\StudentService.exe" -ArgumentList "--console" -PassThru
Write-Host ("student pid=" + $svc.Id)
Start-Sleep 6

# 基线：确认两类 Edge 都在
$n1 = @(Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" | Where-Object { $_.CommandLine -match 'ng-normal-profile' }).Count
Write-Host ("切换前 普通Edge(ng-normal) 进程数: " + $n1)
Write-Host "=== 切换前所有 msedge 主进程 ==="
Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" | Where-Object { $_.CommandLine -notmatch '--type=' } | ForEach-Object { Write-Host ("pid=" + $_.ProcessId + " :: " + ($_.CommandLine -replace '\s+',' ')) }
Write-Host "=== 教师端切换管控 ==="
Start-Sleep 10

# 验证：普通 Edge 主进程应被杀（ng-normal-profile 主进程没了），受控 Edge 主进程保留（ng-ctrl-profile + remote-debugging-port 还在）
$normalAlive = @(Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" | Where-Object { $_.CommandLine -match 'ng-normal-profile' -and $_.CommandLine -notmatch '--type=' }).Count
$ctrlAlive = @(Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" | Where-Object { $_.CommandLine -match 'remote-debugging-port' -and $_.CommandLine -notmatch '--type=' }).Count
Write-Host ("切换后 普通Edge 主进程存活数: " + $normalAlive + "（应为 0 = 已强制关闭）")
Write-Host ("切换后 受控Edge 主进程存活数: " + $ctrlAlive + "（应 >= 1 = 保留）")

# 学生端日志
$log = Get-ChildItem "C:\ProgramData\NetGuard\logs\*.log" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host ("--- 日志尾部 ---")
Get-Content $log.FullName -Tail 6

Get-Process -Name StudentService -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process -Name msedge -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Remove-Item 'C:\ProgramData\NetGuard\config.json' -Force -ErrorAction SilentlyContinue
Remove-Item $normalProfile, $ctrlProfile -Recurse -Force -ErrorAction SilentlyContinue
Receive-Job $fake -ErrorAction SilentlyContinue | ForEach-Object { Write-Host ("fake: " + $_) }
Remove-Job $fake -Force
Write-Host "=== done ==="
