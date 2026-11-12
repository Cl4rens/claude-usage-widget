# Developer install: builds a SIGNED package with a fresh version number and installs it as an update.
# (Signed builds can't be replaced by a loose developer registration, so the dev loop uses the same
#  signed .msix as the releases; an update keeps a pinned widget.)
# Requires: the signing certificate "CN=Cl4rens" with private key in Cert:\CurrentUser\My, and the
# public certificate trusted on this PC (run the release Install.ps1 once).
#   powershell -ExecutionPolicy Bypass -File install.ps1            build + install / update
#   powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall remove again
param([switch]$Uninstall)
$ErrorActionPreference = 'Stop'

if ($Uninstall) {
    & (Join-Path $PSScriptRoot 'tools\release\Uninstall.ps1')
    return
}

# version that is always higher than the last one: 1.<day of 2026+>.<minute of day>
$now = Get-Date
$ver = "1.$(($now - [datetime]'2026-01-01').Days).$([int][Math]::Floor($now.TimeOfDay.TotalMinutes))"
& (Join-Path $PSScriptRoot 'tools\make-release.ps1') -Version $ver | Out-Host

$msix = Join-Path $PSScriptRoot "dist\ClaudeUsageWidget-$ver.msix"
Get-Process ClaudeUsageWidget -ErrorAction SilentlyContinue | Stop-Process -Force
Add-AppxPackage -Path $msix -ForceApplicationShutdown
$pkg = Get-AppxPackage ClaudeUsageWidget
Start-Process "shell:AppsFolder\$($pkg.PackageFamilyName)!App"

# keep dist\ small: drop the dev builds again
Get-ChildItem (Join-Path $PSScriptRoot 'dist') -Filter "ClaudeUsageWidget-$ver*" | Remove-Item -Recurse -Force
"Installiert: $($pkg.PackageFullName)"
