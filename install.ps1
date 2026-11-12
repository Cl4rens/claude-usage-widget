# Builds the app and registers it as a (developer-mode) package so it appears in the Widgets board.
# Requires: Settings → System → For developers → Developer Mode = On
#   powershell -ExecutionPolicy Bypass -File install.ps1            install / update (keeps pinned widgets)
#   powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall remove again
param([switch]$Uninstall)
$ErrorActionPreference = 'Stop'
$target = Join-Path $env:LOCALAPPDATA 'Programs\ClaudeUsageWidget'

if ($Uninstall) {
    Get-Process ClaudeUsageWidget -ErrorAction SilentlyContinue | Stop-Process -Force
    $existing = Get-AppxPackage ClaudeUsageWidget
    if ($existing) { Remove-AppxPackage $existing.PackageFullName }
    Remove-Item -Recurse -Force $target -ErrorAction SilentlyContinue
    'Claude Usage entfernt.'
    return
}

$dev = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -ErrorAction SilentlyContinue).AllowDevelopmentWithoutDevLicense
if ($dev -ne 1) { throw 'Bitte zuerst den Entwicklermodus einschalten: Einstellungen → System → Für Entwickler.' }

Push-Location $PSScriptRoot
try {
    dotnet build -c Release | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Build fehlgeschlagen' }
} finally { Pop-Location }

# Update = register a NEWER version from a fresh folder. Never remove the package (Windows would
# drop the pinned widget) and never overwrite the running folder (the Widgets host keeps it locked).
$now = Get-Date
$ver = "1.$(($now - [datetime]'2026-01-01').Days).$([int][Math]::Floor($now.TimeOfDay.TotalMinutes)).$($now.Second)"
$dest = Join-Path $target $ver
$out = Join-Path $PSScriptRoot 'bin\x64\Release\net10.0-windows10.0.22621.0\win-x64'
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item -Recurse -Force "$out\*" $dest
$manifest = Join-Path $dest 'AppxManifest.xml'
$xml = (Get-Content $manifest -Raw) -replace '(<Identity [^>]*Version=")[^"]+', ('${1}' + $ver)
[IO.File]::WriteAllText($manifest, $xml)

Add-AppxPackage -Register $manifest -ForceApplicationShutdown
$pkg = Get-AppxPackage ClaudeUsageWidget
Start-Process "shell:AppsFolder\$($pkg.PackageFamilyName)!App"

# clean up older version folders (ignore ones still locked)
Get-ChildItem $target -Directory | Where-Object { $_.FullName -ne $dest } |
    ForEach-Object { Remove-Item -Recurse -Force $_.FullName -ErrorAction SilentlyContinue }
Get-ChildItem $target -File | Remove-Item -Force -ErrorAction SilentlyContinue

"Installiert: $($pkg.PackageFullName)"
'Widget hinzufügen (falls noch nicht da): Widgets öffnen (Win+W) → "+" → "Claude Usage".'
