$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell window (Run as Administrator).'
}
$existing = Get-Service -Name 'EasyBalance' -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') { Stop-Service -Name 'EasyBalance' -Force }
& sc.exe delete EasyBalance | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Could not remove the EasyBalance service (sc.exe exit code $LASTEXITCODE)." }
Write-Host 'EasyBalance service removed.'
