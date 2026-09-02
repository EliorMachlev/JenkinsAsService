<#
.SYNOPSIS
    End-to-end test of the single self-selecting installer .exe (the Burn bundle).

.DESCRIPTION
    The bundle's whole job is to install the architecture that matches the machine and to migrate an install
    of another one. The second half is the dangerous half, and it is dangerous in a way that reads as
    success: the MSI's purge custom action deletes the config, the data folder and the secret, and it is
    gated only on REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE. If Burn were ever to plan the old
    architecture's product as a standalone UNINSTALL rather than letting the incoming MSI's MajorUpgrade
    replace it, that purge would fire - and the migration would then "succeed" onto a machine whose
    configuration and agent secret had just been destroyed.

    Reasoning says Burn treats it as an upgrade, because the MSIs share an UpgradeCode and the chain's own
    package for the baseline architecture (a different ProductCode) is not detected as present. Reasoning is
    not evidence for something with that failure mode, so this installs the baseline MSI directly, runs the
    bundle over it, and asserts the config, the operator's edit, the secret and the data folder all survived.

    The architecture pair is a parameter, and there are two real ones, each needing its own runner:
        x86 -> x64      on an x64 runner
        x64 -> arm64    on an ARM64 runner. This is the path EVERY existing ARM64 install takes: before there
                        was an arm64 package those machines all received the x64 one and ran it emulated, so
                        the first upgrade after this ships is a live cross-architecture migration on
                        somebody's build agent.

    Asserted here:
      * the bundle installs the architecture matching the machine - checked against the PE header of the
        binary that actually landed, not against what the package claimed
      * it installs EXACTLY ONE architecture: the chain has three mutually exclusive conditions, and the one
        deciding between the two 64-bit packages is NativeMachine, a Burn variable rather than a build-time
        fact. The x64 run exercises its negative half (NativeMachine <> ARM64) and the ARM64 run its positive
        half - and the negative half is the one that fails SILENTLY, since a machine wrongly offered the
        emulated x64 package installs and runs perfectly well
      * it migrates an install of another architecture, without being told to
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
    # The MSI to install first, at a LOWER version than the bundle, targeting an architecture the bundle will
    # NOT choose on this runner - so running the bundle over it is a migration.
    [Parameter(Mandatory)][string]$BaselineMsi,
    # What the baseline targets, and what the bundle must choose here. Parameterised rather than hard-coded
    # because that pair IS the run; the two real pairs are in the description above.
    [Parameter(Mandatory)][ValidateSet('x64', 'x86', 'arm64')][string]$BaselineArchitecture,
    [Parameter(Mandatory)][ValidateSet('x64', 'x86', 'arm64')][string]$ExpectedArchitecture,
    # Where to install the baseline; defaults to that architecture's default folder. The ARM64 run passes a
    # NON-default path, because x64 and arm64 share a default - both are 64-bit, so ProgramFiles6432Folder is
    # the real C:\Program Files for either - and with the two folders identical, "the migration kept the
    # original folder" would hold no matter what the recovery actually did.
    [string]$BaselineInstallFolder,
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

# Where the baseline lands, and therefore what the migration must PRESERVE rather than reset - the same path
# before and after the bundle runs. It follows the BASELINE's architecture (ProgramFiles6432Folder resolves to
# "C:\Program Files (x86)" in a 32-bit package and the real "C:\Program Files" in either 64-bit one), so
# assuming $env:ProgramFiles would look for the baseline in a folder it was never installed to.
$installFolder = if ($BaselineInstallFolder) { $BaselineInstallFolder }
else { Get-JasDefaultInstallFolder -Platform $BaselineArchitecture }
$migratedDefaultInstallFolder = Get-JasDefaultInstallFolder -Platform $ExpectedArchitecture
$dataFolder = Join-Path $env:ProgramData 'JenkinsAsServiceBundleTest'
$configPath = Join-Path $installFolder 'appsettings.json'
$agentExe = Join-Path $installFolder 'JenkinsAsService.exe'
$serviceName = Get-JasServiceName

# --------------------------------------------------------------------------------------------------
Write-Host "`n=== 0. Preconditions ==="
# The runner must be 64-bit or the migration under test cannot happen at all - the bundle would pick x86 on
# both runs and quietly assert nothing. A hard stop, not an assertion, for the same reason the lifecycle
# suite hard-stops on matching ProductVersions.
# The runner must NATIVELY be the architecture the bundle is expected to choose, because that choice is what
# is under test: the chain decides from NativeMachine, which reports the silicon rather than any emulation.
# On the wrong runner the bundle picks something else and the suite asserts nothing while reporting green.
# A hard stop, not an assertion, for the same reason the version ladder is one.
$nativeArchitecture = "$([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)".ToLowerInvariant()
if ($nativeArchitecture -ne $ExpectedArchitecture) {
    throw ("This run expects the bundle to install $ExpectedArchitecture, so it must run on a native " +
        "$ExpectedArchitecture machine - this one reports '$nativeArchitecture'.")
}
if ($BaselineArchitecture -eq $ExpectedArchitecture) {
    throw 'The baseline must target a DIFFERENT architecture than the bundle installs, or nothing migrates.'
}
$baselineVersion = Get-MsiProperty -Path $BaselineMsi -Name 'ProductVersion'

# The rung this suite depends on and, until now, the only one in the whole ladder enforced by nothing but a
# comment in build.yml. If the bundle's packages are not above the baseline installed below, MajorUpgrade
# refuses the migration on a VERSION rule - and the run then fails in a way that reads as a statement about
# architecture, which is the one thing this suite exists to measure.
Assert-VersionLadder -Rungs ([ordered]@{
        "$BaselineArchitecture baseline" = $baselineVersion
        'bundle package'                 = $BundleVersion
    })

Assert-That ((Get-MsiArchitecture -Path $BaselineMsi) -eq $BaselineArchitecture) `
    "the baseline package really targets $BaselineArchitecture"
Assert-That (Test-Path $BundleExe) "the bundle exe exists at $BundleExe"

# --------------------------------------------------------------------------------------------------
Write-Host "`n=== 1. Install the $BaselineArchitecture MSI directly ==="
$log = Get-MsiLogPath -Name 'bundle-baseline'
$baselineArguments = @(
    '/i', $BaselineMsi,
    "DATAFOLDER=$dataFolder",
    'JENKINS_URL=https://127.0.0.1:59999',
    'JENKINS_SECRET=bundle-smoke-secret',
    'JENKINS_SECRET_MODE=Unprotected',
    'JENKINS_AGENT_NAME=bundle-node')

# INSTALLFOLDER is passed ONLY when the caller named one, never as "the default, stated explicitly". The
# defaults contain spaces (C:\Program Files\Jenkins, and the (x86) variant also brackets), and these
# arguments reach msiexec through Start-Process -ArgumentList, which joins the array on spaces WITHOUT
# quoting - so INSTALLFOLDER=C:\Program Files (x86)\Jenkins would arrive as three separate arguments and
# msiexec would take the first as a malformed property. Callers that need a non-default folder pass one
# without spaces; callers that do not let the package pick, exactly as before.
if ($BaselineInstallFolder) { $baselineArguments += "INSTALLFOLDER=$BaselineInstallFolder" }

$code = Invoke-Msiexec -LogPath $log -Arguments $baselineArguments
Assert-InstallerSucceeded -ExitCode $code -LogPath $log -Activity "baseline $BaselineArchitecture install"

Assert-That ((Get-InstalledPlatform) -eq $BaselineArchitecture) `
    "the baseline is recorded as $BaselineArchitecture"
Assert-That ((Get-PeMachine -Path $agentExe) -eq $BaselineArchitecture) `
    "the installed binary really is $BaselineArchitecture"

# An operator edit and a data-folder sentinel: these are what prove the migration PRESERVED rather than
# recreated. Without them a purge-then-reinstall would look identical to a clean migration.
$raw = Get-Content $configPath -Raw | ConvertFrom-Json
$raw.Jenkins.Logging.RetainedLogs = 5
$raw | ConvertTo-Json -Depth 8 | Set-Content $configPath -Encoding utf8
$sentinel = Join-Path $dataFolder 'operator-data.txt'
Set-Content -Path $sentinel -Value 'must survive the architecture migration' -Encoding utf8

# --------------------------------------------------------------------------------------------------
Write-Host "`n=== 2. Run the bundle - it must migrate $BaselineArchitecture -> $ExpectedArchitecture unprompted ==="
# No properties at all, and no force flag: the bundle supplies FORCE_UPGRADE=1 itself, because installing the
# machine's native architecture is its policy rather than an option. Anything the install needs beyond that
# has to come from what is already on disk.
$log = Get-MsiLogPath -Name 'bundle-migrate'
$code = Invoke-InstallerProcess -FilePath $BundleExe -Arguments @('-quiet', '-norestart', '-log', $log)
Assert-InstallerSucceeded -ExitCode $code -LogPath $log -Activity 'bundle install' -TailLines 60

# Get-InstalledPlatform enumerates ALL three location keys and answers 'multiple' if more than one holds a
# path, so this single assertion carries three claims at once: the expected architecture was chosen, and
# neither of the other two was. That is how NativeMachine ends up tested in both directions across the two
# runs, without either run needing to know what the other asserts.
Assert-That ((Get-InstalledPlatform) -eq $ExpectedArchitecture) `
    "the bundle selected $ExpectedArchitecture on this native $nativeArchitecture machine, and exactly one architecture"
Assert-That ((Get-PeMachine -Path $agentExe) -eq $ExpectedArchitecture) `
    "the binary on disk really is $ExpectedArchitecture now"

# The purge check. If Burn planned the old product as a standalone uninstall instead of letting the incoming
# MSI's MajorUpgrade replace it, PurgeInstallation would have run and taken all four of these with it.
Assert-That (Test-Path $configPath) "appsettings.json survives the migration"
$cfg = Get-InstalledConfig -Path $configPath
Assert-That ($null -ne $cfg -and $cfg.Jenkins.Secret.Value -eq 'bundle-smoke-secret') `
    "the agent secret survives the migration"
Assert-That ($null -ne $cfg -and $cfg.Jenkins.Logging.RetainedLogs -eq 5) `
    "the operator's edit survives - the config was preserved, not rewritten"
Assert-That (Test-Path $sentinel) "the data folder survives the migration with its contents"

Assert-That ((Get-RecordedLocation -Platform $ExpectedArchitecture -Name 'InstallPath') -eq "$installFolder\") `
    "the migrated install kept the original install folder, recovered rather than reset to a default"
# Only meaningful when the two architectures HAVE different defaults. x64 and arm64 share one, so on that
# pair the baseline goes to an explicit non-default folder instead and this is skipped rather than asserted
# against the folder the install is legitimately sitting in.
if ($migratedDefaultInstallFolder -ne $installFolder) {
    Assert-That (-not (Test-Path (Join-Path $migratedDefaultInstallFolder 'JenkinsAsService.exe'))) `
        "nothing was installed into the $ExpectedArchitecture default folder - the recovered location was used"
}
Assert-That ((Get-RecordedLocation -Platform $ExpectedArchitecture -Name 'DataPath') -eq "$dataFolder\") `
    "the migrated install kept the original data folder, recovered rather than reset to the default"
Assert-That (-not (Test-Path (Join-Path $env:ProgramData 'JenkinsAsService'))) `
    "no stray default data folder was created"

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
Assert-That ($null -ne $service -and $service.Status -eq 'Running') "the service is Running after the migration"

# The identity, asserted through the BUNDLE rather than only through a bare msiexec run. This is the whole
# security posture - a least-privilege virtual account that cannot write the install folder - and the bundle
# is the primary installer, so testing it only via the MSI tested the path almost nobody uses.
# It was silently broken: the bundle forwarded every setting unconditionally, and SERVICE_ACCOUNT= on the
# msiexec command line does not mean "unset", it OVERRIDES the Property table default. Every bundle install
# registered the service with an empty StartName, which SCM resolves to LocalSystem.
$startName = (Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue).StartName
Assert-That ($startName -eq "NT SERVICE\$serviceName") `
    "the bundle install runs as the least-privilege virtual account, not LocalSystem (StartName='$startName')"

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
