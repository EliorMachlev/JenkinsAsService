<#
.SYNOPSIS
    End-to-end test of the x64 <-> x86 migration policy, in both directions.

.DESCRIPTION
    The two packages share an UpgradeCode, so Windows Installer will happily let one replace the other and
    FindRelatedProducts cannot tell that the architecture changed. Left alone that is a foot-gun with no undo:
    MajorUpgrade is scheduled afterInstallInitialize, so by the time anyone notices, the old product is gone.

    The policy is deliberately ASYMMETRIC, and this asserts both halves of it:

      x86 install -> x64 package  SUPPORTED, opt-in via FORCE_UPGRADE=1. The install folder, data folder,
                                  config and secret are all carried across.
      x64 install -> x86 package  REFUSED, and FORCE_UPGRADE does NOT override it. A 32-bit package cannot
                                  recover a 64-bit install folder: the registry search works, but Windows
                                  Installer's WIN64DUALFOLDERS substitution rewrites the result's
                                  `C:\Program Files\` prefix to `C:\Program Files (x86)\`. The migration
                                  would install beside the real one and strand appsettings.json - the only
                                  copy of the secret - at the original path. CI caught exactly that, as a
                                  1603 from UpgradeConfig running an exe with no config beside it.

    Nothing is lost by refusing: the bundle's policy is the machine's NATIVE architecture, so it only ever
    needs the supported direction (x64 on a 64-bit machine; on a 32-bit machine no x64 install can exist).

    SCOPE. The gate's rule is 64-bit vs 32-bit rather than x64 vs x86, so arm64 sits on the supported side
    alongside x64 and takes the same code path through InstallLocation.wxs - one condition, one error string,
    one recovery chain, differing only in which property names they name. This suite exercises the x64/x86
    pair, because the REFUSED direction is the half worth paying an install for and only x86 can be refused.
    The arm64 half of the same authoring is covered on real ARM64 silicon by the msi-lifecycle-arm64 job,
    which runs Test-Bundle with the x64 -> arm64 pair.

    Asserting only one half would be worthless. Asserting only the forced half would let a gate that never
    blocks anything pass; asserting only the blocked half would let a gate that blocks everything - including
    the migration it is supposed to permit - pass just as easily.

    No Jenkins controller is involved: the URL points at a closed port, so the agent never connects.
#>
[CmdletBinding()]
# Write-Host is deliberate - see the note in Test-MsiLifecycle.ps1; this is a CI transcript, not a pipeline.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute(
    'PSAvoidUsingWriteHost', '', Justification = 'Intentional CI transcript output')]
param(
    # x64, and the LOWEST version of the three: the refused direction is attempted as an upgrade of it.
    [Parameter(Mandatory)][string]$X64BaselineMsi,
    # x86, at a HIGHER version than the baseline - so the refused attempt is a genuine upgrade candidate and
    # is refused on architecture rather than on a version rule.
    [Parameter(Mandatory)][string]$X86Msi,
    # x64, at a HIGHER version than the x86 package: the supported migration's target.
    [Parameter(Mandatory)][string]$X64TargetMsi
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'MsiQuery.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'MsiTestHelpers.psm1') -Force

# The x64 and x86 packages install to DIFFERENT default folders - ProgramFiles6432Folder resolves to
# "C:\Program Files" in a 64-bit package and "C:\Program Files (x86)" in a 32-bit one. Which folder the
# migrated install ends up in is the whole point of the supported half, so both are named here.
$x64InstallFolder = Get-JasDefaultInstallFolder -Platform 'x64'
$x86InstallFolder = Get-JasDefaultInstallFolder -Platform 'x86'
$dataFolder = Join-Path $env:ProgramData 'JenkinsAsServiceArchTest'
$serviceName = Get-JasServiceName

$freshInstallProperties = @(
    "DATAFOLDER=$dataFolder",
    'JENKINS_URL=https://127.0.0.1:59999',
    'JENKINS_SECRET=arch-smoke-secret',
    'JENKINS_SECRET_MODE=Unprotected',
    'JENKINS_AGENT_NAME=arch-node'
)

# Asserts a refused install was also INERT. BlockArchMigration is sequenced with the location searches it
# reads, far ahead of InstallInitialize (1500) and RemoveExistingProducts (1501), so nothing should have been
# removed, moved or reconfigured - a gate that blocks but damages the installed product on its way out is not
# a gate.
function Assert-RefusalWasInert {
    param(
        [Parameter(Mandatory)][ValidateSet('x64', 'x86')][string]$ExpectedPlatform,
        [Parameter(Mandatory)][string]$ConfigPath,
        [Parameter(Mandatory)][string]$What
    )
    Assert-That ((Get-InstalledPlatform) -eq $ExpectedPlatform) `
        "$What : the installed product is still $ExpectedPlatform"
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    Assert-That ($null -ne $service -and $service.Status -eq 'Running') `
        "$What : the service is still Running - a blocked install must not disturb the installed one"
    $cfg = Get-InstalledConfig -Path $ConfigPath
    Assert-That ($null -ne $cfg -and $cfg.Jenkins.Secret.Value -eq 'arch-smoke-secret') `
        "$What : the config and secret are untouched"
}

# --------------------------------------------------------------------------------------------------
Write-Host "`n=== 0. Package preconditions ==="
$baselineVersion = Get-MsiProperty -Path $X64BaselineMsi -Name 'ProductVersion'
$x86Version = Get-MsiProperty -Path $X86Msi -Name 'ProductVersion'
$targetVersion = Get-MsiProperty -Path $X64TargetMsi -Name 'ProductVersion'

Assert-VersionLadder -Rungs ([ordered]@{
        'x64 baseline' = $baselineVersion
        'x86 package'  = $x86Version
        'x64 target'   = $targetVersion
    })

Assert-That ((Get-MsiArchitecture -Path $X64BaselineMsi) -eq 'x64') 'the baseline package really targets x64'
Assert-That ((Get-MsiArchitecture -Path $X86Msi) -eq 'x86') 'the x86 package really targets x86'
Assert-That ((Get-MsiArchitecture -Path $X64TargetMsi) -eq 'x64') 'the target package really targets x64'

# ==================================================================================================
# PART ONE - the refused direction: x64 installed, x86 package must not take over.
# ==================================================================================================
Write-Host "`n=== 1. Install x64 $baselineVersion ==="
$log = Get-MsiLogPath -Name 'arch-x64-install'
$code = Invoke-Msiexec -LogPath $log -Arguments (@('/i', $X64BaselineMsi) + $freshInstallProperties)
Assert-InstallerSucceeded -ExitCode $code -LogPath $log -Activity 'x64 baseline install'

$x64ConfigPath = Join-Path $x64InstallFolder 'appsettings.json'
Assert-That ((Get-InstalledPlatform) -eq 'x64') 'installed architecture recorded as x64, and under that key only'
Assert-That (Test-Path $x64ConfigPath) "appsettings.json written to $x64InstallFolder"
Assert-That ((Get-RecordedLocation -Platform 'x64' -Name 'InstallPath') -eq "$x64InstallFolder\") `
    'the x64 install recorded its real install folder'

Write-Host "`n=== 2. x86 $x86Version must be refused - with AND without FORCE_UPGRADE ==="
# Without the flag first, then with it. The second is the one that matters: this direction is refused
# outright, so FORCE_UPGRADE must NOT be a way through. If it ever becomes one, the migration silently
# installs to C:\Program Files (x86) and strands the secret.
foreach ($attempt in @(
        @{ Name = 'arch-x86-blocked'; Args = @('/i', $X86Msi); What = 'x86 over x64 without FORCE_UPGRADE' },
        @{ Name = 'arch-x86-forced'; Args = @('/i', $X86Msi, 'FORCE_UPGRADE=1'); What = 'x86 over x64 WITH FORCE_UPGRADE' })) {
    $code = Invoke-Msiexec -LogPath (Get-MsiLogPath -Name $attempt.Name) -Arguments $attempt.Args
    Assert-That (-not (Test-InstallerSuccess -ExitCode $code)) `
        "$($attempt.What) is refused (msiexec exit code $code)"
    Assert-RefusalWasInert -ExpectedPlatform 'x64' -ConfigPath $x64ConfigPath -What $attempt.What
}

Assert-That (-not (Test-Path (Join-Path $x86InstallFolder 'JenkinsAsService.exe'))) `
    'the refused x86 install left nothing in the 32-bit Program Files - it never got as far as installing'

Write-Host "`n=== 3. Remove the x64 install ==="
Invoke-Msiexec -LogPath (Get-MsiLogPath -Name 'arch-x64-uninstall') -Arguments @('/x', $X64BaselineMsi) | Out-Null
Assert-That ($null -eq (Get-InstalledPlatform)) 'no architecture key survives the x64 uninstall'
Assert-That (-not (Test-Path $dataFolder)) 'the data folder is removed by the x64 uninstall'

# ==================================================================================================
# PART TWO - the supported direction: x86 installed, x64 package migrates it on FORCE_UPGRADE=1.
# ==================================================================================================
Write-Host "`n=== 4. Install x86 $x86Version ==="
$log = Get-MsiLogPath -Name 'arch-x86-install'
$code = Invoke-Msiexec -LogPath $log -Arguments (@('/i', $X86Msi) + $freshInstallProperties)
Assert-InstallerSucceeded -ExitCode $code -LogPath $log -Activity 'x86 install'

$x86ConfigPath = Join-Path $x86InstallFolder 'appsettings.json'
Assert-That ((Get-InstalledPlatform) -eq 'x86') 'installed architecture recorded as x86, and under that key only'
Assert-That ((Get-PeMachine -Path (Join-Path $x86InstallFolder 'JenkinsAsService.exe')) -eq 'x86') `
    'the installed binary really is x86'
Assert-That ((Get-RecordedLocation -Platform 'x86' -Name 'InstallPath') -eq "$x86InstallFolder\") `
    'the x86 install recorded its real install folder'

# An operator edit, so "the config survived" means the real file survived rather than an identical one having
# been rewritten from the properties below (which are deliberately not supplied to either attempt).
$raw = Get-Content $x86ConfigPath -Raw | ConvertFrom-Json
$raw.Jenkins.Logging.RetainedLogs = 7
$raw | ConvertTo-Json -Depth 8 | Set-Content $x86ConfigPath -Encoding utf8

Write-Host "`n=== 5. x64 $targetVersion WITHOUT FORCE_UPGRADE must be refused ==="
$code = Invoke-Msiexec -LogPath (Get-MsiLogPath -Name 'arch-x64-blocked') -Arguments @('/i', $X64TargetMsi)
Assert-That (-not (Test-InstallerSuccess -ExitCode $code)) `
    "x64 over x86 without FORCE_UPGRADE is refused (msiexec exit code $code)"
Assert-RefusalWasInert -ExpectedPlatform 'x86' -ConfigPath $x86ConfigPath -What 'x64 over x86 without FORCE_UPGRADE'

Write-Host "`n=== 6. x64 $targetVersion WITH FORCE_UPGRADE=1 must succeed and preserve everything ==="
# No DATAFOLDER, no URL, no secret: a migration must recover all of that from what is already recorded on the
# machine. Supplying any of it would hide a recovery that never happened.
$log = Get-MsiLogPath -Name 'arch-x64-forced'
$code = Invoke-Msiexec -LogPath $log -Arguments @('/i', $X64TargetMsi, 'FORCE_UPGRADE=1')
Assert-InstallerSucceeded -ExitCode $code -LogPath $log -Activity 'forced x86 -> x64 migration'

# Not just "the new key exists" - the OLD one has to be gone, or the next package sees two architectures
# installed at once and the gate starts firing against a product that no longer exists.
Assert-That ((Get-InstalledPlatform) -eq 'x64') `
    'the recorded architecture is now x64, and only x64 - the x86 key is gone'

# The load-bearing assertion of this half: the migrated x64 install stayed in the folder the x86 install used,
# recovered from the registry rather than reset to the x64 default. If this ever fails, appsettings.json and
# the secret have been stranded at the old path - which is the failure the refused direction suffers from.
Assert-That ((Get-RecordedLocation -Platform 'x64' -Name 'InstallPath') -eq "$x86InstallFolder\") `
    "the migrated install kept the original install folder ($x86InstallFolder), not the x64 default"
Assert-That ((Get-RecordedLocation -Platform 'x64' -Name 'DataPath') -eq "$dataFolder\") `
    'the migrated install kept the original data folder, recovered rather than reset to the default'
Assert-That (-not (Test-Path (Join-Path $env:ProgramData 'JenkinsAsService'))) `
    'no stray default data folder was created by the migration'
Assert-That (-not (Test-Path (Join-Path $x64InstallFolder 'JenkinsAsService.exe'))) `
    'nothing was installed into the x64 default folder - the recovered location was used'

Assert-That ((Get-PeMachine -Path (Join-Path $x86InstallFolder 'JenkinsAsService.exe')) -eq 'x64') `
    'the binary at the original path really is x64 now - the architecture actually changed'

$cfg = Get-InstalledConfig -Path $x86ConfigPath
Assert-That ($null -ne $cfg) 'appsettings.json still exists after the migration'
Assert-That ($cfg.Jenkins.Secret.Value -eq 'arch-smoke-secret') `
    'the secret survives a migration that was never given JENKINS_SECRET'
Assert-That ($cfg.Jenkins.Logging.RetainedLogs -eq 7) "the operator's edit survives the migration"
Assert-That ($cfg.Jenkins.Connection.AgentName -eq 'arch-node') 'an unrelated value survives the migration'

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
Assert-That ($null -ne $service -and $service.Status -eq 'Running') 'the service is Running after the migration'

# --------------------------------------------------------------------------------------------------
Write-Host "`n=== 7. Clean up ==="
Invoke-Msiexec -LogPath (Get-MsiLogPath -Name 'arch-final-uninstall') -Arguments @('/x', $X64TargetMsi) | Out-Null
Assert-That ($null -eq (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) 'service is removed'
Assert-That (-not (Test-Path $dataFolder)) 'the data folder is removed'
Assert-That ($null -eq (Get-InstalledPlatform)) 'neither architecture key survives the uninstall'

# --------------------------------------------------------------------------------------------------
Complete-AssertionReport -Subject 'architecture-migration'
