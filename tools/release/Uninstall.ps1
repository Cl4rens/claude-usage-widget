# Removes Claude Usage Widget (all builds, incl. its local cache/settings).
#   powershell -ExecutionPolicy Bypass -File Uninstall.ps1
#   powershell -ExecutionPolicy Bypass -File Uninstall.ps1 -RemoveCertificate   # also stop trusting "Cl4rens" (admin)
param([switch]$RemoveCertificate)
Get-Process ClaudeUsageWidget -ErrorAction SilentlyContinue | Stop-Process -Force
Get-AppxPackage ClaudeUsageWidget | ForEach-Object { Remove-AppxPackage $_.PackageFullName }
Remove-Item -Recurse -Force (Join-Path $env:LOCALAPPDATA 'Programs\ClaudeUsageWidget') -ErrorAction SilentlyContinue
Write-Host 'Claude Usage Widget removed.'

if ($RemoveCertificate) {
    $cer = Join-Path $PSScriptRoot 'Cl4rens.cer'
    $thumb = (New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $cer).Thumbprint
    $cmd = "Remove-Item 'Cert:\LocalMachine\TrustedPeople\$thumb' -ErrorAction SilentlyContinue"
    Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile', '-Command', $cmd
    Write-Host 'Publisher certificate "Cl4rens" is no longer trusted.'
}
