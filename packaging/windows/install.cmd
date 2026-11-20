@echo off
setlocal
cd /d "%~dp0"
echo Starting NVMe Sentinel Installation...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
pause
