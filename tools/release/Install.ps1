# Installs Claude Usage Widget from this release folder – no Developer Mode needed.
#   Right-click > Run with PowerShell   – or –   powershell -ExecutionPolicy Bypass -File Install.ps1
#
# What it does:
#   1. checks that the Windows App Runtime 1.8 is installed
#   2. trusts the publisher certificate "Cl4rens" (Cl4rens.cer, public part only) –
#      once, for this PC; Windows asks for administrator rights for this step
#   3. removes builds from the old test publisher (keeps your display settings)
#   4. installs / updates ClaudeUsageWidget-*.msix and starts it
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$msix = Get-ChildItem $here -Filter 'ClaudeUsageWidget-*.msix' | Select-Object -First 1
$cer = Join-Path $here 'Cl4rens.cer'
if (-not $msix -or -not (Test-Path $cer)) { throw 'ClaudeUsageWidget-*.msix or Cl4rens.cer missing next to Install.ps1.' }

# 1) runtime
if (-not (Get-AppxPackage Microsoft.WindowsAppRuntime.1.8 | Where-Object Architecture -eq 'X64')) {
    Write-Host 'The Windows App Runtime 1.8 (x64) is missing. Install it from' -ForegroundColor Yellow
    Write-Host '  https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads' -ForegroundColor Yellow
    Write-Host 'and run this script again.' -ForegroundColor Yellow
    return
}

# 2) publisher certificate → Local Machine \ Trusted People
$thumb = (New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $cer).Thumbprint
if (-not (Test-Path "Cert:\LocalMachine\TrustedPeople\$thumb")) {
    Write-Host 'Windows will ask once for administrator rights to trust the publisher certificate "Cl4rens".'
    $cmd = "Import-Certificate -FilePath '$cer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null"
    Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile', '-Command', $cmd
    if (-not (Test-Path "Cert:\LocalMachine\TrustedPeople\$thumb")) { throw 'The certificate was not trusted (cancelled?). Nothing installed.' }
}

# 3) older builds were published as "CN=ClaudeUsageWidgetDev" – Windows treats them as a different app
$settings = $null
foreach ($old in Get-AppxPackage ClaudeUsageWidget | Where-Object { $_.Publisher -ne 'CN=Cl4rens' }) {
    $s = Join-Path $env:LOCALAPPDATA "Packages\$($old.PackageFamilyName)\LocalCache\Local\ClaudeUsageWidget\settings.json"
    if (Test-Path $s) { $settings = Get-Content $s -Raw }
    Get-Process ClaudeUsageWidget -ErrorAction SilentlyContinue | Stop-Process -Force
    Remove-AppxPackage $old.PackageFullName
    Write-Host "Removed old test build $($old.PackageFullName)."
}

# 4) install / update
Get-Process ClaudeUsageWidget -ErrorAction SilentlyContinue | Stop-Process -Force
Add-AppxPackage -Path $msix.FullName -ForceApplicationShutdown
$pkg = Get-AppxPackage ClaudeUsageWidget | Where-Object Publisher -eq 'CN=Cl4rens'
if ($settings) {
    $dir = Join-Path $env:LOCALAPPDATA "Packages\$($pkg.PackageFamilyName)\LocalCache\Local\ClaudeUsageWidget"
    New-Item -ItemType Directory -Force $dir | Out-Null
    [IO.File]::WriteAllText((Join-Path $dir 'settings.json'), $settings)
}
Start-Process "shell:AppsFolder\$($pkg.PackageFamilyName)!App"

Write-Host "Installed $($pkg.PackageFullName)" -ForegroundColor Green
Write-Host 'Add the widget: open Widgets (Win+W) > "+" > "Claude Usage".'
