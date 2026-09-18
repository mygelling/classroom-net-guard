# 学生端冒烟测试：启动 StudentService --console，验证
#   1) 本地策略 API (127.0.0.1:8890/policy)
#   2) 本地代理白名单拦截（CONNECT 白名单外域名 → 403）
#   3) 与教师端 TCP 协议（hello → 下发 policy → 收到 ack）
# 用法：.\smoke-test.ps1 [-ServiceExe <path>]
param([string]$ServiceExe = "..\src\StudentService\bin\Release\net8.0-windows\StudentService.exe")
$ErrorActionPreference = "Stop"

function Send-Frame($stream, $json) {
    $b = [Text.Encoding]::UTF8.GetBytes($json)
    $len = $b.Length
    $h = [byte[]]@( ($len -shr 24) -band 0xFF, ($len -shr 16) -band 0xFF, ($len -shr 8) -band 0xFF, $len -band 0xFF )
    $stream.Write($h, 0, 4); $stream.Write($b, 0, $len); $stream.Flush()
}
function Read-Frame($stream, $timeoutMs = 3000) {
    $stream.ReadTimeout = $timeoutMs
    $h = New-Object byte[] 4; $r = 0
    while ($r -lt 4) { $n = $stream.Read($h, $r, 4 - $r); if ($n -le 0) { return $null }; $r += $n }
    $len = ($h[0] -shl 24) -bor ($h[1] -shl 16) -bor ($h[2] -shl 8) -bor $h[3]
    if ($len -le 0 -or $len -gt 1048576) { return $null }
    $b = New-Object byte[] $len; $r = 0
    while ($r -lt $len) { $n = $stream.Read($b, $r, $len - $r); if ($n -le 0) { return $null }; $r += $n }
    return [Text.Encoding]::UTF8.GetString($b)
}

$exe = (Resolve-Path $ServiceExe).Path
Write-Host "==> 启动学生端服务（前台调试模式）" -ForegroundColor Cyan
$p = Start-Process -FilePath $exe -ArgumentList "--console" -PassThru -WindowStyle Hidden
try {
    Start-Sleep -Seconds 3

    # --- 1) 策略 API ---
    Write-Host "==> [1] 本地策略 API" -ForegroundColor Cyan
    try {
        $r = Invoke-WebRequest -Uri "http://127.0.0.1:8890/policy" -UseBasicParsing -TimeoutSec 5
        Write-Host "    HTTP $($r.StatusCode)：" -NoNewline
        $json = $r.Content | ConvertFrom-Json
        Write-Host "版本 v$($json.version) · 管控=$($json.classroomOn) · 白名单=$($json.allowDomains.Count) 条 · 下载允许=$($json.download.enabled)"
        if ($json.allowDomains.Count -gt 0) { Write-Host "    白名单样本: $($json.allowDomains[0])" }
    } catch { Write-Host "    [失败] $($_.Exception.Message)" -ForegroundColor Red }

    # --- 2) 代理拦截（CONNECT 白名单外）---
    Write-Host "==> [2] 代理白名单拦截 (127.0.0.1:8888)" -ForegroundColor Cyan
    try {
        $c = New-Object System.Net.Sockets.TcpClient("127.0.0.1", 8888)
        $s = $c.GetStream()
        $req = "CONNECT www.4399.com:443 HTTP/1.1`r`nHost: www.4399.com:443`r`n`r`n"
        $b = [Text.Encoding]::ASCII.GetBytes($req)
        $s.Write($b, 0, $b.Length); $s.Flush()
        $buf = New-Object byte[] 512
        $n = $s.Read($buf, 0, 512)
        $resp = [Text.Encoding]::ASCII.GetString($buf, 0, $n)
        $first = ($resp -split "`r`n")[0]
        if ($first -match "403") { Write-Host "    [OK] 白名单外域名被拦截: $first" -ForegroundColor Green }
        else { Write-Host "    [异常] 期望 403，实际: $first" -ForegroundColor Red }
        $c.Close()
    } catch { Write-Host "    [失败] $($_.Exception.Message)" -ForegroundColor Red }

    # --- 3) 模拟教师端：hello -> 下发策略 -> 期望 ack ---
    Write-Host "==> [3] 教师端协议 (TCP 9999)" -ForegroundColor Cyan
    try {
        $tc = New-Object System.Net.Sockets.TcpClient("127.0.0.1", 9999)
        $ts = $tc.GetStream()
        Send-Frame $ts '{"t":"hello","data":{"seat":"SMOKE-PC","name":"测试机","ip":"10.0.0.9","os":"Windows"}}'
        Start-Sleep -Milliseconds 500
        Send-Frame $ts '{"t":"policy","data":{"version":88,"classroomOn":true,"allowDomains":["*.baidu.com","*.edu.cn"],"download":{"enabled":false,"allowTypes":["pdf","docx"],"maxSizeMB":50}}}'
        $ack = Read-Frame $ts
        if ($ack -and $ack -match '"t"\s*:\s*"ack"' -and $ack -match '88') {
            Write-Host "    [OK] 学生端已确认策略 v88: $ack" -ForegroundColor Green
        } else {
            Write-Host "    [异常] 未收到正确 ack: $ack" -ForegroundColor Red
        }
        $tc.Close()
    } catch { Write-Host "    [失败] $($_.Exception.Message)" -ForegroundColor Red }
}
finally {
    Write-Host "==> 停止测试进程" -ForegroundColor Cyan
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
}
