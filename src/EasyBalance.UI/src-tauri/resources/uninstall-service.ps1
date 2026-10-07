param(
    [Parameter(Mandatory = $true)]
    [string] $InstallRoot
)

$ErrorActionPreference = 'Stop'

try {
    $InstallRoot = [System.IO.Path]::GetFullPath($InstallRoot)
    $ExpectedRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
    if (-not [string]::Equals($InstallRoot.TrimEnd('\'), $ExpectedRoot.TrimEnd('\'), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The supplied uninstall root does not match this uninstaller's resources: $InstallRoot"
    }

    $ServiceName = 'EasyNetBalance'
    $ExistingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    $WasRunning = $null -ne $ExistingService -and $ExistingService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped
    if ($WasRunning) {
        Stop-Service -Name $ServiceName -Force -ErrorAction Stop
        $ExistingService = Get-Service -Name $ServiceName
        $ExistingService.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(45))
    }

    # The UI and service share one executable. Wait for the service process to
    # exit, then refuse to remove files while a separate UI window is still open.
    $ProcessDeadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $AppProcesses = @(Get-Process -Name 'EasyNetBalance' -ErrorAction SilentlyContinue)
        if ($AppProcesses.Count -eq 0) { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $ProcessDeadline)
    if ($AppProcesses.Count -gt 0) {
        if ($WasRunning) { Start-Service -Name $ServiceName -ErrorAction Stop }
        throw 'Close the EasyNetBalance app window, then retry uninstall. The service was not removed.'
    }
    if ($null -eq $ExistingService) { exit 0 }

    & "$env:SystemRoot\System32\sc.exe" delete $ServiceName
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 1060) {
        throw "Could not remove the EasyNetBalance service (sc.exe exit code $LASTEXITCODE)."
    }
    # Windows removes a stopped service asynchronously after all handles close.
    # Keep its Program Files folder if the SCM still reports it, rather than
    # deleting a binary that the service manager may still reference.
    $Deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 500
        $ExistingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($null -eq $ExistingService) { break }
    } while ([DateTime]::UtcNow -lt $Deadline)
    if ($null -ne $ExistingService) {
        throw 'Windows is still removing the service. Retry uninstall after a short wait or reboot.'
    }

    # Settings, profiles, logs, and mihomo state in %ProgramData% are preserved.
} catch {
    [Console]::Error.WriteLine("EasyNetBalance service removal failed: {0}", $_.Exception.Message)
    exit 1
}
