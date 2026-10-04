@echo off
rem Pre-flight check before starting Claude Code.
rem Exit 0 = safe to launch; non-zero = do NOT launch.
"C:\Program Files\nodejs\node.exe" "%~dp0gate-control.cjs" check
exit /b %errorlevel%
