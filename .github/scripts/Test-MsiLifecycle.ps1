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

$installFolder = Join-Path $env:ProgramFiles 'Jenkins'
$dataFolder = Join-Path $env:ProgramData 'JenkinsAsServiceTest'
$configPath = Join-Path $installFolder 'appsettings.json'
$serviceName = 'Jenkins'
$productName = 'Jenkins Agent Service'
$logDir = Join-Path (Get-Location) 'msi-logs'
New-Item -ItemType Directory -Path $logDir -Force | Out-Null

# The install and the upgrade must be given the SAME properties: the point of the upgrade assertions is that
# the config survives, so any difference here would make a preserved value indistinguishable from a re-written
# one. Declared once for exactly that reason.
$commonProperties = @(
    "DATAFOLDER=$dataFolder",
    'JENKINS_URL=https://127.0.0.1:59999',
    'JENKINS_SECRET=ci-smoke-secret',
    'JENKINS_SECRET_MODE=Unprotected'
)

$script:Failures = @()

function Assert-That {
    param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if ($Condition) {
        Write-Host "  [PASS] $Message"
    }
    else {
        Write-Host "  [FAIL] $Message"
        $script:Failures += $Message
    }
}

function Invoke-Msi {
    param([Parameter(Mandatory)][string[]]$Arguments, [Parameter(Mandatory)][string]$LogName)
    $log = Join-Path $logDir "$LogName.log"
    $all = $Arguments + @('/quiet', '/norestart', '/l*v', $log)
    Write-Host "msiexec $($all -join ' ')"
    $p = Start-Process msiexec.exe -ArgumentList $all -Wait -PassThru
    # 3010 = success, reboot requested. Not an error for this package, but worth surfacing.
    if ($p.ExitCode -notin @(0, 3010)) {
        Write-Host "::error::msiexec failed with exit code $($p.ExitCode) - see artifact $LogName.log"
        Get-Content $log -Tail 40 | ForEach-Object { Write-Host "    $_" }
        throw "msiexec exit code $($p.ExitCode)"
    }
}

function Get-Config {
    if (-not (Test-Path $configPath)) { return $null }
    return Get-Content $configPath -Raw | ConvertFrom-Json
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
# Precondition, checked BEFORE anything is installed.
#
# If the two packages carry the same ProductVersion, msiexec treats the second /i as a maintenance-mode
# reconfigure of the product already on the machine: RemoveExistingProducts is skipped, the reconcile custom
# actions never run, and every downstream assertion still passes because nothing was touched. That is exactly
# how this job first failed - a build-caching bug made both packages 1.0.0 and the "upgrade" tested nothing.
# A suite that cannot tell "the upgrade worked" from "the upgrade never happened" is worse than no suite, so
# this is a hard stop rather than an assertion.
Write-Host "`n=== 0. Package preconditions ==="
$v1Version = Get-MsiProperty -Path $V1Msi -Name 'ProductVersion'
$v2Version = Get-MsiProperty -Path $V2Msi -Name 'ProductVersion'
Write-Host "  v1 package: $v1Version"
Write-Host "  v2 package: $v2Version"
if ($v1Version -eq $v2Version) {
    Write-Host "::error::Both MSIs are stamped $v1Version - the upgrade would be a no-op reconfigure, not an upgrade."
    throw "MSI ProductVersion must differ between the two packages (both are $v1Version)"
}

# --------------------------------------------------------------------------------------------------
Write-Host "`n=== 1. Fresh install ($v1Version) ==="
Invoke-Msi -LogName 'install-v1' -Arguments (@('/i', $V1Msi) + $commonProperties + 'JENKINS_AGENT_NAME=ci-node')

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
Assert-That ($null -ne $service) "service '$serviceName' is registered"
Assert-That (Test-Path $configPath) "appsettings.json written to the install folder"
Assert-That (Test-Path $dataFolder) "data folder created at $dataFolder"

$cfg = Get-Config
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

Invoke-Msi -LogName 'upgrade-v2' -Arguments (@('/i', $V2Msi) + $commonProperties)

$cfg = Get-Config
Assert-That ($null -ne $cfg) "appsettings.json still exists after the upgrade"
Assert-That ($cfg.Jenkins.Logging.RetainedLogs -eq 9) "an operator's edited value survives the upgrade"
Assert-That ($cfg.Jenkins.Connection.AgentName -eq 'ci-node') "an unrelated value survives the upgrade"
Assert-That ($cfg.Jenkins.Secret.Value -eq 'ci-smoke-secret') "the secret survives the upgrade"
Assert-That ($null -ne $cfg.Jenkins.Connection.PSObject.Properties['ControllerCertThumbprint']) `
    "a schema key missing from the old config is re-added by the reconcile"
Assert-That ($null -eq $cfg.Jenkins.Logging.PSObject.Properties['NoSuchSetting']) `
    "a key the schema does not define is pruned"

Assert-That (Test-Path $sentinel) "the data folder SURVIVES an upgrade (the purge must be gated on NOT UPGRADINGPRODUCTCODE)"

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

# --------------------------------------------------------------------------------------------------
Write-Host ''
if ($script:Failures.Count -gt 0) {
    Write-Host "::error::$($script:Failures.Count) MSI lifecycle assertion(s) failed"
    $script:Failures | ForEach-Object { Write-Host "::error::  $_" }
    exit 1
}

Write-Host "All MSI lifecycle assertions passed."
