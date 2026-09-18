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
    $v1 = '{"t":"policy","data":{"version":1,"classroomOn":true,"allowDomains":["10.114.105.150","www.baidu.com"],"deviceModes":null,"devicePasswords":null,"unlockPassword":"","download":{"enabled":false,"allowTypes":[],"maxSizeMB":0},"updatedAt":"2026-09-18T14:00:00+08:00"}}'
    Write-Frame $s $v1
    Write-Output "v1 sent (含 10.114.105.150 + www.baidu.com)"
    Start-Sleep 45
    $listener.Stop()
    Write-Output "fake done"
}

Start-Sleep 1
$svc = Start-Process "$root\publish\student\StudentService.exe" -ArgumentList "--console" -PassThru
Write-Host ("student pid=" + $svc.Id)

$got = $false
for ($i=0; $i -lt 20; $i++) {
    Start-Sleep 1
    try {
        $p = (Invoke-WebRequest -Uri "http://127.0.0.1:8890/policy" -UseBasicParsing -TimeoutSec 3).Content | ConvertFrom-Json
        if ($p.allowDomains -contains "10.114.105.150") { $got = $true; Write-Host ("策略生效 v" + $p.version); break }
    } catch {}
}
if (-not $got) { Write-Host "FAIL 策略未生效" }

# 1) 白名单内真实外网域名 www.baidu.com → 期待真实放行（200 内容，非拦截页）
curl.exe -s -x http://127.0.0.1:8888 "http://www.baidu.com/" -o "$env:TEMP\ng-baidu.html" -w "code=%{http_code} len=%{size_download} " --max-time 10
$rb = Get-Content "$env:TEMP\ng-baidu.html" -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
Write-Host ("| 白名单 baidu -> 拦截页=" + ($rb -match '访问已被拦截'))

# 2) 白名单内 IP 10.114.105.150 → 期待放行路径（本机若连不上 10.x 网段则连接失败，但绝不是拦截页）
curl.exe -s -x http://127.0.0.1:8888 "http://10.114.105.150/" -o "$env:TEMP\ng-ip150.html" -w "code=%{http_code} len=%{size_download} " --max-time 8
$ri = Get-Content "$env:TEMP\ng-ip150.html" -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
Write-Host ("| 白名单 IP 150 -> 拦截页=" + ($ri -match '访问已被拦截'))

# 3) 白名单外 IP 10.114.105.5 → 期待拦截页
curl.exe -s -x http://127.0.0.1:8888 "http://10.114.105.5/" -o "$env:TEMP\ng-ip5.html" -w "code=%{http_code} len=%{size_download} " --max-time 8
$r5 = Get-Content "$env:TEMP\ng-ip5.html" -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
Write-Host ("| 白名单外 IP 5 -> 拦截页=" + ($r5 -match '访问已被拦截'))

Get-Process -Name StudentService -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Receive-Job $fake | ForEach-Object { Write-Host ("fake: " + $_) }
Remove-Job $fake -Force
Remove-Item 'C:\ProgramData\NetGuard\config.json' -ErrorAction SilentlyContinue
Write-Host "=== 日志尾部 ==="
Get-Content $log -Tail 8 -Encoding Default -ErrorAction SilentlyContinue
Write-Host "=== 结束 ==="
