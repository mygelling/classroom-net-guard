$ErrorActionPreference = 'Continue'
$root = 'C:\Users\admin\Desktop\上网控制\ClassroomNetGuard'
$log = 'C:\ProgramData\NetGuard\logs\student-service-20260918.log'
if (Test-Path $log) { Remove-Item $log -Force }
[IO.File]::WriteAllText('C:\ProgramData\NetGuard\config.json', '{"teacherHost":"127.0.0.1","teacherPort":9999,"proxyPort":8888,"localApiPort":8890}')

$fake = Start-Job -ScriptBlock {
    $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Any, 9999)
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
    $null = Read-Frame $s
    $v1 = '{"t":"policy","data":{"version":1,"classroomOn":false,"allowDomains":[],"deviceModes":null,"devicePasswords":null,"unlockPassword":"","download":{"enabled":false,"allowTypes":[],"maxSizeMB":0},"updatedAt":"2026-09-18T14:00:00+08:00"}}'
    Write-Frame $s $v1
    Write-Output ("v1 自由模式 sent")
    Start-Sleep 10
    $v2 = '{"t":"policy","data":{"version":2,"classroomOn":true,"allowDomains":[],"deviceModes":null,"devicePasswords":null,"unlockPassword":"","download":{"enabled":false,"allowTypes":[],"maxSizeMB":0},"updatedAt":"2026-09-18T14:01:00+08:00"}}'
    Write-Frame $s $v2
    Write-Output ("v2 课堂管控 sent")
    Start-Sleep 16
    $listener.Stop()
}

Start-Sleep 1
$svc = Start-Process "$root\publish\student\StudentService.exe" -ArgumentList "--console" -PassThru
Start-Sleep 9

$got = $false
for ($i=0; $i -lt 15; $i++) {
    Start-Sleep 1
    try {
        $p = (Invoke-WebRequest -Uri "http://127.0.0.1:8890/policy" -UseBasicParsing -TimeoutSec 3).Content | ConvertFrom-Json
        if ($p.classroomOn -eq $false) { $got = $true; Write-Host "自由模式策略生效"; break }
    } catch {}
}

# 自由模式下访问（期待放行路径，非拦截页）
curl.exe -s -x http://127.0.0.1:8888 "http://www.sina.com/" -o "$env:TEMP\ng-f1.html" -w "code=%{http_code} " --max-time 8
$r1 = Get-Content "$env:TEMP\ng-f1.html" -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
Write-Host ("自由模式访问 -> 拦截页=" + ($r1 -match '访问已被拦截') + " len=" + $r1.Length)

# 等待 v2（管控）下发
$got2 = $false
for ($i=0; $i -lt 20; $i++) {
    Start-Sleep 1
    try {
        $p2 = (Invoke-WebRequest -Uri "http://127.0.0.1:8890/policy" -UseBasicParsing -TimeoutSec 3).Content | ConvertFrom-Json
        if ($p2.classroomOn -eq $true -and $p2.version -eq 2) { $got2 = $true; Write-Host "课堂管控策略生效"; break }
    } catch {}
}

# 管控后访问（期待拦截页）
Start-Sleep 2
curl.exe -s -x http://127.0.0.1:8888 "http://www.sina.com/" -o "$env:TEMP\ng-f2.html" -w "code=%{http_code} " --max-time 8
$r2 = Get-Content "$env:TEMP\ng-f2.html" -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
Write-Host ("管控后访问 -> 拦截页=" + ($r2 -match '访问已被拦截') + " len=" + $r2.Length)

Get-Process -Name StudentService -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Receive-Job $fake | ForEach-Object { Write-Host ("fake: " + $_) }
Remove-Job $fake -Force
Remove-Item 'C:\ProgramData\NetGuard\config.json' -ErrorAction SilentlyContinue
Write-Host "=== 日志尾部 ==="
Get-Content $log -Tail 5 -Encoding Default
Write-Host "=== 测试结束 ==="
