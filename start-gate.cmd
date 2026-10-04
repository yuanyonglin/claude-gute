@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-guard.ps1"
exit /b %errorlevel%
