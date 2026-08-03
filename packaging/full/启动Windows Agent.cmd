@echo off
cd /d "%~dp0"
set "AGENT_DIR="
for /f "delims=" %%D in ('dir /b /ad /o-n "01-安装包\DisplayControlAgent-win-x64-*" 2^>nul') do (
  if not defined AGENT_DIR set "AGENT_DIR=%%D"
)
if not defined AGENT_DIR (
  echo 未找到 01-安装包\DisplayControlAgent-win-x64-* 文件夹。
  pause
  exit /b 1
)
start "" "%~dp001-安装包\%AGENT_DIR%\DisplayControl.Agent.exe"
