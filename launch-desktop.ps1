$ErrorActionPreference='Stop'
$settings=Get-Content (Join-Path $PSScriptRoot 'tray-settings.json') -Raw | ConvertFrom-Json
$pkg=Get-AppxPackage -Name Claude
if(-not $pkg){throw 'Installed Claude desktop package not found.'}
$exe=Join-Path $pkg.InstallLocation 'app\claude.exe'
if($exe -notin $settings.paths){throw 'Claude desktop was updated. Verify and update tray-settings.json paths before launch.'}
$existing=Get-Process -Name claude -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq $exe}
if($existing){throw 'Claude desktop is already running. Close it normally first so proxy flags can take effect.'}
& 'C:\Program Files\nodejs\node.exe' (Join-Path $PSScriptRoot 'gate-control.cjs') check
if($LASTEXITCODE -ne 0){throw 'Gate not READY.'}
# Electron Chromium proxy flags: native sidecars may not honor these; not an OS firewall.
Start-Process -FilePath $exe -ArgumentList '--proxy-server=http://127.0.0.1:17899','--disable-quic' -WindowStyle Hidden
