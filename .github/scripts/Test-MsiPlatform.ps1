<#
.SYNOPSIS
    Asserts that each MSI targets the architecture its filename and build claim.

.DESCRIPTION
    Guards a defect that shipped unnoticed in every release: both workflows build the installer project
    repeatedly from one directory, varying only MSBuild PROPERTIES (PublishDir, InstallerPlatform, Version).
    The up-to-date check does not track properties, so the second build finished in under a second and copied
    the FIRST package to the new output directory - the published x86 installer was a byte-identical x64
    package and could not install on a 32-bit machine.

    With a third architecture the blind spot gets worse, not better: an arm64 package that was silently a copy
    of the x64 one would install and RUN on every ARM64 machine, under emulation, looking entirely healthy
    while delivering none of the native performance the package exists to provide. So the content check looks
    for a duplicate among ALL the packages rather than comparing one chosen pair.

    The build now uses a separate intermediate directory per package, which fixes the cause. This checks the
    result, because nothing ever did: a fix that leaves the blind spot intact lets the defect return the
    moment someone edits the build steps.
#>
[CmdletBinding()]
# Write-Host is deliberate, matching Test-MsiLifecycle.ps1: this is a human-readable CI transcript.
# Write-Output would pollute the pipeline, and Write-Verbose/Information are hidden by default - the
# opposite of what a check whose whole value is visibility needs.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute(
    'PSAvoidUsingWriteHost', '', Justification = 'Intentional CI transcript output')]
param(
    [Parameter(Mandatory)][string]$X64Msi,
    [Parameter(Mandatory)][string]$X86Msi,
    [Parameter(Mandatory)][string]$Arm64Msi
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'MsiQuery.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'MsiTestHelpers.psm1') -Force

# Which package is meant to be which architecture, and nothing more. The comparison is in x64/x86/arm64 terms
# rather than in raw summary tokens, so the facts that a 32-bit package spells itself "Intel" - the Windows
# Installer token for x86, not a vendor name - and that the ARM64 token is capitalised differently from the
# x64 one stay inside MsiQuery. A malformed Template still fails: Get-MsiArchitecture hands back the raw token
# when it normalises to no known architecture.
# Each package is carried through as ONE object - name, expected architecture, what it reports, and its
# content hash - so both checks below read a named field instead of two collections agreeing on an index.
#
# A list, never a hashtable keyed by path. Keying by path looks tidier and is a trap: pass the same file for
# two architectures - exactly what the copy-instead-of-build defect produces upstream, and exactly what a
# copy-pasted workflow argument produces - and the hashtable silently collapses to two entries, leaving the
# content check one fewer package to find a collision among. It would pass, which is the opposite of what it
# is for.
$packages = @(
    @{ Path = $X64Msi; Architecture = 'x64' }
    @{ Path = $X86Msi; Architecture = 'x86' }
    @{ Path = $Arm64Msi; Architecture = 'arm64' }
) | ForEach-Object {
    [pscustomobject]@{
        # Parent directory INCLUDED: both workflows build every package to the same file name
        # (JenkinsAsService.Installer.msi) in a per-architecture directory, so a bare leaf makes all three
        # print identically - and the collision message, whose entire job is to say WHICH two packages are
        # the same file, would name the same string twice.
        Name         = '{0}/{1}' -f (Split-Path (Split-Path $_.Path -Parent) -Leaf), (Split-Path $_.Path -Leaf)
        Architecture = $_.Architecture
        Path         = $_.Path
        Actual       = Get-MsiArchitecture -Path $_.Path
        Hash         = (Get-FileHash $_.Path).Hash
    }
}

foreach ($package in $packages) {
    Assert-That ($package.Actual -eq $package.Architecture) `
        "$($package.Name) targets $($package.Architecture) - it reports '$($package.Actual)'"
}

# Identical bytes is the signature of the copy-instead-of-build defect, and catches it even if the packages
# somehow carried plausible platform tokens. Grouping by hash rather than comparing chosen pairs: the defect
# copies whichever build ran first over whichever ran next, so WHICH packages collide depends on the order of
# the build steps - a hand-picked comparison would leave the outcome dependent on how the workflow happens to
# be written. A group of more than one is a collision whatever its members, and it names them.
$collisions = @($packages | Group-Object Hash | Where-Object { $_.Count -gt 1 })
$detail = ($collisions | ForEach-Object { ($_.Group.Name) -join ' == ' }) -join '; '

Assert-That ($collisions.Count -eq 0) `
    "all $($packages.Count) packages differ in content - none was copied rather than built$(if ($detail) { " (identical: $detail)" })"

# Custom-action ORDER, asserted statically because nothing else catches it cheaply. Both of these CAs must
# land strictly between InstallServices and StartServices, and both for the same reason: the default identity
# is the VIRTUAL account NT SERVICE\Jenkins, whose SID does not exist until ServiceInstall creates the
# service - so an ACL grant or a recovery-config call scheduled earlier cannot resolve the account and dies
# 1722 -> 1603. They must also precede StartServices, which is where SCM actually launches the service into
# the folder they have just made writable. Both bounds shipped wrong once: GrantDataAccess sat after
# InstallFiles, which links, packages and passes every static check that existed - and fails only against a
# real install, ten minutes into CI.
foreach ($package in $packages) {
    $sequence = @{}
    Get-MsiExecuteSequence -Path $package.Path | ForEach-Object { $sequence[$_.Action] = $_.Sequence }

    foreach ($action in 'GrantDataAccess', 'ConfigureRecovery') {
        Assert-That ($sequence.ContainsKey($action)) "$($package.Name) schedules $action at all"

        if ($sequence.ContainsKey($action)) {
            Assert-That ($sequence[$action] -gt $sequence['InstallServices']) `
                ("$($package.Name): $action ({0}) runs AFTER InstallServices ({1}) - the virtual service account does not exist before it" -f $sequence[$action], $sequence['InstallServices'])
            Assert-That ($sequence[$action] -lt $sequence['StartServices']) `
                ("$($package.Name): $action ({0}) runs BEFORE StartServices ({1}) - SCM launches the service there" -f $sequence[$action], $sequence['StartServices'])
        }
    }
}

# Every [PROPERTY] in a custom-action command line must be quoted. An empty property expands to nothing, so
# an unquoted one lets its flag swallow the NEXT flag as a value and the rest of the command line is re-read
# wrongly - one blank optional setting silently corrupting several. Quoted, an empty value arrives as "" and
# parses to null, meaning "not set", which is what it is.
# Checked on the built package rather than the source: the .wxs is preprocessed per architecture, and this is
# the string msiexec actually runs. Nothing else validates it - ICE03 caps Target at 255 characters and has
# no opinion on its content.
# Only the custom actions whose Target IS a command line. The low 6 bits of Type are the base type: 2 and 18
# are EXE from the Binary table / from an installed file, 34 and 50 the directory- and property-anchored EXE
# forms. Everything else stores something that is not a command line in the same column - type 51 sets a
# property (INSTALLFOLDER is recovered that way, and quoting there would put quotes IN the path), 19 is an
# error message, 35 sets a directory, 37/38 are script bodies.
$commandLineTypes = 2, 18, 34, 50

foreach ($package in $packages) {
    $bare = @(
        Get-MsiCustomAction -Path $package.Path |
            Where-Object { (([int]$_.Type) -band 0x3F) -in $commandLineTypes } |
            Where-Object { $_.Target -match '\[[A-Z_]+\]' } |
            ForEach-Object {
                # A property expansion counts as quoted when a double quote sits immediately either side.
                $unquoted = [regex]::Matches($_.Target, '(?<!")\[[A-Z_]+\](?!")')
                if ($unquoted.Count -gt 0) { '{0}: {1}' -f $_.Action, (($unquoted.Value) -join ', ') }
            }
    )

    Assert-That ($bare.Count -eq 0) `
        "$($package.Name): every custom-action property expansion is quoted$(if ($bare) { " (bare: $($bare -join '; '))" })"
}

Complete-AssertionReport -Subject 'MSI architecture'
