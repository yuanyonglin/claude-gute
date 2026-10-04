@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-guard.ps1"
if errorlevel 1 exit /b 1
set "NODE=C:\Program Files\nodejs\node.exe"
if not exist "%NODE%" for %%I in (node.exe) do set "NODE=%%~$PATH:I"
if not defined NODE (echo node.exe not found. Install Node.js or add it to PATH. & exit /b 1)
"%NODE%" "%~dp0gate-control.cjs" check
if errorlevel 1 exit /b 1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0launch-desktop.ps1"
exit /b %errorlevel%
