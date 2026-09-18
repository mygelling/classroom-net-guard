$ErrorActionPreference = 'Continue'
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
    $hello = Read-Frame $s
    Write-Output "hello"
    $v1 = '{"t":"policy","data":{"version":99,"classroomOn":true,"allowDomains":["10.114.105.150"],"deviceModes":null,"devicePasswords":null,"unlockPassword":"","download":{"enabled":false,"allowTypes":[],"maxSizeMB":0},"updatedAt":"2026-09-18T18:00:00+08:00"}}'
    Write-Frame $s $v1
    Write-Output "v1 sent"
    Start-Sleep 30
    $listener.Stop()
}

Start-Sleep 1
$svc = Start-Process "C:\Users\admin\Desktop\上网控制\ClassroomNetGuard\publish\student\StudentService.exe" -ArgumentList "--console" -PassThru
Write-Host ("student pid=" + $svc.Id)
Start-Sleep 10

Write-Host "=== 通过代理访问 10.114.105.150/ （期待 200 与页面内容，而非 404） ==="
curl.exe -s -x http://127.0.0.1:8888 "http://10.114.105.150/" -o "$env:TEMP\ng-intranet.html" -w "HTTP=%{http_code}`n" --max-time 10
$body = Get-Content "$env:TEMP\ng-intranet.html" -Raw -Encoding UTF8
Write-Host ("长度=" + $body.Length)
Write-Host ("含Not Found=" + ($body -match 'Not Found'))
Write-Host ("首行: " + ($body -split "`n" | Select-Object -First 2 | Select-String -NotMatch '^\s*$' | Select-Object -First 1))

Write-Host "=== 对比：白名单外域名应被拦截 ==="
curl.exe -s -x http://127.0.0.1:8888 "http://example.com/" -o "$env:TEMP\ng-block.html" -w "HTTP=%{http_code}`n" --max-time 10
$rb = Get-Content "$env:TEMP\ng-block.html" -Raw -Encoding UTF8
Write-Host ("长度=" + $rb.Length + " 拦截页=" + $rb.Contains([string][char]0x8BBF + [string][char]0x95EE))

Get-Process -Name StudentService -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Remove-Item 'C:\ProgramData\NetGuard\config.json' -Force -ErrorAction SilentlyContinue
Receive-Job $fake | ForEach-Object { Write-Host ("fake: " + $_) }
Remove-Job $fake -Force
Write-Host "=== done ==="
