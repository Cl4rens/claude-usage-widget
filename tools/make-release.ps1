# Builds a signed release:  dist\ClaudeUsageWidget-<version>.zip  (+ .msix and .cer next to it)
#   powershell -ExecutionPolicy Bypass -File tools\make-release.ps1 -Version 1.2.0
# Needs the signing certificate "CN=Cl4rens" (with private key) in Cert:\CurrentUser\My
# and the Windows SDK (makeappx.exe / signtool.exe).
param([string]$Version = '1.0.0', [string]$Publisher = 'CN=Cl4rens')
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

$sdk = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Directory -Filter '10.*' |
    Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName 'x64' } |
    Where-Object { Test-Path (Join-Path $_ 'signtool.exe') } | Select-Object -First 1
if (-not $sdk) { throw 'Windows SDK (signtool.exe / makeappx.exe) not found.' }

$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Publisher -and $_.HasPrivateKey } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) { throw "Signing certificate '$Publisher' with private key not found in Cert:\CurrentUser\My." }

Push-Location $root
try {
    dotnet build ClaudeUsageWidget.csproj -c Release | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
} finally { Pop-Location }

$out = Join-Path $root 'bin\x64\Release\net10.0-windows10.0.22621.0\win-x64'
$name = "ClaudeUsageWidget-$Version"
$dist = Join-Path $root 'dist'
$layout = Join-Path $dist "$name-layout"
$stage = Join-Path $dist $name
Remove-Item -Recurse -Force $layout, $stage -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $layout, $stage | Out-Null

# 1) package layout: the app without debug symbols / build recipes (they contain local build paths)
Copy-Item -Recurse -Force "$out\*" $layout
Get-ChildItem $layout -Recurse -Include *.pdb, *.appxrecipe | Remove-Item -Force
$m = Join-Path $layout 'AppxManifest.xml'
$xml = (Get-Content $m -Raw) -replace '(<Identity [^>]*Version=")[^"]+', ('${1}' + $Version + '.0')
if ($xml -notmatch [regex]::Escape("Publisher=""$Publisher""")) { throw "AppxManifest publisher is not $Publisher" }
[IO.File]::WriteAllText($m, $xml)

$needles = @($env:USERNAME, $env:USERPROFILE, (Split-Path $root -Parent))
$leak = Get-ChildItem $layout -Recurse -File | Select-String -Pattern $needles -SimpleMatch -CaseSensitive -List -ErrorAction SilentlyContinue
if ($leak) { throw "Package contains local paths / user name in: $($leak.Path -join ', ')" }

# 2) pack + sign (with timestamp, so the signature stays valid after the certificate expires)
$msix = Join-Path $stage "$name.msix"
& "$sdk\makeappx.exe" pack /d $layout /p $msix /o | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'makeappx failed' }
& "$sdk\signtool.exe" sign /fd SHA256 /sha1 $cert.Thumbprint /s My /tr http://timestamp.digicert.com /td SHA256 $msix | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'signtool failed' }

# 3) release folder: msix, public certificate, scripts, license
Copy-Item (Join-Path $PSScriptRoot 'release\Cl4rens.cer'), (Join-Path $PSScriptRoot 'release\Install.ps1'),
          (Join-Path $PSScriptRoot 'release\Uninstall.ps1'), (Join-Path $root 'LICENSE') $stage
Remove-Item -Recurse -Force $layout

$zip = Join-Path $dist "$name.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path "$stage\*" -DestinationPath $zip
Copy-Item $msix, (Join-Path $stage 'Cl4rens.cer') $dist -Force
"Release: $zip"
"Signed:  $msix"
