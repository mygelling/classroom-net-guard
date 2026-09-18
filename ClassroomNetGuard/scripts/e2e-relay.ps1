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
    $v1 = '{"t":"policy","data":{"version":1,"classroomOn":true,"allowDomains":["10.114.105.150","www.baidu.com"],"deviceModes":null,"devicePasswords":null,"unlockPassword":"","download":{"enabled":false,"allowTypes":[],"maxSizeMB":0},"updatedAt":"2026-09-18T14:00:00+08:00"}}'
    Write-Frame $s $v1
    Write-Output "v1 sent"
    Start-Sleep 50
    $listener.Stop()
}

Start-Sleep 1
$svc = Start-Process "$root\publish\student\StudentService.exe" -ArgumentList "--console" -PassThru
Write-Host ("student pid=" + $svc.Id)

$got = $false
for ($i=0; $i -lt 20; $i++) {
    Start-Sleep 1
    try {
        $p = (Invoke-WebRequest -Uri "http://127.0.0.1:8890/policy" -UseBasicParsing -TimeoutSec 3).Content | ConvertFrom-Json
        if ($p.allowDomains -contains "www.baidu.com") { $got = $true; break }
    } catch {}
}
Write-Host ("策略生效=" + $got)

# 走代理访问 baidu（白名单内）：期待 200 真实内容
Write-Host "=== 代理访问 baidu ==="
curl.exe -s -x http://127.0.0.1:8888 "http://www.baidu.com/" -o "$env:TEMP\ng-bd.html" -w "code=%{http_code} size=%{size_download} ttfb=%{time_starttransfer}s" --max-time 12
Write-Host ""
Write-Host ("拦截页=" + ((Get-Content "$env:TEMP\ng-bd.html" -Raw -Encoding UTF8 -ErrorAction SilentlyContinue) -match '访问已被拦截'))

# 走代理访问 150（白名单内）：期待放行（200 或连接成功响应）
Write-Host "=== 代理访问 150 ==="
curl.exe -s -x http://127.0.0.1:8888 "http://10.114.105.150/" -o "$env:TEMP\ng-150.html" -w "code=%{http_code} size=%{size_download} ttfb=%{time_starttransfer}s" --max-time 12
Write-Host ""
Write-Host ("拦截页=" + ((Get-Content "$env:TEMP\ng-150.html" -Raw -Encoding UTF8 -ErrorAction SilentlyContinue) -match '访问已被拦截'))

# 走代理访问 10.114.105.5（白名单外）：期待拦截页
Write-Host "=== 代理访问 5（白名单外）==="
curl.exe -s -x http://127.0.0.1:8888 "http://10.114.105.5/" -o "$env:TEMP\ng-5.html" -w "code=%{http_code} size=%{size_download}" --max-time 8
Write-Host ""
Write-Host ("拦截页=" + ((Get-Content "$env:TEMP\ng-5.html" -Raw -Encoding UTF8 -ErrorAction SilentlyContinue) -match '访问已被拦截'))

Get-Process -Name StudentService -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Receive-Job $fake | ForEach-Object { Write-Host ("fake: " + $_) }
Remove-Job $fake -Force
Remove-Item 'C:\ProgramData\NetGuard\config.json' -ErrorAction SilentlyContinue
Write-Host "=== 结束 ==="
