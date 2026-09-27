param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('EasyBalance', 'EasyNetBalance')]
    [string] $ServiceName,

    [Parameter(Mandatory = $true)]
    [string] $LogPath
)

$ErrorActionPreference = 'Stop'

function Write-StopLog([string] $Message) {
    Add-Content -LiteralPath $LogPath -Value ("{0:u} {1}: {2}" -f [DateTime]::UtcNow, $ServiceName, $Message)
}

try {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -eq $service -or $service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        Write-StopLog 'Already stopped or not installed.'
        exit 0
    }

    Write-StopLog "Initial status: $($service.Status)."
    $scOutput = & "$env:SystemRoot\System32\sc.exe" stop $ServiceName 2>&1 | Out-String
    $scExit = $LASTEXITCODE
    Write-StopLog "sc.exe stop exit code: $scExit. $($scOutput.Trim())"
    if ($scExit -notin @(0, 1062, 1061, 1052)) {
        throw "Windows rejected the stop request (sc.exe exit code $scExit)."
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 500
        $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($null -eq $service -or $service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
            Write-StopLog 'Stopped gracefully.'
            exit 0
        }
    } while ([DateTime]::UtcNow -lt $deadline)

    # Older builds may remain in a network probe while waiting for the scheduler.
    # Terminate only a dedicated service process reported by the SCM.
    $query = & "$env:SystemRoot\System32\sc.exe" queryex $ServiceName 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or $query -notmatch '(?m)^\s*TYPE\s*:\s*10\s+WIN32_OWN_PROCESS' -or
        $query -notmatch '(?m)^\s*PID\s*:\s*(\d+)') {
        throw 'The service did not stop within 30 seconds and its dedicated process could not be identified safely.'
    }
    $serviceProcessId = [int]$Matches[1]
    if ($serviceProcessId -le 0) { throw 'The service has no valid process ID.' }
    Write-StopLog "Graceful stop timed out; terminating dedicated service process $serviceProcessId."
    $taskOutput = & "$env:SystemRoot\System32\taskkill.exe" /PID $serviceProcessId /T /F 2>&1 | Out-String
    $taskExit = $LASTEXITCODE
    Write-StopLog "taskkill exit code: $taskExit. $($taskOutput.Trim())"
    if ($taskExit -ne 0) { throw 'Windows could not terminate the service process.' }
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 500
        $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($null -eq $service -or $service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
            Write-StopLog 'Service process terminated and SCM reports Stopped.'
            exit 0
        }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'The service manager did not report Stopped after the process exited.'
} catch {
    Write-StopLog "FAILED: $($_.Exception.Message)"
    [Console]::Error.WriteLine("Could not stop service {0}: {1}", $ServiceName, $_.Exception.Message)
    exit 1
}
