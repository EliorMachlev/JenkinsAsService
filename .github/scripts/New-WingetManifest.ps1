<#
.SYNOPSIS
    Emits the three winget manifest files for a release.

.DESCRIPTION
    winget requires a multi-file manifest (version + installer + locale), and every field that changes per
    release - the version, the download URL and the installer's SHA256 - has to agree across them. Generating
    them from one place is the only way that stays true; a hand-edited manifest with a stale hash is rejected
    by the community-repo pipeline long after the release has shipped.

    The manifest points at the BUNDLE, not the two MSIs, and lists it under BOTH architectures with the same
    URL. That is deliberate. winget picks an installer by architecture, and the bundle is the component that
    knows how to choose - it installs the machine's native architecture and migrates an install of the other
    one. Listing the MSIs directly would move that decision into winget, which would then offer an x64
    package to a machine running the x86 install and produce a failed upgrade rather than a migration.

    The output is not submitted anywhere. It is attached to the release so the winget-pkgs pull request can be
    opened from a known-good, hash-correct starting point.
#>
[CmdletBinding()]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute(
    'PSAvoidUsingWriteHost', '', Justification = 'Intentional CI transcript output')]
param(
    [Parameter(Mandatory)][string]$Version,
    # The bundle .exe being released - hashed here rather than passed in, so the manifest cannot disagree
    # with the artifact.
    [Parameter(Mandatory)][string]$BundlePath,
    [Parameter(Mandatory)][string]$InstallerUrl,
    [Parameter(Mandatory)][string]$OutputDirectory,
    # The bundle's UpgradeCode, which is how winget matches an installed Burn bundle in ARP. Authored in
    # Bundle.wxs; defaulted here rather than hardcoded inline so a rotation has one obvious place to land and
    # a caller can pass the built bundle's own value. If the two ever disagree, winget stops recognising the
    # installed package and every upgrade fails on a user's machine with nothing failing in CI.
    [string]$BundleUpgradeCode = '{015B49BD-10B0-4FC7-802B-A248BD50A205}'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$packageId = 'EliorMachlev.JenkinsAsService'
# Pinned rather than tracking the newest: a schema bump can add required fields, and finding that out during
# a release is the wrong time.
$manifestVersion = '1.6.0'

$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $BundlePath).Hash.ToUpper()
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

function Write-Manifest {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Content)
    $path = Join-Path $OutputDirectory $Name
    # UTF-8 with BOM is what the winget schema expects for manifest files.
    Set-Content -LiteralPath $path -Value $Content -Encoding utf8BOM
    Write-Host "  wrote $path"
}

Write-Manifest -Name "$packageId.yaml" -Content @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.$manifestVersion.schema.json
PackageIdentifier: $packageId
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $manifestVersion
"@

# InstallerType: burn - a WiX bundle, which winget understands natively (it knows the -quiet/-norestart and
# -uninstall switches, and that ARP registration is the bundle's UpgradeCode rather than an MSI ProductCode).
# The same file is listed for both architectures because the bundle selects internally; see the note above.
Write-Manifest -Name "$packageId.installer.yaml" -Content @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.$manifestVersion.schema.json
PackageIdentifier: $packageId
PackageVersion: $Version
InstallerType: burn
Scope: machine
InstallModes:
  - interactive
  - silent
  - silentWithProgress
UpgradeBehavior: install
ProductCode: '$BundleUpgradeCode'
ReleaseDate: $(Get-Date -Format 'yyyy-MM-dd')
Installers:
  - Architecture: x64
    InstallerUrl: $InstallerUrl
    InstallerSha256: $hash
  - Architecture: x86
    InstallerUrl: $InstallerUrl
    InstallerSha256: $hash
ManifestType: installer
ManifestVersion: $manifestVersion
"@

Write-Manifest -Name "$packageId.locale.en-US.yaml" -Content @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.$manifestVersion.schema.json
PackageIdentifier: $packageId
PackageVersion: $Version
PackageLocale: en-US
Publisher: EliorMachlev
PublisherUrl: https://github.com/EliorMachlev
PublisherSupportUrl: https://github.com/EliorMachlev/JenkinsAsService/issues
PackageName: Jenkins Agent Service
PackageUrl: https://github.com/EliorMachlev/JenkinsAsService
License: BSD-3-Clause
LicenseUrl: https://github.com/EliorMachlev/JenkinsAsService/blob/main/LICENSE
Copyright: Copyright (c) EliorMachlev
ShortDescription: Runs a Jenkins inbound (JNLP) agent as a hardened native Windows Service.
Description: |-
  JenkinsAsService runs a Jenkins inbound (JNLP) agent as a native Windows Service - no login session, no
  scheduled task, no manual restarts. It validates its configuration, resolves Java, tests connectivity,
  downloads and integrity-checks agent.jar, then supervises the agent with an event-driven watchdog that
  recovers automatically from crashes.

  The agent secret is protected at rest (TPM, DPAPI, Credential Manager or environment variable), kept off
  the process table and redacted from logs. The service runs under a least-privilege virtual account, and
  the install folder stays non-writable by the agent identity.
Moniker: jenkinsasservice
Tags:
  - jenkins
  - ci
  - agent
  - windows-service
  - devops
ReleaseNotesUrl: https://github.com/EliorMachlev/JenkinsAsService/releases/tag/v$Version
Documentations:
  - DocumentLabel: Documentation
    DocumentUrl: https://jenkinsasservice.machlev.org
ManifestType: defaultLocale
ManifestVersion: $manifestVersion
"@

Write-Host "winget manifests for $packageId $Version written to $OutputDirectory"
