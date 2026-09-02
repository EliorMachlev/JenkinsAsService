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

Complete-AssertionReport -Subject 'MSI architecture'
