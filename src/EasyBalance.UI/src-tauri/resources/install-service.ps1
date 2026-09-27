param(
    [Parameter(Mandatory = $true)]
    [string] $InstallRoot
)

$ErrorActionPreference = 'Stop'

try {
    $InstallRoot = [System.IO.Path]::GetFullPath($InstallRoot)
    # NSIS is a 32-bit process and can launch 32-bit PowerShell, where
    # ProgramFiles points to Program Files (x86) even for a per-machine x64
    # installation. ProgramW6432 always names the native Program Files root.
    $NativeProgramFiles = if ([string]::IsNullOrWhiteSpace($env:ProgramW6432)) { $env:ProgramFiles } else { $env:ProgramW6432 }
    $ExpectedRoot = [System.IO.Path]::GetFullPath((Join-Path $NativeProgramFiles 'EasyNetBalance'))
    if (-not [string]::Equals($InstallRoot.TrimEnd('\'), $ExpectedRoot.TrimEnd('\'), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to install outside the protected application directory: $InstallRoot (expected $ExpectedRoot)"
    }

    $ServiceName = 'EasyNetBalance'
    $ServiceExecutableName = 'EasyNetBalance.exe'
    $CoreSource = Join-Path $InstallRoot 'resources\core\mihomo.exe'
    $ServiceExecutable = Join-Path $InstallRoot $ServiceExecutableName
    $ManifestPath = Join-Path $InstallRoot '.easynetbalance-service-files.txt'
    $DataDirectory = Join-Path $env:ProgramData 'EasyNetBalance'

    if (-not (Test-Path -LiteralPath $ServiceExecutable -PathType Leaf)) {
        throw "The installed service executable is missing: $ServiceExecutable"
    }
    if (-not (Test-Path -LiteralPath $CoreSource -PathType Leaf)) {
        throw "The mihomo core is missing from the bundle: $CoreSource"
    }
    foreach ($ProtectedDirectory in @($DataDirectory)) {
        if (Test-Path -LiteralPath $ProtectedDirectory) {
            $ProtectedItem = Get-Item -LiteralPath $ProtectedDirectory -Force
            if (($ProtectedItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to apply service ACLs through a reparse point: $ProtectedDirectory"
            }
        }
    }

    $LegacyService = Get-Service -Name 'EasyBalance' -ErrorAction SilentlyContinue
    if ($null -ne $LegacyService) {
        if ($LegacyService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
            Stop-Service -Name 'EasyBalance' -Force -ErrorAction Stop
            $LegacyService = Get-Service -Name 'EasyBalance'
            $LegacyService.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(45))
        }
        & "$env:SystemRoot\System32\sc.exe" delete 'EasyBalance'
        if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 1060) {
            throw "Could not remove the legacy EasyBalance service (sc.exe exit code $LASTEXITCODE)."
        }
        $LegacyDeadline = [DateTime]::UtcNow.AddSeconds(30)
        do {
            Start-Sleep -Milliseconds 500
            $LegacyService = Get-Service -Name 'EasyBalance' -ErrorAction SilentlyContinue
            if ($null -eq $LegacyService) { break }
        } while ([DateTime]::UtcNow -lt $LegacyDeadline)
        if ($null -ne $LegacyService) {
            throw 'Windows is still removing the previous EasyBalance service. Retry installation after a short wait or reboot.'
        }
    }

    $ExistingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -ne $ExistingService -and $ExistingService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        Stop-Service -Name $ServiceName -Force -ErrorAction Stop
        $ExistingService = Get-Service -Name $ServiceName
        $ExistingService.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(45))
    }

    # Remove only files recorded by a prior EasyNetBalance package. This keeps
    # upgrades tidy without deleting unrelated files from the installation root.
    if (Test-Path -LiteralPath $ManifestPath -PathType Leaf) {
        foreach ($RelativePath in [System.IO.File]::ReadAllLines($ManifestPath)) {
            if ([string]::IsNullOrWhiteSpace($RelativePath)) { continue }
            if ([System.IO.Path]::IsPathRooted($RelativePath) -or $RelativePath -match '(^|[\\/])\.\.([\\/]|$)') {
                throw "Invalid path in the previous service payload manifest: $RelativePath"
            }
            $PreviousFile = [System.IO.Path]::GetFullPath((Join-Path $InstallRoot $RelativePath))
            if (-not $PreviousFile.StartsWith($InstallRoot.TrimEnd('\') + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "The previous service payload manifest points outside the installation root: $RelativePath"
            }
            if (Test-Path -LiteralPath $PreviousFile -PathType Leaf) {
                Remove-Item -LiteralPath $PreviousFile -Force
            }
        }
    }

    $BroadPrincipals = @('*S-1-1-0', '*S-1-5-11', '*S-1-5-32-545')
    New-Item -ItemType Directory -Force -Path $DataDirectory | Out-Null
    & "$env:SystemRoot\System32\icacls.exe" $DataDirectory /inheritance:r /remove:g @BroadPrincipals /remove:d @BroadPrincipals /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' /T /C
    if ($LASTEXITCODE -ne 0) {
        throw "Could not set protected SYSTEM/Administrators ACLs on $DataDirectory (icacls exit code $LASTEXITCODE)."
    }
    if (Test-Path -LiteralPath $ManifestPath -PathType Leaf) { Remove-Item -LiteralPath $ManifestPath -Force }

    $BinaryPath = '"' + $ServiceExecutable + '" --service'
    if ($null -eq $ExistingService) {
        New-Service -Name $ServiceName -DisplayName 'EasyNetBalance' -Description 'EasyNetBalance background routing service' -BinaryPathName $BinaryPath -StartupType Automatic | Out-Null
    } else {
        & "$env:SystemRoot\System32\sc.exe" config $ServiceName binPath= $BinaryPath start= auto obj= LocalSystem
        if ($LASTEXITCODE -ne 0) {
            throw "Could not update the EasyNetBalance service configuration (sc.exe exit code $LASTEXITCODE)."
        }
    }

    Start-Service -Name $ServiceName -ErrorAction Stop
    $RunningService = Get-Service -Name $ServiceName
    $RunningService.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(45))
} catch {
    [Console]::Error.WriteLine("EasyNetBalance service installation failed: {0}", $_.Exception.Message)
    exit 1
}
