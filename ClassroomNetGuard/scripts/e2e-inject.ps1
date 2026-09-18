$ErrorActionPreference = 'Continue'
[IO.File]::WriteAllText('C:\ProgramData\NetGuard\config.json', '{"TeacherHost":"127.0.0.1","TeacherPort":19999,"ProxyPort":8888,"LocalApiPort":8890}')

# 慢速上游：接受连接但不响应（模拟"页面正在加载、响应未开始"）
$slow = Start-Process python -ArgumentList "-c", "import socket,time; s=socket.socket(); s.bind(('127.0.0.1',9101)); s.listen(5); [ (c,time.sleep(60)) for c in [x[0] for x in [s.accept()]] ]" -PassThru -WindowStyle Hidden

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
    Write-Output "hello"
    # v1：自由模式（全部放行）
    $v1 = '{"t":"policy","data":{"version":201,"classroomOn":false,"allowDomains":[],"deviceModes":null,"devicePasswords":null,"unlockPassword":"","download":{"enabled":false,"allowTypes":[],"maxSizeMB":0},"updatedAt":"2026-09-18T20:00:00+08:00"}}'
    Write-Frame $s $v1
    Write-Output "v1 free sent"
    Start-Sleep 6
    # v2：切回课堂管控（白名单空 → 全拦）
    $v2 = '{"t":"policy","data":{"version":202,"classroomOn":true,"allowDomains":[],"deviceModes":null,"devicePasswords":null,"unlockPassword":"","download":{"enabled":false,"allowTypes":[],"maxSizeMB":0},"updatedAt":"2026-09-18T20:00:10+08:00"}}'
    Write-Frame $s $v2
    Write-Output "v2 control sent"
    Start-Sleep 12
    $listener.Stop()
}

Start-Sleep 1
$svc = Start-Process "C:\Users\admin\Desktop\上网控制\ClassroomNetGuard\publish\student\StudentService.exe" -ArgumentList "--console" -PassThru
Write-Host ("student pid=" + $svc.Id)
Start-Sleep 8

$py = @'
import socket, time
# 客户端：自由模式下发出请求（上游挂起），等待管控恢复后的注入响应
s = socket.create_connection(('127.0.0.1', 8888), timeout=20)
req = b'GET http://127.0.0.1:9101/ HTTP/1.1\r\nHost: 127.0.0.1:9101\r\nConnection: close\r\n\r\n'
s.sendall(req)
s.settimeout(20)
data = b''
try:
    while True:
        chunk = s.recv(4096)
        if not chunk: break
        data += chunk
        if b'\r\n\r\n' in data and len(data) > data.index(b'\r\n\r\n') + 4:
            break  # 头部已到，够判断
except socket.timeout:
    pass
head = data.split(b'\r\n\r\n')[0].decode(errors='replace')
status = head.split('\r\n')[0] if head else '(无响应)'
print('STATUS:', status)
print('IS_BLOCKED_PAGE:', '访问已被拦截' in data.decode(errors='replace'))
s.close()
'@
[IO.File]::WriteAllText("$env:TEMP\ng-inject.py", $py)
python "$env:TEMP\ng-inject.py" 2>&1

Start-Sleep 4
Get-Process -Name StudentService -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Stop-Process -Id $slow.Id -Force -ErrorAction SilentlyContinue
Remove-Item 'C:\ProgramData\NetGuard\config.json' -Force -ErrorAction SilentlyContinue
Receive-Job $fake -ErrorAction SilentlyContinue | ForEach-Object { Write-Host ("fake: " + $_) }
Remove-Job $fake -Force
Write-Host "=== done ==="
