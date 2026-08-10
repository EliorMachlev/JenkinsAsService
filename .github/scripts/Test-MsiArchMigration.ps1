<#
.SYNOPSIS
    End-to-end test of the x64 <-> x86 migration gate.

.DESCRIPTION
    The two packages share an UpgradeCode, so Windows Installer will happily let one replace the other and
    FindRelatedProducts cannot tell that the architecture changed. Left alone that is a foot-gun with no undo:
    MajorUpgrade is scheduled afterInstallInitialize, so by the time anyone notices, the old product is gone.

    So the migration is supported but opt-in, via FORCE_UPGRADE=1, and this asserts both halves of that.
    Asserting only the "forced" half would let a gate that never blocks anything pass; asserting only the
    blocked half would let a gate that blocks everything - including the migration it is supposed to permit -
    pass just as easily.

    Asserted here:
      * an install of the other architecture WITHOUT FORCE_UPGRADE fails, and fails harmlessly: the installed
        product is untouched, still the original architecture, still running, config and data intact
      * the same install WITH FORCE_UPGRADE=1 succeeds and lands on the ORIGINAL install and data folders,
        recovered from the recorded locations rather than reset to defaults
      * the config, the operator's edits and the secret survive the migration
      * the recorded architecture is updated to the new one, so the gate does not then fire against itself

    Direction is a parameter because the gate has to work symmetrically; CI runs x64 -> x86.

    No Jenkins controller is involved: the URL points at a closed port, so the agent never connects.
#>
[CmdletBinding()]
# Write-Host is deliberate - see the note in Test-MsiLifecycle.ps1; this is a CI transcript, not a pipeline.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute(
    'PSAvoidUsingWriteHost', '', Justification = 'Intentional CI transcript output')]
param(
    # The package to install first.
    [Parameter(Mandatory)][string]$FromMsi,
    [Parameter(Mandatory)][ValidateSet('x64', 'x86')][string]$FromPlatform,
    # The other architecture's package, at a HIGHER ProductVersion.
    [Parameter(Mandatory)][string]$ToMsi,
    [Parameter(Mandatory)][ValidateSet('x64', 'x86')][string]$ToPlatform
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'MsiQuery.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'MsiTestHelpers.psm1') -Force

$installFolder = Join-Path $env:ProgramFiles 'Jenkins'
$dataFolder = Join-Path $env:ProgramData 'JenkinsAsServiceArchTest'
$configPath = Join-Path $installFolder 'appsettings.json'
$serviceName = 'Jenkins'
$logDir = Join-Path (Get-Location) 'msi-logs'
New-Item -ItemType Directory -Path $logDir -Force | Out-Null

function Get-ArchLogPath {
    param([Parameter(Mandatory)][string]$Name)
    return Join-Path $logDir "$Name.log"
}

# --------------------------------------------------------------------------------------------------
# Same hard stop as the lifecycle suite, for the same reason: matching ProductVersions turn the second /i
# into a maintenance reconfigure that touches nothing and passes everything.
Write-Host "`n=== 0. Package preconditions ==="
$fromVersion = Get-MsiProperty -Path $FromMsi -Name 'ProductVersion'
$toVersion = Get-MsiProperty -Path $ToMsi -Name 'ProductVersion'
Write-Host "  from: $FromPlatform $fromVersion"
Write-Host "  to:   $ToPlatform $toVersion"
if ($fromVersion -eq $toVersion) {
    Write-Host "::error::Both MSIs are stamped $fromVersion - the migration would be a no-op reconfigure."
    throw "MSI ProductVersion must differ between the two packages (both are $fromVersion)"
}
if ($FromPlatform -eq $ToPlatform) {
    throw "This suite tests a CROSS-architecture migration; both packages are $FromPlatform."
}

# The packages must really be the architectures they are claimed to be, or the gate could be passing for the
# wrong reason entirely.
Assert-That ((Get-MsiArchitecture -Path $FromMsi) -eq $FromPlatform) `
    "the 'from' package really targets $FromPlatform"
Assert-That ((Get-MsiArchitecture -Path $ToMsi) -eq $ToPlatform) `
    "the 'to' package really targets $ToPlatform"

# --------------------------------------------------------------------------------------------------
Write-Host "`n=== 1. Install $FromPlatform $fromVersion ==="
$log = Get-ArchLogPath -Name 'arch-install'
$code = Invoke-Msiexec -LogPath $log -Arguments @(
    '/i', $FromMsi,
    "DATAFOLDER=$dataFolder",
    'JENKINS_URL=https://127.0.0.1:59999',
    'JENKINS_SECRET=arch-smoke-secret',
    'JENKINS_SECRET_MODE=Unprotected',
    'JENKINS_AGENT_NAME=arch-node')
Assert-InstallerSucceeded -ExitCode $code -LogPath $log -Activity 'baseline install'

Assert-That ((Get-InstalledPlatform) -eq $FromPlatform) `
    "installed architecture recorded as $FromPlatform, and under that key only"
Assert-That (Test-Path $configPath) "appsettings.json written"

# An operator edit, so "the config survived" means the real file survived rather than an identical one having
# been rewritten from the properties below (which are deliberately not supplied to either migration attempt).
$raw = Get-Content $configPath -Raw | ConvertFrom-Json
$raw.Jenkins.Logging.RetainedLogs = 7
$raw | ConvertTo-Json -Depth 8 | Set-Content $configPath -Encoding utf8

# --------------------------------------------------------------------------------------------------
Write-Host "`n=== 2. $ToPlatform $toVersion WITHOUT FORCE_UPGRADE must be refused ==="
$code = Invoke-Msiexec -LogPath (Get-ArchLogPath -Name 'arch-blocked') -Arguments @('/i', $ToMsi)
Assert-That (-not (Test-InstallerSuccess -ExitCode $code)) `
    "the cross-architecture install is refused (msiexec exit code $code)"

# The refusal has to be inert. BlockArchMigration is sequenced at 60, far ahead of InstallInitialize (1500)
# and RemoveExistingProducts (1501), so nothing should have been removed, moved or reconfigured.
Assert-That ((Get-InstalledPlatform) -eq $FromPlatform) `
    "the installed product is still $FromPlatform after the refusal"
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
Assert-That ($null -ne $service -and $service.Status -eq 'Running') `
    "the service is still Running after the refusal - a blocked install must not disturb the installed one"
$cfg = Get-InstalledConfig -Path $configPath
Assert-That ($null -ne $cfg -and $cfg.Jenkins.Secret.Value -eq 'arch-smoke-secret') `
    "the config and secret are untouched by the refusal"

# --------------------------------------------------------------------------------------------------
Write-Host "`n=== 3. $ToPlatform $toVersion WITH FORCE_UPGRADE=1 must succeed ==="
# No DATAFOLDER, no URL, no secret: a migration must recover all of that from what is already recorded on the
# machine. Supplying any of it would hide a recovery that never happened.
$log = Get-ArchLogPath -Name 'arch-forced'
$code = Invoke-Msiexec -LogPath $log -Arguments @('/i', $ToMsi, 'FORCE_UPGRADE=1')
Assert-InstallerSucceeded -ExitCode $code -LogPath $log -Activity 'forced migration'

# Not just "the new key exists" - the OLD one has to be gone, or the next package sees two architectures
# installed at once and the gate starts firing against a product that no longer exists.
Assert-That ((Get-InstalledPlatform) -eq $ToPlatform) `
    "the recorded architecture is now $ToPlatform, and only $ToPlatform - the $FromPlatform key is gone"
Assert-That ((Get-RecordedLocation -Platform $ToPlatform -Name 'InstallPath') -eq "$installFolder\") `
    "the migrated install kept the original install folder"
Assert-That ((Get-RecordedLocation -Platform $ToPlatform -Name 'DataPath') -eq "$dataFolder\") `
    "the migrated install kept the original data folder, recovered rather than reset to the default"
Assert-That (-not (Test-Path (Join-Path $env:ProgramData 'JenkinsAsService'))) `
    "no stray default data folder was created by the migration"

$cfg = Get-InstalledConfig -Path $configPath
Assert-That ($null -ne $cfg) "appsettings.json still exists after the migration"
Assert-That ($cfg.Jenkins.Secret.Value -eq 'arch-smoke-secret') `
    "the secret survives a migration that was never given JENKINS_SECRET"
Assert-That ($cfg.Jenkins.Logging.RetainedLogs -eq 7) "the operator's edit survives the migration"
Assert-That ($cfg.Jenkins.Connection.AgentName -eq 'arch-node') "an unrelated value survives the migration"

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
Assert-That ($null -ne $service -and $service.Status -eq 'Running') "the service is Running after the migration"

# --------------------------------------------------------------------------------------------------
Write-Host "`n=== 4. Clean up ==="
Invoke-Msiexec -LogPath (Get-ArchLogPath -Name 'arch-uninstall') -Arguments @('/x', $ToMsi) | Out-Null
Assert-That ($null -eq (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) "service is removed"
Assert-That (-not (Test-Path $dataFolder)) "the data folder is removed"
Assert-That ($null -eq (Get-InstalledPlatform)) "neither architecture key survives the uninstall"

# --------------------------------------------------------------------------------------------------
Complete-AssertionReport -Subject 'architecture-migration'
