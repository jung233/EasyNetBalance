param(
    [Parameter(Mandatory = $true)]
    [string] $PackageRoot,

    [Parameter(Mandatory = $true)]
    [string] $Destination
)

$ErrorActionPreference = 'Stop'

try {
    $PackageRoot = [System.IO.Path]::GetFullPath($PackageRoot)
    $Destination = [System.IO.Path]::GetFullPath($Destination)
    $ModulesRoot = Join-Path $PackageRoot 'node_modules'
    if (-not (Test-Path -LiteralPath $ModulesRoot -PathType Container)) {
        throw "npm dependencies are missing: $ModulesRoot"
    }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null

    $inventory = [System.Collections.Generic.List[string]]::new()
    $missing = [System.Collections.Generic.List[string]]::new()
    $licensePattern = '^(LICENSE|LICENCE|COPYING|NOTICE)([._-].*)?$'
    $packageFiles = @(Get-ChildItem -LiteralPath $ModulesRoot -Filter 'package.json' -File -Recurse)
    if ($packageFiles.Count -eq 0) { throw 'No installed npm dependency manifests were found.' }

    foreach ($packageFile in $packageFiles) {
        $manifest = Get-Content -LiteralPath $packageFile.FullName -Raw | ConvertFrom-Json
        $name = [string]$manifest.name
        $version = [string]$manifest.version
        if ([string]::IsNullOrWhiteSpace($name) -or [string]::IsNullOrWhiteSpace($version)) {
            $missing.Add($packageFile.FullName)
            continue
        }
        $packageDirectory = Split-Path -Parent $packageFile.FullName
        $licenseFiles = @(Get-ChildItem -LiteralPath $packageDirectory -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match $licensePattern })
        if ($licenseFiles.Count -eq 0) {
            $licenseDirectories = @(Get-ChildItem -LiteralPath $packageDirectory -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^(licenses?|licensing)$' })
            foreach ($licenseDirectory in $licenseDirectories) {
                $licenseFiles += @(Get-ChildItem -LiteralPath $licenseDirectory.FullName -File -Recurse -Depth 3 -ErrorAction SilentlyContinue | Where-Object { $_.Name -match $licensePattern })
            }
        }
        $license = [string]$manifest.license
        if ([string]::IsNullOrWhiteSpace($license) -and $licenseFiles.Count -gt 0) {
            $license = 'See bundled license text: ' + (($licenseFiles | ForEach-Object { $_.Name }) -join ', ')
        }
        if ([string]::IsNullOrWhiteSpace($license)) { $license = 'UNSPECIFIED' }
        $inventory.Add("$name $version — $license")
        if ($licenseFiles.Count -eq 0) {
            $missing.Add("$name $version (license metadata: $license; no top-level license/notice file)")
            continue
        }
        $safeName = (($name + '-' + $version) -replace '[^A-Za-z0-9._-]', '_')
        foreach ($licenseFile in $licenseFiles) {
            $relativeLicensePath = $licenseFile.FullName.Substring($packageDirectory.Length).TrimStart([char[]]@([char]'\', [char]'/'))
            $safeLicensePath = $relativeLicensePath -replace '[\\/]', '_'
            Copy-Item -LiteralPath $licenseFile.FullName -Destination (Join-Path $Destination ($safeName + '-' + $safeLicensePath)) -Force
        }
    }

    if ($missing.Count -gt 0) {
        throw "Could not collect complete npm dependency metadata/license files: $($missing -join '; ')"
    }
    $inventory | Sort-Object -Unique | Set-Content -LiteralPath (Join-Path $Destination 'NPM-DEPENDENCIES.txt') -Encoding utf8
} catch {
    [Console]::Error.WriteLine("EasyNetBalance npm license collection failed: {0}", $_.Exception.Message)
    exit 1
}
