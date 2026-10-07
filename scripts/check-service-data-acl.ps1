$ErrorActionPreference = 'Stop'

# Exercise only the ACL helper on disposable data, never register/start a service.
$InstallerScript = Join-Path $PSScriptRoot '..\src\EasyBalance.UI\src-tauri\resources\install-service.ps1'
$Tokens = $null
$ParseErrors = $null
$Ast = [System.Management.Automation.Language.Parser]::ParseFile($InstallerScript, [ref]$Tokens, [ref]$ParseErrors)
if ($ParseErrors) { throw ($ParseErrors -join [Environment]::NewLine) }
$Function = $Ast.Find({ param($Node) $Node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $Node.Name -eq 'Protect-ServiceData' }, $true)
if (-not $Function) { throw 'The installer ACL helper was not found.' }
. ([scriptblock]::Create($Function.Extent.Text))

$TempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$Fixture = [System.IO.Path]::GetFullPath((Join-Path $TempRoot ('easynetbalance-acl-' + [guid]::NewGuid().ToString('N'))))
if (-not $Fixture.StartsWith($TempRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'ACL fixture must be inside the temporary directory.' }
try {
    $Nested = Join-Path $Fixture 'mihomo'
    New-Item -ItemType Directory -Path $Nested -Force | Out-Null
    $Settings = Join-Path $Fixture 'settings.json'
    $Config = Join-Path $Nested 'config.yaml'
    [IO.File]::WriteAllText($Settings, '{"enabled":false}')
    [IO.File]::WriteAllText($Config, 'mode: rule')

    # Reproduce a protected child ACL that denies LocalSystem despite an
    # otherwise usable parent directory. The repair must replace, not merge it.
    $StaleAcl = [System.Security.AccessControl.FileSecurity]::new()
    $StaleAcl.SetSecurityDescriptorSddlForm('D:P(D;;FA;;;SY)(A;;FA;;;BA)', [System.Security.AccessControl.AccessControlSections]::Access)
    Set-Acl -LiteralPath $Settings -AclObject $StaleAcl
    Protect-ServiceData -Path $Fixture

    if ([IO.File]::ReadAllText($Settings) -cne '{"enabled":false}' -or [IO.File]::ReadAllText($Config) -cne 'mode: rule') {
        throw 'Repair must preserve existing settings and configuration bytes.'
    }
    $Rules = @((Get-Acl -LiteralPath $Settings).GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier]))
    if ($Rules | Where-Object { $_.AccessControlType -eq [System.Security.AccessControl.AccessControlType]::Deny }) {
        throw 'A stale deny ACE survived repair.'
    }
    [Console]::WriteLine('Service ACL repair verified: directory, nested directory, existing files, stale deny removal, preserved content.')
} finally {
    if (Test-Path -LiteralPath $Fixture) { Remove-Item -LiteralPath $Fixture -Recurse -Force }
}
