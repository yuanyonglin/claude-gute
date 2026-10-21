@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-guard.ps1"
if errorlevel 1 exit /b 1
set "NODE=C:\Program Files\nodejs\node.exe"
if not exist "%NODE%" for %%I in (node.exe) do set "NODE=%%~$PATH:I"
if not defined NODE (echo node.exe not found. Install Node.js or add it to PATH. & exit /b 1)
"%NODE%" "%~dp0gate-control.cjs" check
if errorlevel 1 (
  echo Claude blocked. Inspect status; run gate-control.cjs resume only after checking the cause.
  exit /b 1
)
set "HTTPS_PROXY=http://127.0.0.1:17899"
set "HTTP_PROXY=http://127.0.0.1:17899"
set "https_proxy=http://127.0.0.1:17899"
set "http_proxy=http://127.0.0.1:17899"
set "ALL_PROXY="
set "all_proxy="
set "NO_PROXY="
set "no_proxy="
set "CLAUDE_EXE=%USERPROFILE%\.local\bin\claude.exe"
if not exist "%CLAUDE_EXE%" set "CLAUDE_EXE=C:\Users\JSYL-003\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe"
if not exist "%CLAUDE_EXE%" (echo claude.exe not found. Install Claude Code first. & exit /b 1)
"%CLAUDE_EXE%" %*
exit /b %errorlevel%
