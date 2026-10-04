@echo off
rem Pre-flight check before starting Claude Code.
rem Exit 0 = safe to launch; non-zero = do NOT launch.
set "NODE=C:\Program Files\nodejs\node.exe"
if not exist "%NODE%" for %%I in (node.exe) do set "NODE=%%~$PATH:I"
if not defined NODE (echo node.exe not found. Install Node.js or add it to PATH. & exit /b 1)
"%NODE%" "%~dp0gate-control.cjs" check
exit /b %errorlevel%
