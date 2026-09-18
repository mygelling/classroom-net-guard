@echo off
rem ===== Student NetGuard installer (uninstall old, install new) =====

net session >nul 2>&1
if %errorlevel% neq 0 (
    echo 正在请求管理员权限，请在弹出窗口中选择“是”...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

set DEST=C:\Program Files\NetGuard
set SRC=%~dp0

echo.
echo  ============================================================
echo   上网管控学生端 安装
echo ============================================================
echo.
set TEACHER_IP=
set /p TEACHER_IP=请输入教师端 IP（直接回车=开机自动搜索教师机）：
echo.
echo 正在卸载旧版本并安装新版本...
powershell -NoProfile -ExecutionPolicy Bypass -File "%SRC%install.ps1" -Dest "%DEST%" -Zip "%SRC%student.zip" -TeacherHost "%TEACHER_IP%"
if %errorlevel% neq 0 (
    echo [错误] 安装失败，请查看上方错误信息。
    pause
    exit /b 1
)

echo 无需安装浏览器扩展，下载管控由本地代理完成。

echo.
echo ============================================================
echo   上网管控学生端 安装完成！
echo.
echo   - 已先卸载旧版本（服务 / 托盘 / 自启 / 旧文件）
echo   - 管控服务  NetGuardStudentService  已安装并启动
echo   - 托盘程序  已启动（系统代理已指向本地白名单代理）
echo   - 程序目录  %DEST%
echo.
echo   注意：如果浏览器已经打开，请关闭后重新打开浏览器。
echo.
echo   无需安装 Edge 扩展，也没有“开发人员模式”提示。
echo ============================================================
echo.
pause
