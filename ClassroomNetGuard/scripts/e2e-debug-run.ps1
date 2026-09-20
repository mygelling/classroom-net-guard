# 原始字节调试：chunked + POST 分片
$ErrorActionPreference = 'Continue'
[IO.File]::WriteAllText('C:\ProgramData\NetGuard\config.json', '{"TeacherHost":"127.0.0.1","TeacherPort":19999,"ProxyPort":8888,"LocalApiPort":8890}')
$f = Start-Process python -ArgumentList "C:\Users\admin\Desktop\上网控制\ClassroomNetGuard\scripts\e2e-fake-teacher.py" -PassThru -WindowStyle Hidden
$p = Start-Process python -ArgumentList "C:\Users\admin\Desktop\上网控制\ClassroomNetGuard\scripts\e2e-chunked-post.py" -PassThru -WindowStyle Hidden
Start-Sleep 2
$s = Start-Process "C:\Users\admin\Desktop\上网控制\ClassroomNetGuard\publish\student\StudentService.exe" -ArgumentList "--console" -PassThru
Start-Sleep 9
Write-Host "=== 8888 listener before raw ==="
netstat -ano | Select-String ":8888" | Select-Object -First 5
python "C:\Users\admin\Desktop\上网控制\ClassroomNetGuard\scripts\e2e-debug-raw.py"
Write-Host "=== 8888 listener after raw ==="
netstat -ano | Select-String ":8888" | Select-Object -First 5
Write-Host "=== StudentService procs ==="
Get-Process -Name StudentService -ErrorAction SilentlyContinue | Select-Object Id, StartTime, Path
Stop-Process -Name StudentService -Force -ErrorAction SilentlyContinue
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $f.Id -Force -ErrorAction SilentlyContinue
Write-Host "=== debug done ==="
