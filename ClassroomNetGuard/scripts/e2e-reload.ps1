$ErrorActionPreference = 'Continue'
[IO.File]::WriteAllText('C:\ProgramData\NetGuard\config.json', '{"TeacherHost":"127.0.0.1","TeacherPort":19999,"ProxyPort":8888,"LocalApiPort":8890}')

# 本地页面服务器（记录请求次数到日志文件，验证 CDP 自动刷新触发重新请求）
$pyhttp = @'
import http.server, threading, os
LOG = os.path.join(os.environ["TEMP"], "ng-http-count.log")
if os.path.exists(LOG): os.remove(LOG)
class H(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        with open(LOG, "a") as f: f.write("GET %s\n" % self.path)
        body = b"<html><body>test page</body></html>"
        self.send_response(200)
        self.send_header("Content-Type", "text/html")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)
    def log_message(self, *a): pass
srv = http.server.HTTPServer(("127.0.0.1", 9102), H)
threading.Thread(target=srv.serve_forever, daemon=True).start()
import time
time.sleep(45)
'@
[IO.File]::WriteAllText("$env:TEMP\ng-http9102.py", $pyhttp)
$httpPid = (Start-Process python -ArgumentList "$env:TEMP\ng-http9102.py" -PassThru -WindowStyle Hidden).Id

# headless Edge 打开本地页面（远程调试端口 9222 + 独立 profile）
$edge = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
if (-not (Test-Path $edge)) { $edge = "C:\Program Files\Microsoft\Edge\Application\msedge.exe" }
$edgeProfile = "$env:TEMP\ng-edge-profile"
Remove-Item $edgeProfile -Recurse -Force -ErrorAction SilentlyContinue
$edgePid = (Start-Process $edge -ArgumentList "--headless","--remote-debugging-port=9222","--user-data-dir=$edgeProfile","--no-first-run","http://127.0.0.1:9102/" -PassThru -WindowStyle Hidden).Id
Write-Host ("edge pid=" + $edgePid)
Start-Sleep 6

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
    $v1 = '{"t":"policy","data":{"version":301,"classroomOn":false,"allowDomains":[],"deviceModes":null,"devicePasswords":null,"unlockPassword":"","download":{"enabled":false,"allowTypes":[],"maxSizeMB":0},"updatedAt":"2026-09-18T21:00:00+08:00"}}'
    Write-Frame $s $v1
    Write-Output "v1 free sent"
    Start-Sleep 5
    $v2 = '{"t":"policy","data":{"version":302,"classroomOn":true,"allowDomains":[],"deviceModes":null,"devicePasswords":null,"unlockPassword":"","download":{"enabled":false,"allowTypes":[],"maxSizeMB":0},"updatedAt":"2026-09-18T21:00:10+08:00"}}'
    Write-Frame $s $v2
    Write-Output "v2 control sent"
    Start-Sleep 10
    $listener.Stop()
}

Start-Sleep 1
$svc = Start-Process "C:\Users\admin\Desktop\上网控制\ClassroomNetGuard\publish\student\StudentService.exe" -ArgumentList "--console" -PassThru
Write-Host ("student pid=" + $svc.Id)
Start-Sleep 6

# 基线：Edge 打开页面已请求 1 次；切换管控后应自动刷新（第 2 次请求）
Write-Host "=== 切换管控（触发自动刷新） ==="
Start-Sleep 12
$countLog = "$env:TEMP\ng-http-count.log"
$n = 0
if (Test-Path $countLog) { $n = (Get-Content $countLog | Measure-Object -Line).Lines }
Write-Host ("=== 本地页面请求次数: " + $n + "（>=2 表示 CDP 自动刷新成功，刷新前应=1） ===")

Get-Process -Name StudentService -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Stop-Process -Id $httpPid -Force -ErrorAction SilentlyContinue
Stop-Process -Id $edgePid -Force -ErrorAction SilentlyContinue
Get-Process -Name msedge -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "*Microsoft*Edge*" } | Stop-Process -Force -ErrorAction SilentlyContinue
Remove-Item 'C:\ProgramData\NetGuard\config.json' -Force -ErrorAction SilentlyContinue
Remove-Item $edgeProfile -Recurse -Force -ErrorAction SilentlyContinue
Receive-Job $fake -ErrorAction SilentlyContinue | ForEach-Object { Write-Host ("fake: " + $_) }
Remove-Job $fake -Force
Write-Host "=== done ==="
