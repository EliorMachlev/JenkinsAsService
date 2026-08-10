<#
.SYNOPSIS
    Shared scaffolding for the MSI/bundle end-to-end suites.

.DESCRIPTION
    Test-MsiLifecycle, Test-MsiArchMigration and Test-Bundle all install a real package on a real runner and
    then assert against the same three things: the PASS/FAIL transcript, the location keys the package records,
    and the config it writes. Each of them grew its own copy of that scaffolding, and the copies had already
    started to disagree - two different Get-RecordedLocation signatures, three different wrappers around
    Start-Process, and three near-identical failure epilogues.

    The registry layout in particular is load-bearing: WHICH key holds a path is the package's statement of
    which architecture is installed (see InstallLocation.wxs). Written out per-script, a renamed key makes
    Get-InstalledPlatform return $null everywhere - and an assertion of the form "the other arch key is
    absent" then passes harder than before. It is stated once, here.

    Write-Host throughout is deliberate: these produce a human-readable CI transcript, not a pipeline. The
    alternatives are wrong - Write-Output would pollute the return value of the functions it is called from,
    and Write-Verbose/Information are hidden by default, which is the opposite of what a test log needs.
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute(
    'PSAvoidUsingWriteHost', '', Justification = 'Intentional CI transcript output')]
param()

Set-StrictMode -Version Latest

# Where each architecture records the locations it installed to. An x86 install is additionally redirected
# into WOW6432Node, since its component inherits the package's 32-bit-ness - so the arch is stated twice over.
$script:LocationKeys = [ordered]@{
    x64 = 'HKLM:\SOFTWARE\JenkinsAsService\x64'
    x86 = 'HKLM:\SOFTWARE\WOW6432Node\JenkinsAsService\x86'
}

# 0 = success, 3010 = success but a reboot was requested. Neither is a failure for this package. Held here so
# "succeeded" is defined once rather than restated as a bare @(0, 3010) at every call site.
$script:SuccessExitCodes = @(0, 3010)

$script:Failures = @()

function Get-JasLocationKeyPath {
    <#
    .SYNOPSIS
        The registry path under which the given architecture records its install and data locations.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)][ValidateSet('x64', 'x86')][string]$Platform)
    return $script:LocationKeys[$Platform]
}

function Assert-That {
    <#
    .SYNOPSIS
        Records a PASS/FAIL against the module's failure list. Never throws - a suite runs to the end so one
        broken assertion does not hide the state of every later one.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if ($Condition) { Write-Host "  [PASS] $Message" }
    else {
        Write-Host "  [FAIL] $Message"
        $script:Failures += $Message
    }
}

function Complete-AssertionReport {
    <#
    .SYNOPSIS
        Reports the recorded failures and exits 1 if there were any. The last line of every suite.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Subject)
    Write-Host ''
    if ($script:Failures.Count -gt 0) {
        Write-Host "::error::$($script:Failures.Count) $Subject assertion(s) failed"
        $script:Failures | ForEach-Object { Write-Host "::error::  $_" }
        exit 1
    }
    Write-Host "All $Subject assertions passed."
}

function Test-InstallerSuccess {
    <#
    .SYNOPSIS
        Whether an installer exit code means success - 0, or 3010 for "success, reboot requested".
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param([Parameter(Mandatory)][int]$ExitCode)
    return $ExitCode -in $script:SuccessExitCodes
}

function Invoke-InstallerProcess {
    <#
    .SYNOPSIS
        Runs an installer to completion and returns its exit code, echoing the command line and the result.
    .DESCRIPTION
        Returns the code rather than throwing: one suite asserts on a NON-zero code as its main result, so a
        failure here is data. Callers that do want a failure to be fatal pass the code to
        Assert-InstallerSucceeded.
    #>
    [CmdletBinding()]
    [OutputType([int])]
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments
    )
    Write-Host "$FilePath $($Arguments -join ' ')"
    $process = Start-Process $FilePath -ArgumentList $Arguments -Wait -PassThru
    Write-Host "  exit code $($process.ExitCode)"
    return $process.ExitCode
}

function Invoke-Msiexec {
    <#
    .SYNOPSIS
        Invoke-InstallerProcess against msiexec, with the /quiet /norestart /l*v tail every call needs.
    #>
    [CmdletBinding()]
    [OutputType([int])]
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$LogPath
    )
    return Invoke-InstallerProcess -FilePath 'msiexec.exe' `
        -Arguments ($Arguments + @('/quiet', '/norestart', '/l*v', $LogPath))
}

function Assert-InstallerSucceeded {
    <#
    .SYNOPSIS
        Throws on a failing exit code, after tailing the log into the CI transcript.
    .DESCRIPTION
        The tail is the whole point: a bare "exit code 1603" in a CI log is unactionable, and the verbose log
        is an artifact nobody downloads for a run that failed on a typo. Fatal rather than an assertion -
        every later step in these suites presupposes the install actually happened.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$ExitCode,
        [Parameter(Mandatory)][string]$LogPath,
        [Parameter(Mandatory)][string]$Activity,
        [int]$TailLines = 40
    )
    if (Test-InstallerSuccess -ExitCode $ExitCode) { return }

    Write-Host "::error::$Activity failed with exit code $ExitCode - see artifact $(Split-Path -Leaf $LogPath)"
    if (Test-Path $LogPath) {
        Get-Content $LogPath -Tail $TailLines | ForEach-Object { Write-Host "    $_" }
    }
    throw "$Activity exit code $ExitCode"
}

function Get-RecordedLocation {
    <#
    .SYNOPSIS
        A recorded value from one architecture's key, or $null when that architecture is not the installed one.
    .DESCRIPTION
        Guarded through PSObject.Properties because Set-StrictMode -Version Latest turns reading an absent
        property into a terminating error rather than returning $null.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('x64', 'x86')][string]$Platform,
        [Parameter(Mandatory)][string]$Name
    )
    $key = Get-ItemProperty -Path $script:LocationKeys[$Platform] -ErrorAction SilentlyContinue
    if ($null -eq $key -or -not $key.PSObject.Properties[$Name]) { return $null }
    return $key.$Name
}

function Get-InstalledPlatform {
    <#
    .SYNOPSIS
        Which architecture the machine believes is installed - which is simply which key holds a path.
    .DESCRIPTION
        $null if neither does, and 'both' if somehow both do, so a broken state fails loudly rather than being
        silently reported as whichever one happened to be checked first.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param()
    $found = @($script:LocationKeys.Keys |
        Where-Object { $null -ne (Get-RecordedLocation -Platform $_ -Name 'InstallPath') })
    if ($found.Count -eq 0) { return $null }
    if ($found.Count -gt 1) { return 'both' }
    return $found[0]
}

function Get-InstalledConfig {
    <#
    .SYNOPSIS
        The installed appsettings.json as an object, or $null when it is not there.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path $Path)) { return $null }
    return Get-Content $Path -Raw | ConvertFrom-Json
}

function Get-PeMachine {
    <#
    .SYNOPSIS
        The architecture of a binary on disk, read out of its PE header.
    .DESCRIPTION
        The location key records what the package SAID it was; this is what it shipped. If those two ever
        disagree, every other assertion in these suites is measuring the wrong thing - which is not
        hypothetical here: a build-caching bug once shipped an x86 MSI that was a byte copy of the x64 one.
    #>
    [CmdletBinding()]
    [OutputType([string])]
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

Export-ModuleMember -Function Get-JasLocationKeyPath, Assert-That, Complete-AssertionReport,
    Test-InstallerSuccess, Invoke-InstallerProcess, Invoke-Msiexec, Assert-InstallerSucceeded,
    Get-RecordedLocation, Get-InstalledPlatform, Get-InstalledConfig, Get-PeMachine
