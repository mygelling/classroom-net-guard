# 一键发布脚本：生成教师端 / 学生端 / 托盘 可执行文件
# 自包含发布（win-x64），目标机无需安装 .NET 运行时。
# 用法（任意 PowerShell，需安装 .NET SDK 8.0+）：.\publish.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "publish"

Write-Host "发布教师端..." -ForegroundColor Cyan
dotnet publish (Join-Path $root "src\TeacherConsole\TeacherConsole.csproj") `
    -c Release -r win-x64 --self-contained true -o (Join-Path $out "teacher")

Write-Host "发布学生端服务..." -ForegroundColor Cyan
dotnet publish (Join-Path $root "src\StudentService\StudentService.csproj") `
    -c Release -r win-x64 --self-contained true -o (Join-Path $out "student")

Write-Host "发布学生端托盘..." -ForegroundColor Cyan
dotnet publish (Join-Path $root "src\StudentTray\StudentTray.csproj") `
    -c Release -r win-x64 --self-contained true -o (Join-Path $out "student")

Write-Host ""
Write-Host "发布完成：" -ForegroundColor Green
Write-Host "  教师端: $out\teacher\TeacherConsole.exe（拷到教师机运行）"
Write-Host "  学生端: $out\student\StudentService.exe + StudentTray.exe（拷到学生机）"
Write-Host "  扩展  : 工程根目录 extension\（加载到 Chrome/Edge）"
