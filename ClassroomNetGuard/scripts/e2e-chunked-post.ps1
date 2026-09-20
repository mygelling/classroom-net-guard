# E2E: chunked 响应完整转发 + POST 请求体完整转发（回归：ERR_INCOMPLETE_CHUNKED_ENCODING / ERR_CONNECTION_RESET）
$ErrorActionPreference = 'Continue'
$log = 'C:\ProgramData\NetGuard\logs\student-service-20260920.log'
if (Test-Path $log) { Remove-Item $log -Force }
$bodyFile = 'C:\ProgramData\NetGuard\logs\e2e-post-body.txt'
if (Test-Path $bodyFile) { Remove-Item $bodyFile -Force }
[IO.File]::WriteAllText('C:\ProgramData\NetGuard\config.json', '{"TeacherHost":"127.0.0.1","TeacherPort":19999,"ProxyPort":8888,"LocalApiPort":8890}')

# 假教师端（Python 进程，监听 19999，避开真实 9999）
$fake = Start-Process python -ArgumentList "C:\Users\admin\Desktop\上网控制\ClassroomNetGuard\scripts\e2e-fake-teacher.py" -PassThru -WindowStyle Hidden
Start-Sleep 2
# 假上游（Python 进程，监听 18080）
$py = Start-Process python -ArgumentList "C:\Users\admin\Desktop\上网控制\ClassroomNetGuard\scripts\e2e-chunked-post.py" -PassThru -WindowStyle Hidden
Start-Sleep 2
$svc = Start-Process "C:\Users\admin\Desktop\上网控制\ClassroomNetGuard\publish\student\StudentService.exe" -ArgumentList "--console" -PassThru
Start-Sleep 10

$exp = "hello-chunk-1|world-chunk-2|end"
Write-Host "=== 1) chunked 响应完整转发（期待 body 完整、HTTP 200） ==="
$tmp = "$env:TEMP\ng-chunked.txt"
curl.exe -s -x http://127.0.0.1:8888 "http://127.0.0.1:18080/chunked" -o $tmp -w "HTTP=%{http_code}`n" --max-time 15
$got = [IO.File]::ReadAllText($tmp)
Write-Host ("chunked body: [" + $got + "]")
Write-Host ("chunked match: " + ($got -eq $exp))
Write-Host ("chunked len: " + $got.Length + " expected: " + $exp.Length)

Write-Host "=== 2) POST body 完整转发（期待上游收到完整 JSON body） ==="
$postBody = '{"user":"zhangsan","pwd":"1234567890abcdef","data":"x" * 500}'
curl.exe -s -x http://127.0.0.1:8888 -X POST -d $postBody "http://127.0.0.1:18080/echo" -w "`nHTTP=%{http_code}`n" --max-time 15
Start-Sleep 1
$got2 = ""
if (Test-Path $bodyFile) { $got2 = [IO.File]::ReadAllText($bodyFile) }
Write-Host ("upstream received len: " + $got2.Length + " expected: " + $postBody.Length)
Write-Host ("post match: " + ($got2 -eq $postBody))

Stop-Process -Name StudentService -Force -ErrorAction SilentlyContinue
Stop-Process -Id $py.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $fake.Id -Force -ErrorAction SilentlyContinue
Remove-Item 'C:\ProgramData\NetGuard\config.json' -Force -ErrorAction SilentlyContinue
Write-Host "=== done ==="
