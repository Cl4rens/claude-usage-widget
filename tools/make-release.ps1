# Builds a release zip: dist\ClaudeUsageWidget-<version>.zip
#   powershell -ExecutionPolicy Bypass -File tools\make-release.ps1 -Version 1.0.0
param([string]$Version = '1.0.0')
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

Push-Location $root
try {
    dotnet build ClaudeUsageWidget.csproj -c Release | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
} finally { Pop-Location }

$out = Join-Path $root 'bin\x64\Release\net10.0-windows10.0.22621.0\win-x64'
$name = "ClaudeUsageWidget-$Version"
$stage = Join-Path $root "dist\$name"
Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$stage\app" | Out-Null

# copy the app, but no debug symbols / build recipes (they contain local build paths)
Copy-Item -Recurse -Force "$out\*" "$stage\app"
Get-ChildItem "$stage\app" -Recurse -Include *.pdb, *.appxrecipe, *.build.appxrecipe | Remove-Item -Force

# stamp the package version
$m = Join-Path $stage 'app\AppxManifest.xml'
$xml = (Get-Content $m -Raw) -replace '(<Identity [^>]*Version=")[^"]+', ('${1}' + $Version + '.0')
[IO.File]::WriteAllText($m, $xml)

Copy-Item (Join-Path $PSScriptRoot 'release\Install.ps1'), (Join-Path $PSScriptRoot 'release\Uninstall.ps1') $stage
Copy-Item (Join-Path $root 'LICENSE') $stage

# make sure nothing machine-specific slipped in
$needles = @($env:USERNAME, $env:USERPROFILE, (Split-Path $root -Parent))
$leak = Get-ChildItem $stage -Recurse -File | Select-String -Pattern $needles -SimpleMatch -List -ErrorAction SilentlyContinue
if ($leak) { throw "Release contains local paths / user name in: $($leak.Path -join ', ')" }

$zip = Join-Path $root "dist\$name.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path "$stage\*" -DestinationPath $zip
"Release: $zip"
