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
    $hello = Read-Frame $s
    Write-Output "hello"
    $v1 = '{"t":"policy","data":{"version":102,"classroomOn":true,"allowDomains":["10.114.105.150"],"deviceModes":null,"devicePasswords":null,"unlockPassword":"","download":{"enabled":false,"allowTypes":[],"maxSizeMB":0},"updatedAt":"2026-09-18T19:00:00+08:00"}}'
    Write-Frame $s $v1
    Write-Output "v1 sent"
    Start-Sleep 20
    $listener.Stop()
}

Start-Sleep 1
$svc = Start-Process "C:\Users\admin\Desktop\上网控制\ClassroomNetGuard\publish\student\StudentService.exe" -ArgumentList "--console" -PassThru
Write-Host ("student pid=" + $svc.Id)
Start-Sleep 8

$py = @'
import socket
def read_resp(s):
    data = b''
    while b'\r\n\r\n' not in data:
        chunk = s.recv(4096)
        if not chunk: break
        data += chunk
    head, _, body = data.partition(b'\r\n\r\n')
    cl = 0
    for line in head.split(b'\r\n'):
        if line.lower().startswith(b'content-length:'):
            cl = int(line.split(b':')[1].strip())
    while len(body) < cl:
        chunk = s.recv(4096)
        if not chunk: break
        body += chunk
    status = head.split(b'\r\n')[0].decode(errors='replace')
    conn = [l.decode(errors='replace') for l in head.split(b'\r\n') if l.lower().startswith(b'connection:')]
    return status, len(body), conn
s = socket.create_connection(('127.0.0.1', 8888), timeout=10)
req = b'GET http://10.114.105.150/ HTTP/1.1\r\nHost: 10.114.105.150\r\nConnection: keep-alive\r\nProxy-Connection: keep-alive\r\n\r\n'
s.sendall(req)
print('req1 ->', read_resp(s))
# 连接被代理按 Connection: close 主动关闭（浏览器会感知并重开连接）
try:
    s.sendall(req)
    print('req2(same conn) ->', read_resp(s))
except Exception as e:
    print('same-conn closed by proxy (expected):', type(e).__name__)
s.close()
# 浏览器行为：重开新连接继续请求
s2 = socket.create_connection(('127.0.0.1', 8888), timeout=10)
s2.sendall(req)
print('req2(new conn) ->', read_resp(s2))
s2.close()
print('OK_BOTH_REQUESTS_SERVED')
'@
[IO.File]::WriteAllText("$env:TEMP\ng-keepalive.py", $py)
python "$env:TEMP\ng-keepalive.py" 2>&1

Start-Sleep 3
Get-Process -Name StudentService -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Remove-Item 'C:\ProgramData\NetGuard\config.json' -Force -ErrorAction SilentlyContinue
Receive-Job $fake -ErrorAction SilentlyContinue | ForEach-Object { Write-Host ("fake: " + $_) }
Remove-Job $fake -Force
Write-Host "=== done ==="
