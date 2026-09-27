param(
    [Parameter(Mandatory = $true)]
    [string] $ServiceName
)

$ErrorActionPreference = 'Stop'

try {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -eq $service -or $service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Stopped) { exit 0 }
    & "$env:SystemRoot\System32\sc.exe" stop $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 1062 -and $LASTEXITCODE -ne 1060) {
        throw "sc.exe stop returned exit code $LASTEXITCODE."
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    do {
        Start-Sleep -Milliseconds 500
        $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($null -eq $service -or $service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Stopped) { exit 0 }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'The service did not stop within 90 seconds.'
} catch {
    [Console]::Error.WriteLine("Could not stop service {0}: {1}", $ServiceName, $_.Exception.Message)
    exit 1
}
