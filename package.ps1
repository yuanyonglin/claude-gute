#requires -Version 5.1
# Zip the current version (git HEAD plus the deployed binaries) to the desktop.
#   -Mode private (default)  also includes this machine's real config, node credentials included.
#                            For restoring on your own machines only; never share or upload it.
#   -Mode public             templates only; refused if any local node data is found in it.
param([ValidateSet('private','public')][string]$Mode='private')
$ErrorActionPreference='Stop'
$dir=$PSScriptRoot
$node='C:\Program Files\nodejs\node.exe'
if(-not (Test-Path $node)){ $node=(Get-Command node.exe -ErrorAction Stop).Source }
$version=(git -C $dir describe --tags --always --dirty).Trim()
if($LASTEXITCODE -ne 0){ throw 'git describe failed' }
$stage=Join-Path $env:TEMP ('claude-guard-pkg-'+[guid]::NewGuid().ToString('N'))
$pkg=Join-Path $stage 'ClaudeGuard'
New-Item -ItemType Directory -Force $pkg | Out-Null
try {
  $tar=Join-Path $stage 'src.tar'
  git -C $dir archive --format=tar HEAD -o $tar
  if($LASTEXITCODE -ne 0){ throw 'git archive failed' }
  tar -xf $tar -C $pkg
  if($LASTEXITCODE -ne 0){ throw 'tar extract failed' }
  Remove-Item $tar
  foreach($f in 'ClaudeGuardTray.exe','mihomo-gate.exe','ClaudeGuard.ico','ClaudeGuard.png'){
    $p=Join-Path $dir $f
    if(-not (Test-Path $p)){ throw "missing $f; run deploy.ps1 (or build-tray.ps1) first" }
    Copy-Item $p $pkg
  }
  if($Mode -eq 'private'){
    foreach($f in 'guard-node.json','guard-policy.json','tray-settings.json'){ Copy-Item (Join-Path $dir $f) $pkg }
    $name="ClaudeGuard-$version-private-CONTAINS-CREDENTIALS.zip"
  } else {
    & $node (Join-Path $dir 'tools\check-secrets.cjs') dir $pkg
    if($LASTEXITCODE -ne 0){ throw 'public package contains local node data; nothing written' }
    $name="ClaudeGuard-$version.zip"
  }
  $zip=Join-Path ([Environment]::GetFolderPath('Desktop')) $name
  if(Test-Path $zip){ Remove-Item $zip }
  Compress-Archive -Path $pkg -DestinationPath $zip
  Write-Output "written: $zip"
} finally {
  Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}
