@echo off
rem Point Claude Code's own settings at the gate: HTTPS_PROXY plus the guard hooks.
rem Safe to run repeatedly; anything that rewrites ~/.claude/settings.json (e.g. Orca) is repaired here.
set "NODE=C:\Program Files\nodejs\node.exe"
if not exist "%NODE%" for %%I in (node.exe) do set "NODE=%%~$PATH:I"
if not defined NODE (echo node.exe not found. Install Node.js or add it to PATH. & exit /b 1)
"%NODE%" "%~dp0install-claude-settings.cjs" %*
exit /b %errorlevel%
