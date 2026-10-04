@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-guard.ps1"
if errorlevel 1 exit /b 1
"C:\Program Files\nodejs\node.exe" "%~dp0gate-control.cjs" check
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
"C:\Users\JSYL-003\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe" %*
exit /b %errorlevel%
