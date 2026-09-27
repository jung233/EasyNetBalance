param(
    [Parameter(Mandatory = $true)]
    [string] $SourceRoot,

    [Parameter(Mandatory = $true)]
    [string] $TauriRoot,

    [string] $SourceArchivePath
)

$ErrorActionPreference = 'Stop'

$ExpectedRepository = 'MetaCubeX/mihomo'
$ExpectedTag = 'v1.19.31'
$ExpectedCommit = 'ab405bad5beeeac8b003bb01f60f134f6df54471'
$workflowPin = @([string]$env:MIHOMO_REPOSITORY, [string]$env:MIHOMO_TAG, [string]$env:MIHOMO_COMMIT)
if (($workflowPin | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
    if ($workflowPin[0] -cne $ExpectedRepository -or $workflowPin[1] -cne $ExpectedTag -or $workflowPin[2] -cne $ExpectedCommit) {
        throw 'The workflow Mihomo repository/tag/commit does not match the audited release pin.'
    }
}
$RepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$SourceRoot = [System.IO.Path]::GetFullPath($SourceRoot)
$TauriRoot = [System.IO.Path]::GetFullPath($TauriRoot)

function Invoke-CheckedNativeCommand {
    param(
        [Parameter(Mandatory = $true)] [string] $FilePath,
        [Parameter(Mandatory = $true)] [string[]] $ArgumentList,
        [Parameter(Mandatory = $true)] [string] $FailureMessage
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage (exit code $LASTEXITCODE)."
    }
}

try {
    if (-not (Test-Path -LiteralPath $SourceRoot -PathType Container)) {
        throw "Mihomo source checkout is missing: $SourceRoot"
    }
    $weightedStrategy = Join-Path $SourceRoot 'adapter\outboundgroup\weighted_bytes.go'
    $sourceMarker = Join-Path $SourceRoot 'EASYNETBALANCE-SOURCE.md'
    if (-not (Test-Path -LiteralPath $weightedStrategy -PathType Leaf) -or -not (Test-Path -LiteralPath $sourceMarker -PathType Leaf)) {
        throw 'The checked-in Mihomo source is missing the EasyNetBalance weighted-bytes implementation or provenance marker.'
    }

    $moduleFile = Join-Path $SourceRoot 'go.mod'
    $readmeFile = Join-Path $SourceRoot 'README.md'
    $upstreamLicense = Join-Path $SourceRoot 'LICENSE'
    foreach ($requiredFile in @($moduleFile, $readmeFile, $upstreamLicense)) {
        if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
            throw "Pinned Mihomo source is missing required identity/license file: $requiredFile"
        }
    }
    $moduleText = [System.IO.File]::ReadAllText($moduleFile)
    $readmeText = [System.IO.File]::ReadAllText($readmeFile)
    $licenseText = [System.IO.File]::ReadAllText($upstreamLicense)
    if ($moduleText -notmatch '(?m)^module\s+github\.com/metacubex/mihomo\s*$' -or
        $readmeText -notmatch '(?i)Another Mihomo Kernel' -or
        $readmeText -notmatch 'GPL-3\.0' -or
        $licenseText -notmatch 'GNU GENERAL PUBLIC LICENSE' -or
        $licenseText -notmatch 'Version 3') {
        throw 'Pinned source identity or GPL-3.0 license verification failed; refusing to package an unknown core.'
    }

    $patchHash = 'source-in-tree'

    $env:GOSUMDB = 'sum.golang.org'
    $moduleRows = $null
    Push-Location $SourceRoot
    try {
        Invoke-CheckedNativeCommand -FilePath 'go' -ArgumentList @('mod', 'download', 'all') -FailureMessage 'Could not download the pinned Mihomo Go modules'
        $moduleRows = & go list -m -f '{{.Path}}|{{.Version}}|{{.Dir}}' all
        if ($LASTEXITCODE -ne 0) {
            throw 'Could not enumerate Mihomo Go module dependencies for license notices.'
        }
        Invoke-CheckedNativeCommand -FilePath 'go' -ArgumentList @('mod', 'vendor') -FailureMessage 'Could not vendor the pinned Mihomo Go modules'
    } finally {
        Pop-Location
    }

    $resourcesRoot = Join-Path $TauriRoot 'resources'
    $coreDirectory = Join-Path $resourcesRoot 'core'
    $noticesRoot = Join-Path $resourcesRoot 'notices'
    $noticeLicenseRoot = Join-Path $noticesRoot 'licenses'
    $mihomoDependencyNotices = Join-Path $noticeLicenseRoot 'mihomo-dependencies'
    New-Item -ItemType Directory -Force -Path $coreDirectory, $noticesRoot, $mihomoDependencyNotices | Out-Null

    $moduleInventory = [System.Collections.Generic.List[string]]::new()
    $missingModuleLicenses = [System.Collections.Generic.List[string]]::new()
    $licensePattern = '^(LICENSE|LICENCE|COPYING|NOTICE)([._-].*)?$'
    $fallbackLicenseUrls = @{
        'github.com/RyuaNerin/testingutil' = 'https://raw.githubusercontent.com/RyuaNerin/testingutil/master/LICENSE'
        'github.com/metacubex/chacha' = 'https://raw.githubusercontent.com/MetaCubeX/chacha/master/LICENSE'
    }
    foreach ($row in $moduleRows) {
        if ([string]::IsNullOrWhiteSpace([string]$row)) { continue }
        $parts = ([string]$row).Split('|', 3)
        if ($parts.Count -ne 3) { throw "Unexpected go list module row: $row" }
        $modulePath = $parts[0]
        $moduleVersion = $parts[1]
        $moduleDirectory = $parts[2]
        if ($modulePath -ceq 'github.com/metacubex/mihomo') { continue }
        if (-not (Test-Path -LiteralPath $moduleDirectory -PathType Container)) {
            throw "Go module source is unavailable for license collection: $modulePath $moduleVersion"
        }
        $moduleLicenses = @(Get-ChildItem -LiteralPath $moduleDirectory -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match $licensePattern })
        $licenseRoot = $moduleDirectory
        if ($moduleLicenses.Count -eq 0) {
            $licenseDirectories = @(Get-ChildItem -LiteralPath $moduleDirectory -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^(licenses?|licensing)$' })
            foreach ($licenseDirectory in $licenseDirectories) {
                $moduleLicenses += @(Get-ChildItem -LiteralPath $licenseDirectory.FullName -File -Recurse -Depth 3 -ErrorAction SilentlyContinue | Where-Object { $_.Name -match $licensePattern })
            }
        }
        if ($moduleLicenses.Count -eq 0 -and $fallbackLicenseUrls.ContainsKey($modulePath)) {
            $fallbackDirectory = Join-Path $SourceRoot 'vendor\\licenses\\_upstream-fallback'
            New-Item -ItemType Directory -Force -Path $fallbackDirectory | Out-Null
            $fallbackFile = Join-Path $fallbackDirectory (($modulePath -replace '[^A-Za-z0-9._-]', '_') + '-LICENSE.txt')
            Invoke-WebRequest -Uri $fallbackLicenseUrls[$modulePath] -OutFile $fallbackFile -UseBasicParsing
            if (Test-Path -LiteralPath $fallbackFile -PathType Leaf) {
                $moduleLicenses = @(Get-Item -LiteralPath $fallbackFile)
                $licenseRoot = $fallbackDirectory
            }
        }
        if ($moduleLicenses.Count -eq 0) {
            $missingModuleLicenses.Add("$modulePath $moduleVersion")
            $moduleInventory.Add("$modulePath $moduleVersion — NO RECOGNIZED LICENSE FILE")
            continue
        }
        $safeModule = (($modulePath + '-' + $moduleVersion) -replace '[^A-Za-z0-9._-]', '_')
        $licenseNames = [System.Collections.Generic.List[string]]::new()
        $vendorNoticeDirectory = Join-Path (Join-Path $SourceRoot 'vendor\licenses') $safeModule
        New-Item -ItemType Directory -Force -Path $vendorNoticeDirectory | Out-Null
        foreach ($moduleLicense in $moduleLicenses) {
            $relativeLicensePath = $moduleLicense.FullName.Substring($licenseRoot.Length).TrimStart([char[]]@([char]'\', [char]'/'))
            $safeLicensePath = $relativeLicensePath -replace '[\\/]', '_'
            $destinationName = $safeModule + '-' + $safeLicensePath
            Copy-Item -LiteralPath $moduleLicense.FullName -Destination (Join-Path $mihomoDependencyNotices $destinationName) -Force
            Copy-Item -LiteralPath $moduleLicense.FullName -Destination (Join-Path $vendorNoticeDirectory $safeLicensePath) -Force
            $licenseNames.Add($relativeLicensePath)
        }
        $moduleInventory.Add("$modulePath $moduleVersion — $($licenseNames -join ', ')")
    }
    if ($missingModuleLicenses.Count -gt 0) {
        $missingModuleLicenses | Sort-Object -Unique | Set-Content -LiteralPath (Join-Path $noticesRoot 'MIHOMO-GO-LICENSE-GAPS.txt') -Encoding utf8
        throw "Go dependencies lack verifiable license files; refusing to publish. See $noticesRoot\MIHOMO-GO-LICENSE-GAPS.txt. Missing: $($missingModuleLicenses -join ', ')"
    }
    $moduleInventory | Sort-Object -Unique | Set-Content -LiteralPath (Join-Path $noticesRoot 'MIHOMO-GO-DEPENDENCIES.txt') -Encoding utf8

    $buildDate = [DateTime]::UtcNow.ToString('yyyy-MM-dd')
    $buildReadme = @"
EasyNetBalance Mihomo source build
=================================

Upstream: https://github.com/$ExpectedRepository
Upstream tag: $ExpectedTag
Upstream commit: $ExpectedCommit
EasyNetBalance weighted-bytes modification date: $buildDate
The weighted-bytes implementation is committed directly in this source tree.
License: GPL-3.0; see LICENSE and vendor/licenses/.

This source archive contains the matching patched Mihomo source and vendored
Go dependencies. To rebuild from this already-patched source with Go installed,
run in PowerShell from this directory:

  `$env:GOOS = 'windows'
  `$env:GOARCH = 'amd64'
  `$env:CGO_ENABLED = '0'
  go build -mod=vendor -trimpath -buildvcs=false -o mihomo.exe .

The distributed Windows x64 core was built with the same environment and
command. The original upstream commit is identified here for review.
"@
    Set-Content -LiteralPath (Join-Path $SourceRoot 'EASYNETBALANCE-BUILD.md') -Value $buildReadme -Encoding utf8

    $binaryPath = Join-Path $coreDirectory 'mihomo.exe'
    $oldGoos = $env:GOOS
    $oldGoarch = $env:GOARCH
    $oldCgo = $env:CGO_ENABLED
    $env:GOOS = 'windows'
    $env:GOARCH = 'amd64'
    $env:CGO_ENABLED = '0'
    Push-Location $SourceRoot
    try {
        Invoke-CheckedNativeCommand -FilePath 'go' -ArgumentList @('build', '-mod=vendor', '-trimpath', '-buildvcs=false', '-o', $binaryPath, '.') -FailureMessage 'Could not compile the pinned, patched Mihomo core'
    } finally {
        Pop-Location
        $env:GOOS = $oldGoos
        $env:GOARCH = $oldGoarch
        $env:CGO_ENABLED = $oldCgo
    }
    if (-not (Test-Path -LiteralPath $binaryPath -PathType Leaf)) {
        throw "Mihomo build completed without producing its expected executable: $binaryPath"
    }

    Copy-Item -LiteralPath $upstreamLicense -Destination (Join-Path $noticeLicenseRoot 'mihomo-LICENSE.txt') -Force
    Copy-Item -LiteralPath (Join-Path $RepositoryRoot 'licenses\GPL-3.0.txt') -Destination (Join-Path $noticeLicenseRoot 'GPL-3.0.txt') -Force
    Copy-Item -LiteralPath (Join-Path $RepositoryRoot 'THIRD_PARTY_NOTICES.md') -Destination (Join-Path $noticesRoot 'THIRD_PARTY_NOTICES.md') -Force

    $sourceHash = 'not attached in this build'
    if (-not [string]::IsNullOrWhiteSpace($SourceArchivePath)) {
        $SourceArchivePath = [System.IO.Path]::GetFullPath($SourceArchivePath)
        $archiveParent = Split-Path -Parent $SourceArchivePath
        New-Item -ItemType Directory -Force -Path $archiveParent | Out-Null
        $tarPath = [System.IO.Path]::ChangeExtension($SourceArchivePath, '.tar')
        Invoke-CheckedNativeCommand -FilePath 'tar' -ArgumentList @('-cf', $tarPath, '-C', $SourceRoot, '.') -FailureMessage 'Could not export corresponding Mihomo source'
        $inputStream = [System.IO.File]::OpenRead($tarPath)
        $outputStream = [System.IO.File]::Create($SourceArchivePath)
        try {
            $gzipStream = [System.IO.Compression.GZipStream]::new($outputStream, [System.IO.Compression.CompressionLevel]::Optimal, $true)
            try { $inputStream.CopyTo($gzipStream) } finally { $gzipStream.Dispose() }
        } finally {
            $inputStream.Dispose()
            $outputStream.Dispose()
            Remove-Item -LiteralPath $tarPath -Force
        }
        $sourceHash = (Get-FileHash -LiteralPath $SourceArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    }

    $binaryHash = (Get-FileHash -LiteralPath $binaryPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $goVersion = (& go version).Trim()
    $buildCommand = 'go build -mod=vendor -trimpath -buildvcs=false -o mihomo.exe .'
    $sourceInfo = @"
Mihomo upstream repository: https://github.com/$ExpectedRepository
Mihomo upstream tag: $ExpectedTag
Mihomo upstream commit: $ExpectedCommit
Mihomo upstream README and module identity verified against $ExpectedRepository.
Mihomo license: GPL-3.0; upstream LICENSE and complete GPL-3.0 text are installed with EasyNetBalance.
EasyNetBalance modification: weighted-bytes source files committed in third_party/mihomo.
Build environment: $goVersion; GOOS=windows; GOARCH=amd64; CGO_ENABLED=0.
Build command: $buildCommand
Bundled binary: core/mihomo.exe
Bundled binary SHA-256: $binaryHash
Corresponding source: attached mihomo-$ExpectedTag-source.tar.gz, containing the embedded source and vendor tree.
Corresponding source archive SHA-256: $sourceHash
"@
    Set-Content -LiteralPath (Join-Path $noticesRoot 'MIHOMO-SOURCE.txt') -Value $sourceInfo -Encoding utf8

    if (-not [string]::IsNullOrWhiteSpace([string]$env:GITHUB_OUTPUT)) {
        "mihomo_binary_sha256=$binaryHash" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
        "mihomo_patch_sha256=$patchHash" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
        "mihomo_source_sha256=$sourceHash" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
        if (-not [string]::IsNullOrWhiteSpace($SourceArchivePath)) {
            "mihomo_source_archive=$SourceArchivePath" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
        }
    }

    Write-Output "Built Mihomo $ExpectedTag from $ExpectedCommit; binary SHA-256 $binaryHash; patch SHA-256 $patchHash."
} catch {
    [Console]::Error.WriteLine("EasyNetBalance Mihomo build/package failed: {0}", $_.Exception.Message)
    exit 1
}
