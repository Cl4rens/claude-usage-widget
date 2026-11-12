# Installs Claude Usage Widget from this release folder.
# Requirements: Windows 11, Developer Mode on (Settings > System > For developers),
#               Windows App Runtime 1.8 (installed automatically by most Windows 11 systems; see README).
#   Right-click > Run with PowerShell   – or –   powershell -ExecutionPolicy Bypass -File Install.ps1
$ErrorActionPreference = 'Stop'

$dev = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -ErrorAction SilentlyContinue).AllowDevelopmentWithoutDevLicense
if ($dev -ne 1) {
    Write-Host 'Developer Mode is off. Turn it on under Settings > System > For developers, then run this again.' -ForegroundColor Yellow
    return
}

$src = Join-Path $PSScriptRoot 'app'
$manifest = [xml](Get-Content (Join-Path $src 'AppxManifest.xml') -Raw)
$ver = $manifest.Package.Identity.Version
$target = Join-Path $env:LOCALAPPDATA "Programs\ClaudeUsageWidget\$ver"

Get-Process ClaudeUsageWidget -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item -Recurse -Force "$src\*" $target

# Registering a newer version updates in place and keeps a pinned widget.
Add-AppxPackage -Register (Join-Path $target 'AppxManifest.xml') -ForceApplicationShutdown
$pkg = Get-AppxPackage ClaudeUsageWidget
Start-Process "shell:AppsFolder\$($pkg.PackageFamilyName)!App"

Write-Host "Installed $($pkg.PackageFullName)" -ForegroundColor Green
Write-Host 'Add the widget: open Widgets (Win+W) > "+" > "Claude Usage".'
