#requires -Version 5.1
# OS-level layer under the gate: the protected Claude programs may only reach loopback (the gate).
#   status  (default) compare rules with the current program paths; exit 1 if anything is missing
#   apply   enable Windows Firewall, (re)create the rules, then prove they block with a test copy of curl
#   remove  delete the rules and restore the firewall on/off state recorded by the first apply
param([ValidateSet('status','apply','remove')][string]$Action='status')
$ErrorActionPreference='Stop'
$dir=$PSScriptRoot
$run=Join-Path $dir 'guard-runtime'
$group='Claude Guard'
$logFile=Join-Path $run 'firewall.log'
$previousFile=Join-Path $run 'firewall-previous.json'
# Everything except loopback (127.0.0.0/8, ::1). Loopback is how the programs reach the gate.
$remote=@('0.0.0.0-126.255.255.255','128.0.0.0-255.255.255.255','::2-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff')

function Log([string]$m){ $line=('{0:yyyy-MM-dd HH:mm:ss} {1}' -f (Get-Date),$m); Add-Content -Path $logFile -Value $line; Write-Output $line }
function IsAdmin { ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }

# tray-settings.json paths; Store paths are mapped onto the installed version, like the tray does.
function Targets {
  $settings=Get-Content (Join-Path $dir 'tray-settings.json') -Raw | ConvertFrom-Json
  $pkg=Get-AppxPackage -Name Claude -ErrorAction SilentlyContinue
  foreach($p in $settings.paths){
    $path=$p
    if($p -match '(?i)\\WindowsApps\\Claude_[0-9.]+_x64__pzs8sxrjxfjjc\\(.+)$'){
      if(-not $pkg){ continue }
      $path=Join-Path $pkg.InstallLocation $Matches[1]
    }
    [pscustomobject]@{ Path=$path; Exists=(Test-Path -LiteralPath $path) }
  }
}
function RulePrograms {
  @(Get-NetFirewallRule -Group $group -ErrorAction SilentlyContinue | ForEach-Object { ($_ | Get-NetFirewallApplicationFilter).Program })
}

function Status {
  $ok=$true
  foreach($p in Get-NetFirewallProfile){ if(-not $p.Enabled){ Write-Output "OFF      firewall profile $($p.Name) is disabled; rules do not apply"; $ok=$false } }
  $programs=RulePrograms
  foreach($t in Targets){
    if(-not $t.Exists){ Write-Output "ABSENT   $($t.Path) (not installed; skipped)"; continue }
    if($programs -contains $t.Path){ Write-Output "OK       $($t.Path)" } else { Write-Output "MISSING  $($t.Path)"; $ok=$false }
  }
  foreach($p in $programs){ if(-not (Test-Path -LiteralPath $p)){ Write-Output "STALE    $p (program no longer exists; run apply)"; $ok=$false } }
  if($ok){ exit 0 } else { exit 1 }
}

function AddRule([string]$name,[string]$program){
  New-NetFirewallRule -DisplayName $name -Group $group -Direction Outbound -Action Block -Program $program -RemoteAddress $remote -Profile Any | Out-Null
}

# Proves the rule shape blocks: a copy of curl under the same rule must fail direct and still work via the gate.
function SelfTest {
  $policy=Get-Content (Join-Path $dir 'guard-policy.json') -Raw | ConvertFrom-Json
  $testDir=Join-Path $env:TEMP ('claude-guard-fw-'+[guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory $testDir | Out-Null
  $curl=Join-Path $testDir 'curl.exe'; Copy-Item "$env:WINDIR\System32\curl.exe" $curl
  $name='Claude Guard self-test'
  try {
    New-NetFirewallRule -DisplayName $name -Direction Outbound -Action Block -Program $curl -RemoteAddress $remote -Profile Any | Out-Null
    & $curl -s -m 8 --noproxy '*' -o NUL https://1.1.1.1/ 2>$null
    $direct=$LASTEXITCODE
    $viaGate=(& $curl -s -m 20 -x ("http://127.0.0.1:"+$policy.proxyPort) https://api.ipify.org/ 2>$null)
    Log "self-test: direct exit code $direct (must be non-zero); via gate '$viaGate' (expected $($policy.expectedIp))"
    if($direct -eq 0){ throw 'self-test failed: direct connection was NOT blocked' }
    if($viaGate -ne $policy.expectedIp){ throw 'self-test failed: connection through the gate did not return the fixed exit IP' }
  } finally {
    Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    Remove-Item $testDir -Recurse -Force -ErrorAction SilentlyContinue
  }
}

function Apply {
  New-Item -ItemType Directory -Force $run | Out-Null
  if(-not (Test-Path $previousFile)){
    $state=@{}; foreach($p in Get-NetFirewallProfile){ $state[$p.Name]=[bool]$p.Enabled }
    $state | ConvertTo-Json | Set-Content $previousFile
    Log "recorded previous firewall state: $(($state.GetEnumerator() | ForEach-Object { $_.Key+'='+$_.Value }) -join ', ')"
  }
  Set-NetFirewallProfile -Profile Domain,Private,Public -Enabled True
  Log 'firewall enabled on all profiles'
  Get-NetFirewallRule -Group $group -ErrorAction SilentlyContinue | Remove-NetFirewallRule
  foreach($t in Targets){
    if(-not $t.Exists){ Log "skip (not installed): $($t.Path)"; continue }
    AddRule ('Claude Guard: '+(Split-Path $t.Path -Leaf)+' '+(Split-Path (Split-Path $t.Path) -Leaf)) $t.Path
    Log "blocked non-loopback outbound: $($t.Path)"
  }
  SelfTest
  Log 'apply complete'
}

function Remove {
  Get-NetFirewallRule -Group $group -ErrorAction SilentlyContinue | Remove-NetFirewallRule
  Log 'rules removed'
  if(Test-Path $previousFile){
    $state=Get-Content $previousFile -Raw | ConvertFrom-Json
    foreach($name in 'Domain','Private','Public'){ if($null -ne $state.$name){ Set-NetFirewallProfile -Profile $name -Enabled ([bool]$state.$name) } }
    Remove-Item $previousFile
    Log 'firewall on/off state restored to what it was before the first apply'
  }
}

if($Action -eq 'status'){ Status }
if(-not (IsAdmin)){
  # Elevate once; the elevated copy writes firewall.log, which is shown here afterwards.
  $before=if(Test-Path $logFile){ (Get-Content $logFile).Count } else { 0 }
  $p=Start-Process powershell.exe -Verb RunAs -Wait -PassThru -WindowStyle Hidden -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$PSCommandPath+'"'),$Action)
  if(Test-Path $logFile){ Get-Content $logFile | Select-Object -Skip $before }
  exit $p.ExitCode
}
try { if($Action -eq 'apply'){ Apply } else { Remove }; exit 0 }
catch { Log ('FAILED: '+$_.Exception.Message); exit 1 }
