$ErrorActionPreference='Stop'
$exe=Join-Path $PSScriptRoot 'ClaudeGuardTray.exe'
$existing=Get-Process -Name ClaudeGuardTray -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq $exe}
if(-not $existing){Start-Process -FilePath $exe -ArgumentList '--background' -WorkingDirectory $PSScriptRoot -WindowStyle Hidden}
