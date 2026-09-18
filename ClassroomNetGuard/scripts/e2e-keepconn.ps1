$ErrorActionPreference = 'Continue'
$root = 'C:\Users\admin\Desktop\上网控制\ClassroomNetGuard'
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
    Write-Output "v1 自由模式"
    Start-Sleep 12
    $v2 = '{"t":"policy","data":{"version":2,"classroomOn":true,"allowDomains":[],"deviceModes":null,"devicePasswords":null,"unlockPassword":"","download":{"enabled":false,"allowTypes":[],"maxSizeMB":0},"updatedAt":"2026-09-18T14:01:00+08:00"}}'
    Write-Frame $s $v2
    Write-Output "v2 课堂管控"
    Start-Sleep 20
    $listener.Stop()
}

Start-Sleep 1
$svc = Start-Process "$root\publish\student\StudentService.exe" -ArgumentList "--console" -PassThru

# 等策略 v1（自由）
for ($i=0; $i -lt 20; $i++) { Start-Sleep 1; try { $p = (Invoke-WebRequest "http://127.0.0.1:8890/policy" -UseBasicParsing -TimeoutSec 3).Content | ConvertFrom-Json; if ($p.classroomOn -eq $false) { break } } catch {} }
Write-Host "自由模式策略生效"

# 建立持久连接：发请求 → 读到响应头后保持连接（模拟浏览器打开中的页面）
$tc = New-Object Net.Sockets.TcpClient
$tc.Connect("127.0.0.1", 8888)
$ns = $tc.GetStream()
$req = [Text.Encoding]::ASCII.GetBytes("GET http://www.sina.com/ HTTP/1.1`r`nHost: www.sina.com`r`nConnection: keep-alive`r`n`r`n")
$ns.Write($req, 0, $req.Length); $ns.Flush()
$buf = New-Object byte[] 4096
$n = $ns.Read($buf, 0, 4096)
$first = [Text.Encoding]::ASCII.GetString($buf, 0, [Math]::Min($n, 80))
Write-Host ("连接建立，首响应: " + $first.Split("`n")[0])

# 等 v2（管控）
for ($i=0; $i -lt 20; $i++) { Start-Sleep 1; try { $p2 = (Invoke-WebRequest "http://127.0.0.1:8890/policy" -UseBasicParsing -TimeoutSec 3).Content | ConvertFrom-Json; if ($p2.classroomOn -eq $true -and $p2.version -eq 2) { break } } catch {} }
Write-Host "课堂管控策略生效，等待断连检测..."

# 检测连接是否被断开（ReadAsync 返回 0 或抛异常 = 已断开）
$disconnected = $false
for ($i=0; $i -lt 10; $i++) {
    Start-Sleep 1
    try {
        $ar = $ns.BeginRead($buf, 0, 4096, $null, $null)
        if ($ar.AsyncWaitHandle.WaitOne(1500)) {
            $n2 = $ns.EndRead($ar)
            if ($n2 -eq 0) { $disconnected = $true; break }
        }
    } catch { $disconnected = $true; break }
}
Write-Host ("保持中的连接被断开=" + $disconnected)

$tc.Close()
Get-Process -Name StudentService -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Receive-Job $fake | ForEach-Object { Write-Host ("fake: " + $_) }
Remove-Job $fake -Force
Remove-Item 'C:\ProgramData\NetGuard\config.json' -ErrorAction SilentlyContinue
Write-Host "=== 日志 ==="
Get-Content "C:\ProgramData\NetGuard\logs\student-service-20260918.log" -Encoding Default -ErrorAction SilentlyContinue | Select-String -Pattern "管控恢复|策略"
Write-Host "=== 结束 ==="
