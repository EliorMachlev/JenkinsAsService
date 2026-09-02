<#
.SYNOPSIS
    End-to-end MSI test: install, in-place upgrade, uninstall.

.DESCRIPTION
    ServiceSettingsNormalizer is unit-tested, but the thing that actually breaks on a customer box is the
    custom-action SEQUENCING during a real upgrade - WIX_UPGRADE_DETECTED gating, UpgradeConfig running
    before StartServices, the config surviving the RemoveExistingProducts pass. None of that is reachable
    from a unit test, so it sat on the "not verified against real infra" list indefinitely.

    Asserted here:
      * install registers the service, writes appsettings.json, creates the data folder
      * an in-place upgrade PRESERVES operator edits, the secret, and the data folder
      * the upgrade ADDS a schema key missing from the old config, at its default
      * the upgrade PRUNES a key the schema does not define
      * uninstall removes the service, the install folder (incl. the CA-generated appsettings.json) AND the
        data folder - neither of the latter two is a tracked MSI file, so both depend on the purge custom
        action, which must fire on a real uninstall and never during an upgrade's removal of the old product

    No Jenkins controller is involved: the URL points at a closed port, so the agent never connects. That is
    fine - every assertion is about files, the registry and SCM, not about connectivity.
#>
[CmdletBinding()]
# Write-Host is deliberate: this writes a human-readable PASS/FAIL transcript to the CI console. The
# alternatives are wrong here - Write-Output would pollute the return value of the functions it is called
# from, and Write-Verbose/Information are hidden by default, which is the opposite of what a test log needs.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute(
    'PSAvoidUsingWriteHost', '', Justification = 'Intentional CI transcript output')]
param(
    [Parameter(Mandatory)][string]$V1Msi,
    [Parameter(Mandatory)][string]$V2Msi
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'MsiQuery.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'MsiTestHelpers.psm1') -Force

$installFolder = Get-JasDefaultInstallFolder -Platform 'x64'
$dataFolder = Join-Path $env:ProgramData 'JenkinsAsServiceTest'
$configPath = Join-Path $installFolder 'appsettings.json'
$serviceName = Get-JasServiceName
$productName = 'Jenkins Agent Service'
# Fresh-install properties. The upgrade deliberately supplies NONE of these - see step 2.
$installProperties = @(
    "DATAFOLDER=$dataFolder",
    'JENKINS_URL=https://127.0.0.1:59999',
    'JENKINS_SECRET=ci-smoke-secret',
    'JENKINS_SECRET_MODE=Unprotected'
)

# Where the package records the locations it installed to, so the NEXT package can find them instead of
# resetting both to their defaults. Each architecture owns a named subkey, and which one exists IS the
# statement of what is installed. This job installs x64, which writes the native 64-bit view. The paths
# themselves live in MsiTestHelpers, which is what Get-RecordedLocation -Platform reads.
$locationKey = Get-JasLocationKeyPath -Platform 'x64'
$otherArchKey = Get-JasLocationKeyPath -Platform 'x86'

# Every install here must succeed, so the exit code is checked rather than returned.
function Invoke-Msi {
    param([Parameter(Mandatory)][string[]]$Arguments, [Parameter(Mandatory)][string]$LogName)
    $log = Get-MsiLogPath -Name $LogName
    $code = Invoke-Msiexec -Arguments $Arguments -LogPath $log
    Assert-InstallerSucceeded -ExitCode $code -LogPath $log -Activity 'msiexec'
}

# The installed product's version, from the uninstall registry keys.
#
# Deliberately NOT Win32_Product: querying that class makes Windows Installer walk every installed package
# and reconfigure it, which took 1m42s of this job's runtime and can itself repair or alter packages - a
# test must not mutate the machine state it is inspecting. The registry is read-only and immediate.
# Every property access is guarded through PSObject.Properties because Set-StrictMode -Version Latest turns
# reading an absent property into a terminating error, and a great many uninstall keys carry no DisplayName
# (or no DisplayVersion) at all. `$_.DisplayName -eq ...` therefore throws on the first such key rather than
# simply not matching it.
function Get-InstalledVersion {
    param([Parameter(Mandatory)][string]$DisplayName)
    $roots = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    $entry = Get-ItemProperty -Path $roots -ErrorAction SilentlyContinue |
        Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -eq $DisplayName } |
        Select-Object -First 1

    if ($null -eq $entry -or -not $entry.PSObject.Properties['DisplayVersion']) { return $null }
    return $entry.DisplayVersion
}

# --------------------------------------------------------------------------------------------------
# Precondition, checked BEFORE anything is installed: the upgrade package must outrank the baseline. See
# Assert-VersionLadder for why this is a hard stop and not an assertion - it is the failure mode that lets a
# suite report green for an upgrade that never happened, and it is how this job first failed.
Write-Host "`n=== 0. Package preconditions ==="
$v1Version = Get-MsiProperty -Path $V1Msi -Name 'ProductVersion'
$v2Version = Get-MsiProperty -Path $V2Msi -Name 'ProductVersion'
Assert-VersionLadder -Rungs ([ordered]@{
        'v1 baseline' = $v1Version
        'v2 upgrade'  = $v2Version
    })

# --------------------------------------------------------------------------------------------------
# The wizard is never shown by this job - every msiexec call below is /quiet - so the upgrade UI gating is
# checked by reading the navigation graph back out of the package instead.
#
# The invariant: on an upgrade the operator must not be routed into the configuration pages. Those pages
# collect properties that WriteConfig / WriteAdvanced1-3 / SetDataDir consume, and all five are gated
# NOT WIX_UPGRADE_DETECTED - so an upgrade would demand the controller URL and the AGENT SECRET (blocking
# Next until both are filled in) and then discard both. Every edge INTO the config flow from outside it must
# therefore carry that same gate; edges between the config pages are the flow's own Back/Next and are
# unreachable once the entry points are gated.
#
# Asserting the routes exist at all is load-bearing too: this fragment is pulled in by a UIRef, and when that
# reference was missing the linker dropped the whole thing and shipped an MSI with no custom pages.
Write-Host "`n=== 0b. Upgrade skips the configuration pages ==="
$configDialogs = @('JenkinsConfigDlg', 'SecurityOptionsDlg', 'AdvancedOptionsDlg')
# @() so an empty result is an empty array rather than $null: Set-StrictMode turns .Count on $null into a
# terminating error, which would abort the run instead of failing the assertion it is meant to fail.
$intoConfigFlow = @(Get-MsiControlEvent -Path $V2Msi |
    Where-Object { $_.Event -eq 'NewDialog' -and $_.Argument -in $configDialogs -and $_.Dialog -notin $configDialogs })

Assert-That ($intoConfigFlow.Count -gt 0) `
    "the custom configuration pages are present in the package (the UIRef still pulls the fragment in)"
foreach ($edge in $intoConfigFlow) {
    Assert-That ($edge.Condition -match 'NOT\s+WIX_UPGRADE_DETECTED') `
        "$($edge.Dialog)/$($edge.Control) -> $($edge.Argument) is gated off on upgrade (condition: '$($edge.Condition)')"
}

# --------------------------------------------------------------------------------------------------
Write-Host "`n=== 1. Fresh install ($v1Version) ==="
Invoke-Msi -LogName 'install-v1' -Arguments (@('/i', $V1Msi) + $installProperties + 'JENKINS_AGENT_NAME=ci-node')

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
Assert-That ($null -ne $service) "service '$serviceName' is registered"
Assert-That (Test-Path $configPath) "appsettings.json written to the install folder"
Assert-That (Test-Path $dataFolder) "data folder created at $dataFolder"

$cfg = Get-InstalledConfig -Path $configPath
Assert-That ($cfg.Jenkins.Connection.Url -eq 'https://127.0.0.1:59999') "Connection:Url persisted from the property"
Assert-That ($cfg.Jenkins.Connection.AgentName -eq 'ci-node') "Connection:AgentName persisted from the property"
Assert-That ($cfg.Jenkins.Secret.Value -eq 'ci-smoke-secret') "Secret:Value persisted"
Assert-That ($null -ne $cfg.Telemetry) "the Telemetry section is emitted on a fresh install"
Assert-That ($cfg.Jenkins.Agent.DataDirectory -eq $dataFolder) "Agent:DataDirectory points at DATAFOLDER"

# The service is expected to be running even though the controller is unreachable: bring-up retries
# forever rather than faulting. A stopped service here means startup itself broke - exactly the class of
# bug (wrong config path, DI failure) that no unit test can see.
$service.Refresh()
Assert-That ($service.Status -eq 'Running') "service is Running after install (bring-up retries, it must not exit)"

# The locations are recorded so the next package can recover them. Without this the upgrade below would reset
# INSTALLFOLDER and DATAFOLDER to their defaults - and since appsettings.json is written by a custom action
# rather than installed as a tracked file, a relocated install folder would strand the only copy of the
# secret at the old path. DATAFOLDER here is deliberately NOT the default, so a value that merely looks
# plausible cannot pass.
Assert-That ((Get-RecordedLocation -Platform 'x64' -Name 'InstallPath') -eq "$installFolder\") `
    "install location recorded at $locationKey\InstallPath"
Assert-That ((Get-RecordedLocation -Platform 'x64' -Name 'DataPath') -eq "$dataFolder\") `
    "data location recorded at $locationKey\DataPath (the non-default DATAFOLDER, not the default)"
# Which key holds the paths is how a later package tells a same-arch upgrade from a cross-arch migration, so
# an x64 install writing anything under the x86 key would break that distinction in the quietest way possible.
Assert-That (-not (Test-Path $otherArchKey)) `
    "the x64 install recorded itself under the x64 key only - nothing under $otherArchKey"

# --------------------------------------------------------------------------------------------------
Write-Host "`n=== 2. Operator edits the config, then upgrades to $v2Version ==="

# (a) an edited in-schema value that must survive; (b) a schema key removed, standing in for a config
# written by an older version, which the reconcile must re-add at its default; (c) an unknown key that
# must be pruned.
$raw = Get-Content $configPath -Raw | ConvertFrom-Json
$raw.Jenkins.Logging.RetainedLogs = 9
$raw.Jenkins.Connection.PSObject.Properties.Remove('ControllerCertThumbprint')
$raw.Jenkins.Logging | Add-Member -NotePropertyName 'NoSuchSetting' -NotePropertyValue 'remove-me'
$raw | ConvertTo-Json -Depth 8 | Set-Content $configPath -Encoding utf8

# Uninstall now purges the data folder, and a major upgrade removes the OLD product first
# (MajorUpgrade Schedule="afterInstallInitialize") - so if the purge is not correctly gated on
# NOT UPGRADINGPRODUCTCODE, an upgrade silently destroys the operator's logs and the cached agent.jar.
# This file is planted before the upgrade and checked after it.
$sentinel = Join-Path $dataFolder 'operator-data.txt'
Set-Content -Path $sentinel -Value 'operator data that must survive an upgrade and die on uninstall' -Encoding utf8

# NO properties. An upgrade must not need to be told the URL, the data folder or - above all - the agent
# secret: the wizard no longer asks for them, so a scripted upgrade must not have to supply them either.
# This is also the stronger assertion. Passing the same values on both runs made a preserved value
# indistinguishable from a re-written one; withholding them means every check below can only pass if
# UpgradeConfig really did reconcile the config that was already on disk.
Invoke-Msi -LogName 'upgrade-v2' -Arguments @('/i', $V2Msi)

$cfg = Get-InstalledConfig -Path $configPath
Assert-That ($null -ne $cfg) "appsettings.json still exists after the upgrade"
Assert-That ($cfg.Jenkins.Logging.RetainedLogs -eq 9) "an operator's edited value survives the upgrade"
Assert-That ($cfg.Jenkins.Connection.AgentName -eq 'ci-node') "an unrelated value survives the upgrade"
Assert-That ($cfg.Jenkins.Connection.Url -eq 'https://127.0.0.1:59999') `
    "Connection:Url survives an upgrade that was never given JENKINS_URL"
Assert-That ($cfg.Jenkins.Agent.DataDirectory -eq $dataFolder) `
    "Agent:DataDirectory survives an upgrade that was never given DATAFOLDER"
Assert-That ($cfg.Jenkins.Secret.Value -eq 'ci-smoke-secret') `
    "the secret survives an upgrade that was never given JENKINS_SECRET"
Assert-That ($null -ne $cfg.Jenkins.Connection.PSObject.Properties['ControllerCertThumbprint']) `
    "a schema key missing from the old config is re-added by the reconcile"
Assert-That ($null -eq $cfg.Jenkins.Logging.PSObject.Properties['NoSuchSetting']) `
    "a key the schema does not define is pruned"

Assert-That (Test-Path $sentinel) "the data folder SURVIVES an upgrade (the purge must be gated on NOT UPGRADINGPRODUCTCODE)"

# The upgrade was given no DATAFOLDER, so it had to recover the recorded one. If it fell back to the default
# instead, the DataFolderAcl component would create and ACL a stray %ProgramData%\JenkinsAsService - granting
# the service account write on a folder nothing uses, which uninstall's purge (it reads DataDirectory out of
# the config) would then leave behind for good. Nothing here should ever create the default path.
$strayDataFolder = Join-Path $env:ProgramData 'JenkinsAsService'
Assert-That (-not (Test-Path $strayDataFolder)) `
    "no stray default data folder at $strayDataFolder - the recorded DATAFOLDER was recovered"
Assert-That ((Get-RecordedLocation -Platform 'x64' -Name 'InstallPath') -eq "$installFolder\") `
    "the recorded install location survives the upgrade and still points at the real folder"
Assert-That ((Get-RecordedLocation -Platform 'x64' -Name 'DataPath') -eq "$dataFolder\") `
    "the recorded data location survives the upgrade"

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
Assert-That ($null -ne $service -and $service.Status -eq 'Running') "service is Running after the upgrade"

# Unconditional: an absent product is a failure, not a reason to skip the check. The previous `if ($installed)`
# guard meant a lookup that found nothing silently reported success.
$installedVersion = Get-InstalledVersion -DisplayName $productName
Assert-That ($installedVersion -eq $v2Version) `
    "installed product version is $v2Version (in-place upgrade, not side-by-side) - found '$installedVersion'"

# --------------------------------------------------------------------------------------------------
Write-Host "`n=== 3. Uninstall ==="

# The sentinel planted before the upgrade is still there, and now has to disappear: uninstall removes BOTH
# trees. The service is running and holds its own agent.log open, so the data folder is deliberately probed
# with an inert file - a handle the service happens to hold would otherwise confuse the result.
Assert-That (Test-Path $sentinel) "sentinel is present going into the uninstall (guards the assertion below)"

Invoke-Msi -LogName 'uninstall' -Arguments @('/x', $V2Msi)

Assert-That ($null -eq (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) "service is removed"
Assert-That (-not (Test-Path $configPath)) "appsettings.json is removed from the install folder"
Assert-That (-not (Test-Path $installFolder)) "the install folder is removed"
Assert-That (-not (Test-Path $dataFolder)) "the data folder is removed, with the logs, agent.jar and work tree"

# The location key is a tracked component, so a genuine uninstall takes it with everything else. Leaving it
# would point the next fresh install at a folder that no longer exists.
Assert-That ($null -eq (Get-RecordedLocation -Platform 'x64' -Name 'InstallPath')) `
    "the recorded install location is removed - a later fresh install must not inherit a dead path"

# --------------------------------------------------------------------------------------------------
Complete-AssertionReport -Subject 'MSI lifecycle'
