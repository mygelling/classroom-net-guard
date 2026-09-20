$ErrorActionPreference = 'Continue'
[IO.File]::WriteAllText('C:\ProgramData\NetGuard\config.json', '{"TeacherHost":"127.0.0.1","TeacherPort":19999,"ProxyPort":8888,"LocalApiPort":8890}')

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
    $types = New-Object System.Collections.ArrayList
    $hello = Read-Frame $s
    if ($hello) { [void]$types.Add("hello") }
    # 管控策略（白名单空 → 全拦）
    $v = '{"t":"policy","data":{"version":501,"classroomOn":true,"allowDomains":[],"deviceModes":null,"devicePasswords":null,"unlockPassword":"","download":{"enabled":false,"allowTypes":[],"maxSizeMB":0},"updatedAt":"2026-09-20T11:00:00+08:00"}}'
    Write-Frame $s $v
    # 收集 12 秒内收到的消息
    $deadline = (Get-Date).AddSeconds(12)
    while ((Get-Date) -lt $deadline) {
        $f = Read-Frame $s
        if (-not $f) { break }
        $m = $f | ConvertFrom-Json
        [void]$types.Add($m.t)
    }
    $listener.Stop()
    $grouped = $types | Group-Object | ForEach-Object { "$($_.Name)x$($_.Count)" }
    Write-Output ("RECEIVED: " + ($grouped -join ", "))
}

Start-Sleep 1
$svc = Start-Process "C:\Users\admin\Desktop\上网控制\ClassroomNetGuard\publish\student\StudentService.exe" -ArgumentList "--console" -PassThru
Write-Host ("student pid=" + $svc.Id)
Start-Sleep 6

# 经代理访问被拦截网站（白名单外）
$py = @'
import socket
s = socket.create_connection(('127.0.0.1', 8888), timeout=10)
s.sendall(b'GET http://www.baidu.com/ HTTP/1.1\r\nHost: www.baidu.com\r\nConnection: close\r\n\r\n')
s.settimeout(10)
data = b''
try:
    while True:
        c = s.recv(4096)
        if not c: break
        data += c
        if len(data) > 4096: break
except socket.timeout: pass
print('STATUS:', data.split(b'\r\n')[0].decode(errors='replace'))
print('IS_BLOCKED:', '访问已被拦截' in data.decode(errors='replace'))
s.close()
'@
[IO.File]::WriteAllText("$env:TEMP\ng-reduce.py", $py)
python "$env:TEMP\ng-reduce.py" 2>&1

Start-Sleep 10
Get-Process -Name StudentService -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Remove-Item 'C:\ProgramData\NetGuard\config.json' -Force -ErrorAction SilentlyContinue
Receive-Job $fake | ForEach-Object { Write-Host ("fake: " + $_) }
Remove-Job $fake -Force
Write-Host "=== done ==="
