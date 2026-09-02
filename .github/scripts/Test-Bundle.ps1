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
      * it installs EXACTLY ONE architecture: the chain now has three mutually exclusive conditions, and the
        one that decides between the two 64-bit packages is NativeMachine, a Burn variable rather than a
        build-time fact. On this x64 runner that exercises its negative half - which is the half that fails
        SILENTLY, since a machine wrongly offered the emulated x64 package installs and runs perfectly well
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
    [Parameter(Mandatory)][string]$BundleExe,
    # The version the MSIs INSIDE the bundle carry. Passed in rather than read back, because a Burn bundle
    # keeps its chain in an attached container: getting at the embedded packages' ProductVersion means
    # unpacking the .exe, and a wrong value read out of the file version block would be worse than none.
    # This is the rung of the ladder that had no check at all - see the note at the ladder assertion below.
    [Parameter(Mandatory)][string]$BundleVersion
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'MsiQuery.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'MsiTestHelpers.psm1') -Force

# The 32-bit Program Files, because the baseline installed here is the x86 package: ProgramFiles6432Folder
# resolves to "C:\Program Files (x86)" in a 32-bit package. The migration must PRESERVE this folder rather
# than relocate to the x64 default, so it stays the expected path throughout - before and after the bundle
# runs. Using $env:ProgramFiles here would have looked for the baseline in a folder it was never installed to.
$installFolder = Get-JasDefaultInstallFolder -Platform 'x86'
$x64DefaultInstallFolder = Get-JasDefaultInstallFolder -Platform 'x64'
$dataFolder = Join-Path $env:ProgramData 'JenkinsAsServiceBundleTest'
$configPath = Join-Path $installFolder 'appsettings.json'
$agentExe = Join-Path $installFolder 'JenkinsAsService.exe'
$serviceName = Get-JasServiceName

# --------------------------------------------------------------------------------------------------
Write-Host "`n=== 0. Preconditions ==="
# The runner must be 64-bit or the migration under test cannot happen at all - the bundle would pick x86 on
# both runs and quietly assert nothing. A hard stop, not an assertion, for the same reason the lifecycle
# suite hard-stops on matching ProductVersions.
if (-not [Environment]::Is64BitOperatingSystem) {
    throw 'This suite migrates x86 -> x64 and therefore requires a 64-bit runner.'
}
$baselineVersion = Get-MsiProperty -Path $BaselineX86Msi -Name 'ProductVersion'

# The rung this suite depends on and, until now, the only one in the whole ladder enforced by nothing but a
# comment in build.yml. If the bundle's packages are not above the x86 baseline installed below, MajorUpgrade
# refuses the migration on a VERSION rule - and the run then fails in a way that reads as a statement about
# architecture, which is the one thing this suite exists to measure.
Assert-VersionLadder -Rungs ([ordered]@{
        'x86 baseline'   = $baselineVersion
        'bundle package' = $BundleVersion
    })

Assert-That ((Get-MsiArchitecture -Path $BaselineX86Msi) -eq 'x86') "the baseline package really targets x86"
Assert-That (Test-Path $BundleExe) "the bundle exe exists at $BundleExe"

# --------------------------------------------------------------------------------------------------
Write-Host "`n=== 1. Install the x86 MSI directly ==="
$log = Get-MsiLogPath -Name 'bundle-baseline'
$code = Invoke-Msiexec -LogPath $log -Arguments @(
    '/i', $BaselineX86Msi,
    "DATAFOLDER=$dataFolder",
    'JENKINS_URL=https://127.0.0.1:59999',
    'JENKINS_SECRET=bundle-smoke-secret',
    'JENKINS_SECRET_MODE=Unprotected',
    'JENKINS_AGENT_NAME=bundle-node')
Assert-InstallerSucceeded -ExitCode $code -LogPath $log -Activity 'baseline x86 install'

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
$log = Get-MsiLogPath -Name 'bundle-migrate'
$code = Invoke-InstallerProcess -FilePath $BundleExe -Arguments @('-quiet', '-norestart', '-log', $log)
Assert-InstallerSucceeded -ExitCode $code -LogPath $log -Activity 'bundle install' -TailLines 60

# Get-InstalledPlatform enumerates ALL three location keys and answers 'multiple' if more than one holds a
# path, so this single assertion carries three claims at once: x64 was chosen, arm64 was not (NativeMachine
# evaluated, and evaluated to something other than ARM64), and x86 was not.
Assert-That ((Get-InstalledPlatform) -eq 'x64') `
    "the bundle selected x64 for this x64 machine, and exactly one architecture"
Assert-That ((Get-PeMachine -Path $agentExe) -eq 'x64') "the binary on disk really is x64 now"

# The purge check. If Burn planned the x86 product as a standalone uninstall instead of letting the x64 MSI's
# MajorUpgrade replace it, PurgeInstallation would have run and taken all four of these with it.
Assert-That (Test-Path $configPath) "appsettings.json survives the migration"
$cfg = Get-InstalledConfig -Path $configPath
Assert-That ($null -ne $cfg -and $cfg.Jenkins.Secret.Value -eq 'bundle-smoke-secret') `
    "the agent secret survives the migration"
Assert-That ($null -ne $cfg -and $cfg.Jenkins.Logging.RetainedLogs -eq 5) `
    "the operator's edit survives - the config was preserved, not rewritten"
Assert-That (Test-Path $sentinel) "the data folder survives the migration with its contents"

Assert-That ((Get-RecordedLocation -Platform 'x64' -Name 'InstallPath') -eq "$installFolder\") `
    "the migrated install kept the original install folder, recovered rather than reset to the x64 default"
Assert-That (-not (Test-Path (Join-Path $x64DefaultInstallFolder 'JenkinsAsService.exe'))) `
    "nothing was installed into the x64 default folder - the recovered location was used"
Assert-That ((Get-RecordedLocation -Platform 'x64' -Name 'DataPath') -eq "$dataFolder\") `
    "the migrated install kept the original data folder, recovered rather than reset to the default"
Assert-That (-not (Test-Path (Join-Path $env:ProgramData 'JenkinsAsService'))) `
    "no stray default data folder was created"

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
Assert-That ($null -ne $service -and $service.Status -eq 'Running') "the service is Running after the migration"

# --------------------------------------------------------------------------------------------------
Write-Host "`n=== 3. Re-running the bundle on a matching architecture is a no-op, not a reinstall ==="
$code = Invoke-InstallerProcess -FilePath $BundleExe -Arguments @(
    '-quiet', '-norestart', '-log', (Get-MsiLogPath -Name 'bundle-repeat'))
Assert-That (Test-InstallerSuccess -ExitCode $code) "re-running the bundle succeeds (exit code $code)"
$cfg = Get-InstalledConfig -Path $configPath
Assert-That ($null -ne $cfg -and $cfg.Jenkins.Secret.Value -eq 'bundle-smoke-secret') `
    "the secret survives re-running the bundle over an identical install"

# --------------------------------------------------------------------------------------------------
Write-Host "`n=== 4. Uninstall through the bundle ==="
$code = Invoke-InstallerProcess -FilePath $BundleExe -Arguments @(
    '-uninstall', '-quiet', '-norestart', '-log', (Get-MsiLogPath -Name 'bundle-uninstall'))
Assert-That (Test-InstallerSuccess -ExitCode $code) "the bundle uninstalls cleanly (exit code $code)"
Assert-That ($null -eq (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) "service is removed"
Assert-That (-not (Test-Path $installFolder)) "the install folder is removed"
Assert-That (-not (Test-Path $dataFolder)) "the data folder is removed"
Assert-That ($null -eq (Get-InstalledPlatform)) "neither architecture key survives the uninstall"

# --------------------------------------------------------------------------------------------------
Complete-AssertionReport -Subject 'bundle'
