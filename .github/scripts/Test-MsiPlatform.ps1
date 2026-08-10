<#
.SYNOPSIS
    Asserts that each MSI targets the architecture its filename and build claim.

.DESCRIPTION
    Guards a defect that shipped unnoticed in every release: both workflows build the installer project twice
    from one directory, varying only MSBuild PROPERTIES (PublishDir, InstallerPlatform, Version). The
    up-to-date check does not track properties, so the second build finished in under a second and copied the
    FIRST package to the new output directory - the published x86 installer was a byte-identical x64 package
    and could not install on a 32-bit machine.

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
    [Parameter(Mandatory)][string]$X86Msi
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'MsiQuery.psm1') -Force

# The expected platform token in each package's summary Template. Get-MsiPlatformToken owns the translation
# (32-bit packages say "Intel" - the Windows Installer token for x86, not a vendor name), so this states only
# which package is meant to be which architecture.
$expectedPlatforms = [ordered]@{
    $X64Msi = Get-MsiPlatformToken -Platform 'x64'
    $X86Msi = Get-MsiPlatformToken -Platform 'x86'
}

$failures = @()
foreach ($entry in $expectedPlatforms.GetEnumerator()) {
    $actual = Get-MsiPlatform -Path $entry.Key
    $name = Split-Path $entry.Key -Leaf
    Write-Host "  $name -> platform '$actual' (expected '$($entry.Value)')"
    if ($actual -ne $entry.Value) {
        $failures += "$name reports platform '$actual', expected '$($entry.Value)'"
    }
}

# Identical bytes is the signature of the copy-instead-of-build defect, and catches it even if both packages
# somehow carried a plausible platform token.
if ((Get-FileHash $X64Msi).Hash -eq (Get-FileHash $X86Msi).Hash) {
    $failures += 'The x64 and x86 packages are byte-identical - one was copied rather than built.'
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "::error::$_" }
    throw "MSI architecture verification failed ($($failures.Count) problem(s))."
}

Write-Host 'Both packages target their own architecture.'
