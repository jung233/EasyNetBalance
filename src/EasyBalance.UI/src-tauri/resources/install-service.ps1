param(
    [Parameter(Mandatory = $true)]
    [string] $InstallRoot
)

$ErrorActionPreference = 'Stop'
$StartupAttempt = Get-Date

function Protect-ServiceData {
    param([Parameter(Mandatory = $true)][string] $Path)

    # Construct a complete DACL instead of merging grants into a legacy ACL.
    # Files receive applicable ACEs without directory inheritance flags.
    $Pending = [System.Collections.Generic.Stack[string]]::new()
    $Pending.Push($Path)
    while ($Pending.Count -gt 0) {
        $ItemPath = $Pending.Pop()
        $Item = Get-Item -LiteralPath $ItemPath -Force -ErrorAction Stop
        if (($Item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to change service permissions through a reparse point: $ItemPath"
        }
        if (($Item.Attributes -band [System.IO.FileAttributes]::Encrypted) -ne 0) {
            throw "Service data is EFS-encrypted and cannot be read by LocalSystem: $ItemPath. Decrypt it with its owning Windows account before installing."
        }
        if ($Item.PSIsContainer) {
            $Acl = [System.Security.AccessControl.DirectorySecurity]::new()
            $Sddl = 'D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)'
        } else {
            $Acl = [System.Security.AccessControl.FileSecurity]::new()
            $Sddl = 'D:P(A;;FA;;;SY)(A;;FA;;;BA)'
        }
        $Acl.SetSecurityDescriptorSddlForm($Sddl, [System.Security.AccessControl.AccessControlSections]::Access)
        Set-Acl -LiteralPath $ItemPath -AclObject $Acl -ErrorAction Stop
        $Applied = Get-Acl -LiteralPath $ItemPath -ErrorAction Stop
        $Rules = @($Applied.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier]))
        foreach ($Sid in @('S-1-5-18', 'S-1-5-32-544')) {
            if (-not ($Rules | Where-Object {
                $_.IdentityReference.Value -eq $Sid -and
                $_.AccessControlType -eq [System.Security.AccessControl.AccessControlType]::Allow -and
                ($_.FileSystemRights -band [System.Security.AccessControl.FileSystemRights]::FullControl) -eq [System.Security.AccessControl.FileSystemRights]::FullControl
            })) { throw "The applied ACL does not grant FullControl to $Sid on $ItemPath." }
        }
        if ($Rules.Count -ne 2 -or -not $Applied.AreAccessRulesProtected) {
            throw "The service ACL contains unexpected or inherited entries on $ItemPath."
        }
        if ($Item.PSIsContainer) {
            # Apply each directory before traversing it. Never follow a junction.
            foreach ($Child in Get-ChildItem -LiteralPath $ItemPath -Force -ErrorAction Stop) {
                $Pending.Push($Child.FullName)
            }
        }
    }
}

try {
    $Identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $Principal = [System.Security.Principal.WindowsPrincipal]::new($Identity)
    if (-not $Principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "The installer service hook must run with an elevated Administrator token (current identity: $($Identity.Name))."
    }
    $InstallRoot = [System.IO.Path]::GetFullPath($InstallRoot)
    $ExpectedRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
    if (-not [string]::Equals($InstallRoot.TrimEnd('\'), $ExpectedRoot.TrimEnd('\'), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The supplied install root does not match this installer's resources: $InstallRoot (expected $ExpectedRoot)"
    }

    $ServiceName = 'EasyNetBalance'
    $ServiceExecutableName = 'EasyNetBalance.exe'
    $CoreSource = Join-Path $InstallRoot 'resources\core\mihomo.exe'
    $ServiceExecutable = Join-Path $InstallRoot $ServiceExecutableName
    $ManifestPath = Join-Path $InstallRoot '.easynetbalance-service-files.txt'
    $CommonData = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::CommonApplicationData)
    if ([string]::IsNullOrWhiteSpace($CommonData)) { throw 'Windows could not resolve CommonApplicationData.' }
    $DataDirectory = Join-Path $CommonData $ServiceName
    [Console]::WriteLine("Service identity: LocalSystem; install root: {0}; data directory: {1}", $InstallRoot, $DataDirectory)

    if (-not (Test-Path -LiteralPath $ServiceExecutable -PathType Leaf)) {
        throw "The installed service executable is missing: $ServiceExecutable"
    }
    if (-not (Test-Path -LiteralPath $CoreSource -PathType Leaf)) {
        throw "The mihomo core is missing from the bundle: $CoreSource"
    }
    foreach ($ProtectedDirectory in @($InstallRoot, $DataDirectory)) {
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
    $LegacyCorePending = $false
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
            # Earlier releases locked this unused copy inside a separate core
            # directory. Its ACL can deny deletion during an upgrade. The new
            # service runs the bundled resources\core\mihomo.exe instead.
            if ([string]::Equals($RelativePath.Replace('/', '\'), 'core\mihomo.exe', [System.StringComparison]::OrdinalIgnoreCase)) {
                $LegacyCorePending = $true
                continue
            }
            if (Test-Path -LiteralPath $PreviousFile -PathType Leaf) {
                Remove-Item -LiteralPath $PreviousFile -Force
            }
        }
    }

    New-Item -ItemType Directory -Force -Path $DataDirectory | Out-Null
    Protect-ServiceData -Path $DataDirectory
    $SettingsPath = Join-Path $DataDirectory 'settings.json'
    if (Test-Path -LiteralPath $SettingsPath) {
        if (-not (Test-Path -LiteralPath $SettingsPath -PathType Leaf)) { throw "The settings path is not a file: $SettingsPath" }
        $SettingsHandle = [System.IO.File]::Open($SettingsPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        $SettingsHandle.Dispose()
    }
    # Keep the old manifest until the new LocalSystem service has removed the
    # protected legacy copy. It is the authority for that one-time cleanup.
    if (-not $LegacyCorePending -and (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        Remove-Item -LiteralPath $ManifestPath -Force
    }

    $BinaryPath = '"' + $ServiceExecutable + '" --service'
    if ($null -eq $ExistingService) {
        New-Service -Name $ServiceName -DisplayName 'EasyNetBalance' -Description 'EasyNetBalance background routing service' -BinaryPathName $BinaryPath -StartupType Automatic | Out-Null
    } else {
        $ServiceConfig = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'" -ErrorAction Stop
        if ($null -eq $ServiceConfig) { throw "Could not read the existing $ServiceName service configuration." }
        if (-not [string]::Equals($ServiceConfig.PathName, $BinaryPath, [System.StringComparison]::OrdinalIgnoreCase) -or
            $ServiceConfig.StartMode -ne 'Auto' -or $ServiceConfig.StartName -ne 'LocalSystem') {
            # CIM passes PathName as one string, preserving the quotes around a
            # Program Files executable. PowerShell 5.1 can strip those quotes
            # when forwarding an argument to sc.exe config.
            $Update = Invoke-CimMethod -InputObject $ServiceConfig -MethodName Change -Arguments @{
                PathName = $BinaryPath
                StartMode = 'Automatic'
                StartName = 'LocalSystem'
            } -ErrorAction Stop
            if ($Update.ReturnValue -ne 0) {
                throw "Could not update the EasyNetBalance service configuration (Win32_Service.Change code $($Update.ReturnValue))."
            }
        }
    }

    $StartupErrorPath = Join-Path $DataDirectory 'service-startup-error.log'
    if (Test-Path -LiteralPath $StartupErrorPath -PathType Leaf) { Remove-Item -LiteralPath $StartupErrorPath -Force }
    Start-Service -Name $ServiceName -ErrorAction Stop
    $RunningService = Get-Service -Name $ServiceName
    $RunningService.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(45))
} catch {
    [Console]::Error.WriteLine("EasyNetBalance service installation failed: {0} ({1})", $_.Exception.Message, $_.InvocationInfo.PositionMessage.Trim())
    if ($StartupErrorPath) {
        try {
            if (Test-Path -LiteralPath $StartupErrorPath -PathType Leaf) {
                [Console]::Error.WriteLine("Service startup error: {0}", [System.IO.File]::ReadAllText($StartupErrorPath).Trim())
            }
        } catch {
            [Console]::Error.WriteLine("Service startup diagnostic is protected from the installer token: {0}", $_.Exception.Message)
        }
    }
    try {
        $StartupEvent = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $StartupAttempt } -MaxEvents 100 -ErrorAction Stop |
            Where-Object { $_.ProviderName -eq 'EasyNetBalance' -and $_.Id -eq 4096 } | Select-Object -First 1
        if ($StartupEvent) {
            [Console]::Error.WriteLine("Windows service startup event: {0}", (($StartupEvent.Properties | ForEach-Object { $_.Value }) -join ' '))
        }
    } catch { }
    exit 1
}
