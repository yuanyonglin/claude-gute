#requires -Version 5.1
$ErrorActionPreference='Stop'
$dir=$PSScriptRoot
$node='C:\Program Files\nodejs\node.exe'
& (Join-Path $dir 'start-tray.ps1')
$existing=& $node (Join-Path $dir 'gate-control.cjs') status --quiet
if ($LASTEXITCODE -eq 0) {
  if (($existing | ConvertFrom-Json).state -eq 'STARTING') { throw 'Guard is still starting. Retry shortly.' }
  exit 0
}
$run=Join-Path $dir 'guard-runtime'
New-Item -ItemType Directory -Path $run -Force | Out-Null
$p=Start-Process -FilePath $node -ArgumentList ('"'+(Join-Path $dir 'guard-daemon.cjs')+'"') -WorkingDirectory $dir -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $run 'daemon.out.log') -RedirectStandardError (Join-Path $run 'daemon.err.log')
for($i=0;$i -lt 60;$i++) {
  Start-Sleep -Milliseconds 200
  $status=& $node (Join-Path $dir 'gate-control.cjs') status --quiet
  if($LASTEXITCODE -eq 0 -and ($status | ConvertFrom-Json).state -ne 'STARTING'){ exit 0 }
  if($p.HasExited){ throw 'Guard startup failed; inspect guard-runtime/daemon.err.log' }
}
throw 'Guard startup timed out; Claude must remain blocked.'
