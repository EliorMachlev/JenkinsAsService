<#
.SYNOPSIS
    End-to-end test of the single self-selecting installer .exe (the Burn bundle).

.DESCRIPTION
    The bundle's whole job is to install the architecture that matches the machine and to migrate an install
    of the other one. The second half is the dangerous half, and it is dangerous in a way that reads as
    success: the MSI's purge custom action deletes the config, the data folder and the secret, and it is
    gated only on REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE. If Burn were ever to plan the old
    architecture's product as a standalone UNINSTALL rather than letting the incoming MSI's MajorUpgrade
    replace it, that purge would fire - and the migration would then "succeed" onto a machine whose
    configuration and agent secret had just been destroyed.

    Reasoning says Burn treats it as an upgrade, because the two MSIs share an UpgradeCode and the chain's
    own x86 package (a different ProductCode) is not detected as present. Reasoning is not evidence for
    something with that failure mode, so this installs the x86 MSI directly, runs the bundle over it on a
    64-bit runner, and asserts the config, the operator's edit, the secret and the data folder all survived.

    Asserted here:
      * the bundle installs the architecture matching the machine - checked against the PE header of the
        binary that actually landed, not against what the package claimed
      * it migrates an install of the other architecture, without being told to
      * the config, an operator edit, the secret and the data folder survive that migration
      * the install and data folders are the original ones, not defaults
      * uninstalling through the bundle removes the service, both folders and the location keys

    No Jenkins controller is involved: the URL points at a closed port, so the agent never connects.
#>
[CmdletBinding()]
# Write-Host is deliberate - see the note in Test-MsiLifecycle.ps1; this is a CI transcript, not a pipeline.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute(
    'PSAvoidUsingWriteHost', '', Justification = 'Intentional CI transcript output')]
param(
    # The x86 MSI to install first, at a LOWER version than the bundle.
    [Parameter(Mandatory)][string]$BaselineX86Msi,
    # The bundle .exe under test.
    [Parameter(Mandatory)][string]$BundleExe
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'MsiQuery.psm1') -Force

$installFolder = Join-Path $env:ProgramFiles 'Jenkins'
$dataFolder = Join-Path $env:ProgramData 'JenkinsAsServiceBundleTest'
$configPath = Join-Path $installFolder 'appsettings.json'
$agentExe = Join-Path $installFolder 'JenkinsAsService.exe'
$serviceName = 'Jenkins'
$logDir = Join-Path (Get-Location) 'msi-logs'
New-Item -ItemType Directory -Path $logDir -Force | Out-Null

$locationKeys = [ordered]@{
    x64 = 'HKLM:\SOFTWARE\JenkinsAsService\x64'
    x86 = 'HKLM:\SOFTWARE\WOW6432Node\JenkinsAsService\x86'
}

$script:Failures = @()

function Assert-That {
    param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if ($Condition) { Write-Host "  [PASS] $Message" }
    else {
        Write-Host "  [FAIL] $Message"
        $script:Failures += $Message
    }
}

function Get-RecordedLocation {
    param([Parameter(Mandatory)][ValidateSet('x64', 'x86')][string]$Platform,
          [Parameter(Mandatory)][string]$Name)
    $key = Get-ItemProperty -Path $locationKeys[$Platform] -ErrorAction SilentlyContinue
    if ($null -eq $key -or -not $key.PSObject.Properties[$Name]) { return $null }
    return $key.$Name
}

function Get-InstalledPlatform {
    $found = @($locationKeys.Keys | Where-Object { $null -ne (Get-RecordedLocation -Platform $_ -Name 'InstallPath') })
    if ($found.Count -eq 0) { return $null }
    if ($found.Count -gt 1) { return 'both' }
    return $found[0]
}

# The architecture of the binary that actually landed on disk, read out of its PE header. The registry key
# records what the package SAID it was; this is what it shipped. If those two ever disagree, every other
# assertion in this suite is measuring the wrong thing.
function Get-PeMachine {
    param([Parameter(Mandatory)][string]$Path)
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $reader = New-Object System.IO.BinaryReader($stream)
        $stream.Position = 0x3C                      # e_lfanew: offset of the PE signature
        $peOffset = $reader.ReadInt32()
        $stream.Position = $peOffset + 4             # skip "PE\0\0" to the COFF Machine field
        switch ($reader.ReadUInt16()) {
            0x8664 { return 'x64' }
            0x014C { return 'x86' }
            default { return 'unknown' }
        }
    }
    finally { $stream.Dispose() }
}

function Invoke-Process {
    param([Parameter(Mandatory)][string]$FilePath, [Parameter(Mandatory)][string[]]$Arguments)
    Write-Host "$FilePath $($Arguments -join ' ')"
    $p = Start-Process $FilePath -ArgumentList $Arguments -Wait -PassThru
    Write-Host "  exit code $($p.ExitCode)"
    return $p.ExitCode
}

# --------------------------------------------------------------------------------------------------
Write-Host "`n=== 0. Preconditions ==="
# The runner must be 64-bit or the migration under test cannot happen at all - the bundle would pick x86 on
# both runs and quietly assert nothing. A hard stop, not an assertion, for the same reason the lifecycle
# suite hard-stops on matching ProductVersions.
if (-not [Environment]::Is64BitOperatingSystem) {
    throw 'This suite migrates x86 -> x64 and therefore requires a 64-bit runner.'
}
$baselineVersion = Get-MsiProperty -Path $BaselineX86Msi -Name 'ProductVersion'
Write-Host "  baseline x86 package: $baselineVersion"
Assert-That ((Get-MsiPlatform -Path $BaselineX86Msi) -eq 'Intel') "the baseline package really targets x86"
Assert-That (Test-Path $BundleExe) "the bundle exe exists at $BundleExe"

# --------------------------------------------------------------------------------------------------
Write-Host "`n=== 1. Install the x86 MSI directly ==="
$code = Invoke-Process -FilePath 'msiexec.exe' -Arguments @(
    '/i', $BaselineX86Msi, '/quiet', '/norestart',
    '/l*v', (Join-Path $logDir 'bundle-baseline.log'),
    "DATAFOLDER=$dataFolder",
    'JENKINS_URL=https://127.0.0.1:59999',
    'JENKINS_SECRET=bundle-smoke-secret',
    'JENKINS_SECRET_MODE=Unprotected',
    'JENKINS_AGENT_NAME=bundle-node')
if ($code -notin @(0, 3010)) { throw "baseline x86 install failed with $code" }

Assert-That ((Get-InstalledPlatform) -eq 'x86') "the baseline is recorded as x86"
Assert-That ((Get-PeMachine -Path $agentExe) -eq 'x86') "the installed binary really is x86"

# An operator edit and a data-folder sentinel: these are what prove the migration PRESERVED rather than
# recreated. Without them a purge-then-reinstall would look identical to a clean migration.
$raw = Get-Content $configPath -Raw | ConvertFrom-Json
$raw.Jenkins.Logging.RetainedLogs = 5
$raw | ConvertTo-Json -Depth 8 | Set-Content $configPath -Encoding utf8
$sentinel = Join-Path $dataFolder 'operator-data.txt'
Set-Content -Path $sentinel -Value 'must survive the architecture migration' -Encoding utf8

# --------------------------------------------------------------------------------------------------
Write-Host "`n=== 2. Run the bundle - it must migrate x86 -> x64 unprompted ==="
# No properties at all, and no force flag: the bundle supplies FORCE_UPGRADE=1 itself, because installing the
# machine's native architecture is its policy rather than an option. Anything the install needs beyond that
# has to come from what is already on disk.
$code = Invoke-Process -FilePath $BundleExe -Arguments @(
    '-quiet', '-norestart', '-log', (Join-Path $logDir 'bundle-migrate.log'))
if ($code -notin @(0, 3010)) {
    Write-Host "::error::bundle install failed with exit code $code - see artifact bundle-migrate.log"
    $log = Join-Path $logDir 'bundle-migrate.log'
    if (Test-Path $log) { Get-Content $log -Tail 60 | ForEach-Object { Write-Host "    $_" } }
    throw "bundle exit code $code"
}

Assert-That ((Get-InstalledPlatform) -eq 'x64') "the bundle selected x64 for a 64-bit machine, and only x64"
Assert-That ((Get-PeMachine -Path $agentExe) -eq 'x64') "the binary on disk really is x64 now"

# The purge check. If Burn planned the x86 product as a standalone uninstall instead of letting the x64 MSI's
# MajorUpgrade replace it, PurgeInstallation would have run and taken all four of these with it.
Assert-That (Test-Path $configPath) "appsettings.json survives the migration"
$cfg = if (Test-Path $configPath) { Get-Content $configPath -Raw | ConvertFrom-Json } else { $null }
Assert-That ($null -ne $cfg -and $cfg.Jenkins.Secret.Value -eq 'bundle-smoke-secret') `
    "the agent secret survives the migration"
Assert-That ($null -ne $cfg -and $cfg.Jenkins.Logging.RetainedLogs -eq 5) `
    "the operator's edit survives - the config was preserved, not rewritten"
Assert-That (Test-Path $sentinel) "the data folder survives the migration with its contents"

Assert-That ((Get-RecordedLocation -Platform 'x64' -Name 'DataPath') -eq "$dataFolder\") `
    "the migrated install kept the original data folder, recovered rather than reset to the default"
Assert-That (-not (Test-Path (Join-Path $env:ProgramData 'JenkinsAsService'))) `
    "no stray default data folder was created"

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
Assert-That ($null -ne $service -and $service.Status -eq 'Running') "the service is Running after the migration"

# --------------------------------------------------------------------------------------------------
Write-Host "`n=== 3. Re-running the bundle on a matching architecture is a no-op, not a reinstall ==="
$code = Invoke-Process -FilePath $BundleExe -Arguments @(
    '-quiet', '-norestart', '-log', (Join-Path $logDir 'bundle-repeat.log'))
Assert-That ($code -in @(0, 3010)) "re-running the bundle succeeds (exit code $code)"
$cfg = if (Test-Path $configPath) { Get-Content $configPath -Raw | ConvertFrom-Json } else { $null }
Assert-That ($null -ne $cfg -and $cfg.Jenkins.Secret.Value -eq 'bundle-smoke-secret') `
    "the secret survives re-running the bundle over an identical install"

# --------------------------------------------------------------------------------------------------
Write-Host "`n=== 4. Uninstall through the bundle ==="
$code = Invoke-Process -FilePath $BundleExe -Arguments @(
    '-uninstall', '-quiet', '-norestart', '-log', (Join-Path $logDir 'bundle-uninstall.log'))
Assert-That ($code -in @(0, 3010)) "the bundle uninstalls cleanly (exit code $code)"
Assert-That ($null -eq (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) "service is removed"
Assert-That (-not (Test-Path $installFolder)) "the install folder is removed"
Assert-That (-not (Test-Path $dataFolder)) "the data folder is removed"
Assert-That ($null -eq (Get-InstalledPlatform)) "neither architecture key survives the uninstall"

# --------------------------------------------------------------------------------------------------
Write-Host ''
if ($script:Failures.Count -gt 0) {
    Write-Host "::error::$($script:Failures.Count) bundle assertion(s) failed"
    $script:Failures | ForEach-Object { Write-Host "::error::  $_" }
    exit 1
}

Write-Host "All bundle assertions passed."
