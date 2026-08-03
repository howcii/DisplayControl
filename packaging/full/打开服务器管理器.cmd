@echo off
cd /d "%~dp0"
set "SERVER_DIR="
for /f "delims=" %%D in ('dir /b /ad /o-n "01-安装包\DisplayControlServer-win-x64-*" 2^>nul') do (
  if not defined SERVER_DIR set "SERVER_DIR=%%D"
)
if not defined SERVER_DIR (
  echo 未找到 01-安装包\DisplayControlServer-win-x64-* 文件夹。
  pause
  exit /b 1
)
start "" "%~dp001-安装包\%SERVER_DIR%\DisplayControl.Server.Manager.exe"
