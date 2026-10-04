$ErrorActionPreference='Stop'
$node='C:\Program Files\nodejs\node.exe'
if(-not (Test-Path $node)){ $node=(Get-Command node.exe -ErrorAction Stop).Source }
$settings=Get-Content (Join-Path $PSScriptRoot 'tray-settings.json') -Raw | ConvertFrom-Json
$pkg=Get-AppxPackage -Name Claude
if(-not $pkg){throw 'Installed Claude desktop package not found.'}
$exe=Join-Path $pkg.InstallLocation 'app\claude.exe'
# Same rule as the tray: only the Store version segment may differ; publisher id and subpath stay pinned.
function Normalize([string]$path){ $path -replace '(?i)\\WindowsApps\\Claude_[0-9.]+_x64__pzs8sxrjxfjjc\\','\WindowsApps\Claude_*_x64__pzs8sxrjxfjjc\' }
if((Normalize $exe) -notin @($settings.paths | ForEach-Object { Normalize $_ })){throw 'Claude desktop path is not in tray-settings.json paths. Verify and update it before launch.'}
$existing=Get-Process -Name claude -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq $exe}
if($existing){throw 'Claude desktop is already running. Close it normally first so proxy flags can take effect.'}
& $node (Join-Path $PSScriptRoot 'gate-control.cjs') check
if($LASTEXITCODE -ne 0){throw 'Gate not READY.'}
# Electron Chromium proxy flags: native sidecars may not honor these; not an OS firewall.
Start-Process -FilePath $exe -ArgumentList '--proxy-server=http://127.0.0.1:17899','--disable-quic' -WindowStyle Hidden
