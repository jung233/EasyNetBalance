$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell window (Run as Administrator).'
}

$packageRoot = $PSScriptRoot
$servicePath = Join-Path $packageRoot 'Service\EasyBalance.Service.exe'
if (-not (Test-Path -LiteralPath $servicePath -PathType Leaf)) {
    throw "Service executable was not found: $servicePath"
}

$escapedPath = $servicePath.Replace('"', '\"')
$existing = Get-Service -Name 'EasyBalance' -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne 'Stopped') { Stop-Service -Name 'EasyBalance' -Force }
    & sc.exe config EasyBalance binPath= "`"$escapedPath`"" start= auto | Out-Host
} else {
    & sc.exe create EasyBalance binPath= "`"$escapedPath`"" start= auto DisplayName= "EasyBalance routing service" | Out-Host
}
if ($LASTEXITCODE -ne 0) { throw "Could not register the EasyBalance service (sc.exe exit code $LASTEXITCODE)." }
& sc.exe start EasyBalance | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Could not start the EasyBalance service (sc.exe exit code $LASTEXITCODE)." }
Write-Host 'EasyBalance service installed and started.'
