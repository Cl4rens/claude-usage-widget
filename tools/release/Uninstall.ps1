# Removes Claude Usage Widget (package, files and its local cache/settings).
#   powershell -ExecutionPolicy Bypass -File Uninstall.ps1
Get-Process ClaudeUsageWidget -ErrorAction SilentlyContinue | Stop-Process -Force
$pkg = Get-AppxPackage ClaudeUsageWidget
if ($pkg) { Remove-AppxPackage $pkg.PackageFullName }
Remove-Item -Recurse -Force (Join-Path $env:LOCALAPPDATA 'Programs\ClaudeUsageWidget') -ErrorAction SilentlyContinue
Write-Host 'Claude Usage Widget removed.'
