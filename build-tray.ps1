$ErrorActionPreference='Stop'
$compiler='C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe /reference:System.Drawing.dll /out:"$PSScriptRoot\BrandAssets.exe" "$PSScriptRoot\BrandAssets.cs"
if($LASTEXITCODE -ne 0){throw 'Icon builder compile failed'}
& "$PSScriptRoot\BrandAssets.exe" "$PSScriptRoot\ClaudeGuard.ico"
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /win32icon:"$PSScriptRoot\ClaudeGuard.ico" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll /out:"$PSScriptRoot\ClaudeGuardTray.next.exe" "$PSScriptRoot\ClaudeGuardTray.cs" "$PSScriptRoot\Dashboard.cs" "$PSScriptRoot\NodePicker.cs"
if($LASTEXITCODE -ne 0){throw 'Tray compile failed'}
