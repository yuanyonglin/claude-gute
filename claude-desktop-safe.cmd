@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-guard.ps1"
if errorlevel 1 exit /b 1
"C:\Program Files\nodejs\node.exe" "%~dp0gate-control.cjs" check
if errorlevel 1 exit /b 1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0launch-desktop.ps1"
exit /b %errorlevel%
