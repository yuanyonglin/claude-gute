#requires -Version 5.1
# Replace the running gate and tray with the code in this folder, in one pass.
# Close every Claude window first: Claude loses its connection for ~10 s while the gate restarts.
# Order matters: the tray is stopped first and started last, only after the new gate is READY,
# so it never sees a blocked gate and never freezes Claude during the switch.
$ErrorActionPreference='Stop'
$dir=$PSScriptRoot
$run=Join-Path $dir 'guard-runtime'
$log=Join-Path $run 'deploy.log'
$node='C:\Program Files\nodejs\node.exe'
if(-not (Test-Path $node)){ $node=(Get-Command node.exe -ErrorAction Stop).Source }
$policy=Get-Content (Join-Path $dir 'guard-policy.json') -Raw | ConvertFrom-Json
$ports=@($policy.adminPort,$policy.proxyPort,$policy.corePort)

function Log([string]$m){ $line=('{0:yyyy-MM-dd HH:mm:ss} {1}' -f (Get-Date),$m); Add-Content -Path $log -Value $line; Write-Output $line }
function Listening { foreach($p in $ports){ if(Get-NetTCPConnection -State Listen -LocalPort $p -ErrorAction SilentlyContinue){ return $true } }; return $false }
function State { $o=& $node (Join-Path $dir 'gate-control.cjs') status --quiet 2>$null; if($LASTEXITCODE -ne 0){ return $null }; return (($o | Out-String) | ConvertFrom-Json).state }

try {
  New-Item -ItemType Directory -Force $run | Out-Null
  Log 'deploy started'
  # Build before touching anything that runs; a compile error leaves the old version running.
  & (Join-Path $dir 'build-tray.ps1')
  if(-not (Test-Path (Join-Path $dir 'ClaudeGuardTray.next.exe'))){ throw 'build produced no ClaudeGuardTray.next.exe' }
  Log 'tray built'

  $exe=Join-Path $dir 'ClaudeGuardTray.exe'
  Get-Process ClaudeGuardTray -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
  Start-Sleep -Milliseconds 500
  Log 'old tray stopped'

  & $node (Join-Path $dir 'gate-control.cjs') stop | Out-Null
  for($i=0;$i -lt 50 -and (Listening);$i++){ Start-Sleep -Milliseconds 200 }
  if(Listening){ throw 'old gate or dedicated core did not exit' }
  Log 'old gate stopped'

  if(Test-Path $exe){ Remove-Item $exe -Force }
  Rename-Item (Join-Path $dir 'ClaudeGuardTray.next.exe') 'ClaudeGuardTray.exe'
  Log 'tray binary replaced'

  Start-Process -FilePath $node -ArgumentList ('"'+(Join-Path $dir 'guard-daemon.cjs')+'"') -WorkingDirectory $dir -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $run 'daemon.out.log') -RedirectStandardError (Join-Path $run 'daemon.err.log')
  $s=$null; for($i=0;$i -lt 120;$i++){ Start-Sleep -Milliseconds 250; $s=State; if($s -and $s -ne 'STARTING'){ break } }
  if(-not $s){ throw 'new gate did not respond; see guard-runtime\daemon.err.log' }
  Log "new gate started: $s"

  # Stopping the gate latches a block on purpose; resume re-verifies the fixed exit before opening.
  if($s -ne 'READY'){ & $node (Join-Path $dir 'gate-control.cjs') resume | Out-Null; $s=State }
  if($s -ne 'READY'){ throw "re-verification failed; the gate stays blocked ($s)" }
  Log 'gate READY'

  Start-Process -FilePath $exe -WorkingDirectory $dir
  Log 'tray started; deploy complete'
  exit 0
} catch {
  Log ('FAILED: '+$_.Exception.Message)
  exit 1
}
