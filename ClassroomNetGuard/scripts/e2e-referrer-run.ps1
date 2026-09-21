# -*- coding: utf-8 -*-
"""E2E 调试 harness：Referer 资源放行。
启动：假教师端(e2e-referrer.py 内含) + 学生服务 --console -> 跑 e2e-referrer.py 客户端 -> 停全部进程。
config.json 用 PascalCase 键，教师端口 19999（避开真实 9999）。
"""
$ErrorActionPreference = "Continue"
$root = "C:\Users\admin\Desktop\上网控制\ClassroomNetGuard"
$publish = "$root\publish\student"
$cfg = "C:\ProgramData\NetGuard\config.json"
$log = "C:\ProgramData\NetGuard\logs\student-service-20260920.log"

# 清理残留
Get-Process -Name StudentService -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep 1

# 清理旧日志
if (Test-Path $log) { Remove-Item $log -Force }
if (Test-Path $cfg) { Remove-Item $cfg -Force }

# 写配置（PascalCase）
New-Item -ItemType Directory -Force -Path "C:\ProgramData\NetGuard\logs" | Out-Null
'{"TeacherHost":"127.0.0.1","TeacherPort":19999,"ProxyPort":8888,"LocalApiPort":8890}' |
    Out-File -FilePath $cfg -Encoding utf8

# 启动学生服务
$svc = Start-Process -FilePath "$publish\StudentService.exe" -ArgumentList "--console" -PassThru -WindowStyle Hidden
Write-Host "student pid: $($svc.Id)"

# 跑客户端（内含假教师端 + 上游 + 断言）
python "$root\scripts\e2e-referrer.py"

# 停进程
Stop-Process -Id $svc.Id -Force -ErrorAction SilentlyContinue
Start-Sleep 1
Remove-Item $cfg -Force -ErrorAction SilentlyContinue
Write-Host "=== done ==="
