param([Parameter(Mandatory = $true)][string] $InstallerPath)

$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows') {
    throw 'This installation check may run only on a disposable GitHub Actions Windows runner.'
}
$ServiceName = 'EasyNetBalance'
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    throw 'The runner already has an EasyNetBalance service; refusing to replace it.'
}
$CommonData = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
$DataDirectory = Join-Path $CommonData $ServiceName
if (Test-Path -LiteralPath $DataDirectory) { throw 'The runner already has application data; refusing to modify it.' }
$RunnerTemp = [IO.Path]::GetFullPath($env:RUNNER_TEMP)
$InstallRoot = [IO.Path]::GetFullPath((Join-Path $RunnerTemp ('EasyNetBalance install check ' + [guid]::NewGuid().ToString('N'))))
if (-not $InstallRoot.StartsWith($RunnerTemp.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'The check must install inside RUNNER_TEMP.' }
$InstallerPath = (Resolve-Path -LiteralPath $InstallerPath).Path
$Before = Get-Date

try {
    New-Item -ItemType Directory -Path $DataDirectory | Out-Null
    $SettingsPath = Join-Path $DataDirectory 'settings.json'
    $FixtureSettings = @{ enabled = $false; interfaceUsabilityOverrides = @{} }
    1..80 | ForEach-Object { $FixtureSettings.interfaceUsabilityOverrides[[guid]::NewGuid().ToString()] = $true }
    [IO.File]::WriteAllText($SettingsPath, ($FixtureSettings | ConvertTo-Json -Depth 4 -Compress))
    $SettingsHash = (Get-FileHash -LiteralPath $SettingsPath -Algorithm SHA256).Hash
    $StaleAcl = [System.Security.AccessControl.FileSecurity]::new()
    $StaleAcl.SetSecurityDescriptorSddlForm('D:P(D;;FA;;;SY)(A;;FA;;;BA)', [System.Security.AccessControl.AccessControlSections]::Access)
    Set-Acl -LiteralPath $SettingsPath -AclObject $StaleAcl

    # NSIS treats /D as the last argument and consumes the remaining path,
    # including spaces. This also verifies a non-default install directory.
    $Installer = Start-Process -FilePath $InstallerPath -ArgumentList @('/S', "/D=$InstallRoot") -WindowStyle Hidden -PassThru
    if (-not $Installer.WaitForExit(90000)) {
        Stop-Process -Id $Installer.Id -Force
        throw 'Silent installation did not finish within 90 seconds.'
    }
    $Installer.Refresh()
    if ($Installer.ExitCode -ne 0) { throw "Silent installation failed with exit code $($Installer.ExitCode)." }
    $Service = Get-Service -Name $ServiceName -ErrorAction Stop
    $Service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(45))
    $Configuration = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
    $ExpectedBinary = '"' + (Join-Path $InstallRoot 'EasyNetBalance.exe') + '" --service'
    if ($Configuration.StartName -ne 'LocalSystem' -or -not [String]::Equals($Configuration.PathName, $ExpectedBinary, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Service registration does not match the selected directory and LocalSystem account: $($Configuration.PathName); $($Configuration.StartName)"
    }
    if ((Get-FileHash -LiteralPath $SettingsPath -Algorithm SHA256).Hash -cne $SettingsHash) { throw 'Installing or starting the service changed saved settings bytes.' }

    foreach ($Method in @('GetStatus', 'GetSettings', 'GetAdapters')) {
        $Pipe = [IO.Pipes.NamedPipeClientStream]::new('.', 'EasyBalance.Control.v1', [IO.Pipes.PipeDirection]::InOut)
        try {
            $Pipe.Connect(10000)
            $Writer = [IO.StreamWriter]::new($Pipe, [Text.UTF8Encoding]::new($false))
            $Writer.AutoFlush = $true
            $Reader = [IO.StreamReader]::new($Pipe, [Text.Encoding]::UTF8)
            $Writer.WriteLine((@{method = $Method; payload = $null} | ConvertTo-Json -Compress))
            $ResponseTask = $Reader.ReadLineAsync()
            if (-not $ResponseTask.Wait(10000)) { throw "$Method did not reply within 10 seconds." }
            $Response = $ResponseTask.Result | ConvertFrom-Json
            if (-not $Response.success -or $null -eq $Response.payload) {
                throw "Unexpected $Method response: $($ResponseTask.Result)"
            }
            if ($Method -eq 'GetStatus' -and $Response.payload.routingEnabled) { throw 'The fixture must keep routing disabled.' }
            if ($Method -eq 'GetSettings' -and
                ($ResponseTask.Result.Length -le 1024 -or @($Response.payload.interfaceUsabilityOverrides.PSObject.Properties).Count -ne 80)) {
                throw 'The large settings response was incomplete or lost fixture entries.'
            }
            [Console]::WriteLine("Control response verified: {0}, {1} characters.", $Method, $ResponseTask.Result.Length)
        } finally { $Pipe.Dispose() }
    }
    $ServicePid = [int]$Configuration.ProcessId
    $Application = Join-Path $InstallRoot 'EasyNetBalance.exe'
    # Exercise WM_CLOSE on the real Tauri window, not just the headless helper.
    $Shutdown = Start-Process -FilePath $Application -PassThru
    $WindowDeadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        Start-Sleep -Milliseconds 250
        $Shutdown.Refresh()
        if ($Shutdown.HasExited) { throw 'The application exited before opening its window.' }
    } while ($Shutdown.MainWindowHandle -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $WindowDeadline)
    if ($Shutdown.MainWindowHandle -eq [IntPtr]::Zero) { throw 'The application did not open a window.' }
    if (-not $Shutdown.CloseMainWindow()) { throw 'Could not request window close.' }
    if (-not $Shutdown.WaitForExit(90000)) { Stop-Process -Id $Shutdown.Id -Force; throw 'Application shutdown timed out.' }
    if ($Shutdown.ExitCode -ne 0) { throw 'Application shutdown reported failure.' }
    $Service.Refresh()
    if ($Service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped -or (Get-Process -Id $ServicePid -ErrorAction SilentlyContinue)) {
        throw 'Application shutdown left its service process running.'
    }
    $AfterShutdown = [IO.File]::ReadAllText($SettingsPath) | ConvertFrom-Json
    if ($AfterShutdown.enabled -or @($AfterShutdown.interfaceUsabilityOverrides.PSObject.Properties).Count -ne 80) { throw 'Shutdown lost settings or left routing enabled.' }
    $Restart = Start-Process -FilePath $Application -ArgumentList '--start-service' -WindowStyle Hidden -PassThru
    if (-not $Restart.WaitForExit(60000)) { Stop-Process -Id $Restart.Id -Force; throw 'Service restart timed out.' }
    if ($Restart.ExitCode -ne 0) { throw 'Service restart reported failure.' }
    $Service.Refresh()
    if ($Service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) { throw 'The stopped service could not restart.' }
    [Console]::WriteLine('Application lifecycle verified: closing the real UI persists disabled routing, UI and service processes exit, service restarts from the installed executable.')
    [Console]::WriteLine('Installer verified: custom directory with spaces, stale SYSTEM deny repaired, LocalSystem service running, settings preserved, complete large control responses, routing disabled.')
} catch {
    try {
        Get-WinEvent -FilterHashtable @{LogName = 'Application'; StartTime = $Before} -MaxEvents 100 -ErrorAction Stop |
            Where-Object { $_.ProviderName -eq $ServiceName } | ForEach-Object {
                [Console]::Error.WriteLine("Service event: {0}", (($_.Properties | ForEach-Object { $_.Value }) -join ' '))
            }
    } catch { }
    throw
} finally {
    $Uninstaller = Join-Path $InstallRoot 'uninstall.exe'
    if (Test-Path -LiteralPath $Uninstaller -PathType Leaf) {
        $Uninstall = Start-Process -FilePath $Uninstaller -ArgumentList '/S' -WindowStyle Hidden -Wait -PassThru
        if ($Uninstall.ExitCode -ne 0) { throw "Smoke-check uninstall failed: $($Uninstall.ExitCode)." }
    }
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) { throw 'The installer check left its service registered; preserving its data for diagnosis.' }
    # Both paths were created by this CI-only script and checked above.
    if (Test-Path -LiteralPath $DataDirectory) { Remove-Item -LiteralPath $DataDirectory -Recurse -Force }
}
