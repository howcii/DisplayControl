@echo off
setlocal EnableExtensions
chcp 65001 >nul
title 屏控助手 - ADB 启用安卓休眠权限

set "PACKAGE=com.displaycontrol.androidagent"
set "COMPONENT=com.displaycontrol.androidagent/.DisplayDeviceAdminReceiver"
set "ADB=%~dp0tools\adb\adb.exe"

if not exist "%ADB%" set "ADB=A:\CodexTools\android-sdk\platform-tools\adb.exe"
if not exist "%ADB%" (
    for %%I in (adb.exe) do set "ADB=%%~$PATH:I"
)

if not exist "%ADB%" (
    echo 未找到 adb.exe。
    echo 请使用完整 Android 安装包，或把 Android SDK platform-tools 加入 PATH。
    pause
    exit /b 1
)

echo.
echo === 屏控助手 Android 休眠权限 ADB 授权工具 ===
echo.
echo 使用前请确认：
echo 1. 大屏已安装最新版 DisplayControlAndroidAgent-v1.6.9.apk。
echo 2. 大屏已开启“开发者选项 / USB 调试”。
echo 3. 已用 USB 线连接这台电脑，并在大屏上允许 USB 调试授权。
echo.

"%ADB%" start-server >nul
echo 当前 ADB 设备：
"%ADB%" devices
echo.

set "DEVICE="
for /f "skip=1 tokens=1,2" %%A in ('"%ADB%" devices') do (
    if "%%B"=="device" if not defined DEVICE set "DEVICE=%%A"
)

if not defined DEVICE (
    echo 未发现已授权的 Android 设备。
    echo 如果列表里显示 unauthorized，请看大屏上的 USB 调试授权弹窗并允许。
    echo 如果列表为空，请检查 USB 线、USB 调试和驱动。
    pause
    exit /b 1
)

echo 使用设备：%DEVICE%
echo.

"%ADB%" -s "%DEVICE%" shell pm list packages %PACKAGE% | findstr /i "%PACKAGE%" >nul
if errorlevel 1 (
    echo 大屏上未检测到屏控助手包：%PACKAGE%
    echo 请先安装 DisplayControlAndroidAgent-v1.6.9.apk，再重新运行本工具。
    pause
    exit /b 1
)

echo 正在尝试通过 ADB 启用设备管理员权限...
set "ADMIN_OK="
call :TryActiveAdmin --user current
if not defined ADMIN_OK call :TryActiveAdmin --user 0
if not defined ADMIN_OK call :TryActiveAdmin

echo.
echo 正在尝试允许“修改系统设置”兼容休眠权限...
"%ADB%" -s "%DEVICE%" shell appops set %PACKAGE% android:write_settings allow >nul 2>nul
if errorlevel 1 "%ADB%" -s "%DEVICE%" shell appops set %PACKAGE% WRITE_SETTINGS allow >nul 2>nul

echo.
echo 权限检查结果：
"%ADB%" -s "%DEVICE%" shell dumpsys device_policy | findstr /i "displaycontrol DisplayDeviceAdminReceiver"
"%ADB%" -s "%DEVICE%" shell appops get %PACKAGE% | findstr /i "write_settings WRITE_SETTINGS"
echo.

if defined ADMIN_OK (
    echo ADB 已请求启用设备管理员权限。
    echo 请回到大屏上的“屏控助手”，点击“刷新权限状态”，再点“本机休眠测试”。
) else (
    echo ADB 未能启用设备管理员权限。
    echo 如果大屏固件仍拦截，需要进入设备所有者模式或厂商电源接口；不要反复清数据。
)

echo.
pause
exit /b 0

:TryActiveAdmin
"%ADB%" -s "%DEVICE%" shell dpm set-active-admin %* %COMPONENT%
if not errorlevel 1 set "ADMIN_OK=1"
exit /b 0
