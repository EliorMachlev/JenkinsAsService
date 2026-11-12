# Exceptions to "No Suppressions"

A finding from a scanner, linter or update bot is fixed at its cause, not hidden. This file is the **complete list** of places where the project hides one anyway, and why. If a suppression, ignore entry, allow-list entry or dismissal is not listed here, it is not allowed: add it here in the same change, or remove it.

## What qualifies

An exception is for a finding that is wrong about code (or a dependency) that is right **as it is used here**:

- the code or dependency is correct as used, and
- no change would satisfy the tool short of removing something that works, and
- the maintainer has decided to exclude it rather than leave it visible.

A finding that is merely hard, slow or unpleasant to fix does **not** qualify. Fix it, or leave the warning visible and say so in the PR.

Every exclusion is the narrowest one available: one rule or advisory id, where it occurs. Never a whole tool, rule or directory switched off when a per-id, per-file entry is possible. Each exception keeps its number for good; a removed one's number is retired, not reused.

## Current exceptions

| # | Tool and rule / advisory | Excluded for | Marked in | Review by | Tracking |
|---|---|---|---|---|---|
| [1](#1-semgreps-pinned-mcp-and-opentelemetry-advisories) | dependency-review: GHSA-84m7-p3x7-pcfv, GHSA-w4fh-qvv9-3v23, GHSA-5h93-6whr-6q8j, GHSA-rwrf-2pqf-9j8j, GHSA-xcq7-p3pf-p88h. Trivy: CVE-2026-59951, CVE-2026-116684, CVE-2026-116685, CVE-2026-116686, CVE-2026-114603 | `mcp` 1.29.0 and `opentelemetry-api` 1.37.0, pinned by Semgrep 1.185.0, in `.github/semgrep/requirements.txt` only | [`.github/workflows/dependency-review.yml`](.github/workflows/dependency-review.yml) (`allow-ghsas`), [`.trivyignore.yaml`](.trivyignore.yaml) (passed in by [`trivy-reusable.yml`](.github/workflows/trivy-reusable.yml)) | 2027-02-09 | [#111](https://github.com/EliorMachlev/JenkinsAsService/issues/111) |
| [2](#2-semgrep-weak-hash-on-the-service-sid-derivation) | Semgrep `csharp.lang.security.weak-hash.weak-hash` | `SHA1.HashData` in `TpmSecretProtector.ComputeServiceSid` | `nosemgrep` comment, [`src/JenkinsAsService/TpmSecretProtector.cs`](src/JenkinsAsService/TpmSecretProtector.cs) | permanent (OS-defined algorithm) | none |
| [3](#3-semgrep-unsafe-path-combine-on-secretwriterwriteroot) | Semgrep `csharp.lang.security.filesystem.unsafe-path-combine.unsafe-path-combine` | `File.WriteAllText` in `SecretWriter.WriteRoot` | `nosemgrep` comment, [`src/JenkinsAsService/SecretWriter.cs`](src/JenkinsAsService/SecretWriter.cs) | next change to `SecretWriter` | none |
| [4](#4-wix-ice80-on-the-x86-package) | WiX / Windows Installer validation `ICE80` | the x86 MSI build only | `<SuppressIces>` in [`src/JenkinsAsService.Installer/JenkinsAsService.Installer.wixproj`](src/JenkinsAsService.Installer/JenkinsAsService.Installer.wixproj) | permanent (by design) | none |
| [5](#5-psscriptanalyzer-psavoidusingwritehost-in-the-ci-scripts) | PSScriptAnalyzer `PSAvoidUsingWriteHost` | 7 CI scripts under `.github/scripts` | `[Diagnostics.CodeAnalysis.SuppressMessageAttribute(...)]` at the top of each | permanent (CI transcript) | none |
| [6](#6-sonar-nosonar-markers) | Sonar (Codacy's SonarSharp) via `// NOSONAR` | 3 `using System.Security.Cryptography` lines, 1 test URL constant | `SecretWriter.cs`, `SecretResolverTests.cs`, `SecretWriterTests.cs`, `HttpJarDownloaderTests.cs` | when Codacy is removed | none |
| [7](#7-dependabot-wix-major-versions) | Dependabot `ignore` | `WixToolset.*` major versions | [`.github/dependabot.yml`](.github/dependabot.yml) (nuget) | owner's licensing decision | none |
| [8](#8-dependabot-net-sdk-major-versions) | Dependabot `ignore` | .NET SDK major versions in `global.json` | `.github/dependabot.yml` (dotnet-sdk) | next target-framework change | none |
| [9](#9-historical-dismissals-on-github) | GitHub code scanning: 118 dismissed alerts (Codacy PSScriptAnalyzer / Remark-lint, DevSkim) | the removed `JenkinsAsService.ps1` script, `README.md`, `.github/ISSUE_TEMPLATE/bug_report.md` | Dismissed on GitHub's Security tab | none (tools no longer run) | none |

### 1. Semgrep's pinned `mcp` and `opentelemetry` advisories

- **What the tools say:** `.github/semgrep/requirements.txt` (Semgrep and its whole tree, hash-pinned for `semgrep.yml`) contains five advisories: four in `mcp` 1.29.0, the Python MCP SDK (fixed in 1.30.0 / 2.2.0), and one in `opentelemetry-api` 1.37.0 (fixed in 1.45.1). dependency-review fails a PR on all five; Trivy reports all five to code scanning.

  | Advisory | CVE | What it is |
  |---|---|---|
  | GHSA-84m7-p3x7-pcfv | CVE-2026-59951 | MCP **server**: unbounded session accumulation in the streamable-HTTP transport |
  | GHSA-w4fh-qvv9-3v23 | CVE-2026-116684 | MCP **server**: bearer-token authentication accepts tokens for another resource server |
  | GHSA-5h93-6whr-6q8j | CVE-2026-116685 | MCP **client**: HTTP transports follow cross-origin redirects carrying custom headers and bodies |
  | GHSA-rwrf-2pqf-9j8j | CVE-2026-116686 | MCP **client**: fetches server-chosen `$ref` URLs while validating tool results |
  | GHSA-xcq7-p3pf-p88h | CVE-2026-114603 | OpenTelemetry: unbounded `tracestate` header parsing |

- **Why it can't be fixed:** both packages come in only as requirements of Semgrep, and Semgrep 1.185.0 (its newest release) declares `mcp==1.29.0` and `opentelemetry-api~=1.37.0`. `pip-compile` cannot resolve a newer one, Dependabot cannot propose one, and forcing one past the pin would run Semgrep on versions it does not declare support for. The previous `semgrep/semgrep` container shipped the same packages; the hash-pinned install only made them visible.
- **Why it's acceptable here:** Semgrep runs only in the ephemeral `semgrep.yml` CI job, on this repository's code; nothing the project ships contains it. The job runs `semgrep scan` and nothing else: it never starts an MCP server, never opens an MCP client session, and never parses an inbound `tracestate` header. The Semgrep vendor's own assessment, given in reply to a report about these pins, agrees for every user: the two client advisories do not apply because Semgrep runs only an MCP server, never a client; the two server advisories affect only its optional streamable-HTTP MCP server, which listens on localhost only; the OpenTelemetry one needs inbound `tracestate` headers, which Semgrep never parses. The vendor has said it will update `mcp` and the OpenTelemetry packages within 90 days of 2026-11-11.
- **What exactly is excluded:**
  - dependency-review: `allow-ghsas:` for exactly the five GHSAs above (the OpenTelemetry one was added after dependency-review started flagging it on the second run). Every other advisory, in every manifest, still fails the check.
  - Trivy: five entries in `.trivyignore.yaml`, one per CVE, each restricted to `paths: [.github/semgrep/requirements.txt]`, each with a `statement` pointing here and at the issue, and each with `expired_at: 2027-02-09`: after that date Trivy stops honouring it and the alert is reported again. The same CVE in any other file, and every other CVE in this one, is still reported.
  - Dependabot's `pip` ecosystem for `/.github/semgrep` stays on, so the Semgrep release that raises the pins is proposed automatically.
- **When it goes:** when a Semgrep release raises the pins and Dependabot's grouped `semgrep` bump is merged; then remove the `allow-ghsas` entries, the five `.trivyignore.yaml` entries, and this row and section. Checklist: issue [#111](https://github.com/EliorMachlev/JenkinsAsService/issues/111). Review by 2027-02-09 at the latest, when the Trivy entries expire on their own.

### 2. Semgrep: weak-hash on the service SID derivation

- **What the rule says:** SHA-1 is a weak hash.
- **Why it's acceptable:** `TpmSecretProtector.ComputeServiceSid` computes a Windows **service SID** (`S-1-5-80-…`), whose definition is five little-endian `uint32`s of SHA-1 over the upper-cased service name in UTF-16LE. The algorithm is fixed by Windows; SHA-1 is not used as a security hash here, and any other hash would produce a SID that does not exist.
- **What exactly is excluded:** that one call, with a `nosemgrep` comment naming the rule. Any other SHA-1 or MD5 use in the code is still reported.
- **When it goes:** never, unless the SID is obtained from the OS instead of computed.

### 3. Semgrep: unsafe-path-combine on `SecretWriter.WriteRoot`

- **What the rule says:** a method parameter reaches `File.Write*` through `Path.Combine` without `Path.GetFileName` sanitization.
- **Why it's acceptable:** the path is `basePath` (always `AppContext.BaseDirectory` in production) joined with the constant `appsettings.json`; no operator or attacker value reaches it.
- **What exactly is excluded:** one `nosemgrep` comment naming the rule, on that one `File.WriteAllText` call.
- **Note:** since every `SecretWriter` config path is built by `SecretWriter.ConfigPath` with `Path.Join` (PR #110), the rule (which needs a `Path.Combine` in a public method) no longer matches this line, so the marker is very likely redundant. It is listed rather than removed so the removal is a deliberate change of its own: delete the comment, confirm Semgrep stays at zero findings, then delete this entry.

### 4. WiX: ICE80 on the x86 package

- **What the rule says:** ICE80 fires when a 32-bit package uses a 64-bit registry locator (or installs 64-bit components).
- **Why it's acceptable:** the x86 package reads the 64-bit registry view **on purpose**: `InstallLocation.wxs` reads both views so an x86 install can detect, and be blocked from silently replacing, an existing x64 or arm64 install. Not reading it is exactly the bug the search exists to prevent.
- **What exactly is excluded:** `ICE80` only, and only when `InstallerPlatform` is `x86`. The x64 and arm64 packages keep the full ICE suite, and the genuine failure ICE80 also covers (a 32-bit package installing 64-bit components) still fails those builds.
- **When it goes:** never, while the cross-architecture guard exists.

### 5. PSScriptAnalyzer: PSAvoidUsingWriteHost in the CI scripts

- **What the rule says:** `Write-Host` output cannot be captured or suppressed.
- **Why it's acceptable:** these scripts are CI suites and tools whose output is the job transcript. `Write-Output` would put the messages into the scripts' return values, and `Write-Verbose`/`Write-Information` are hidden by default: the opposite of what a scan or test log needs.
- **What exactly is excluded:** that one rule, script-wide, with the justification "Intentional CI transcript output", in: `Invoke-ScriptAnalyzerScan.ps1`, `MsiTestHelpers.psm1`, `New-WingetManifest.ps1`, `Test-Bundle.ps1`, `Test-MsiArchMigration.ps1`, `Test-MsiLifecycle.ps1`, `Test-MsiPlatform.ps1`. Every other rule still runs on them (`powershell.yml` uses the full rule set).
- **When it goes:** never, while these remain transcript-producing CI scripts.

### 6. Sonar: `NOSONAR` markers

- **What the tool says:** Codacy's SonarSharp analysis, run standalone, cannot resolve `System.Security.Cryptography.ProtectedData` (a NuGet package, not part of the shared framework) and reports the `using System.Security.Cryptography;` line; separately it flagged the test URL constant `https://jenkins:8443` (the marker carries no rule id; a hard-coded-URI rule is the likely one).
- **Why it's acceptable:** the build resolves the namespace (it compiles with 0 warnings); and the URL is a fixed test fixture for `HttpJarDownloaderTests`, never contacted.
- **What exactly is excluded:** `// NOSONAR` on `using System.Security.Cryptography;` in `src/JenkinsAsService/SecretWriter.cs`, `tests/JenkinsAsService.Tests/SecretResolverTests.cs` and `tests/JenkinsAsService.Tests/SecretWriterTests.cs`, and on the `JenkinsUrl` constant in `tests/JenkinsAsService.Tests/HttpJarDownloaderTests.cs`. They affect Sonar only; Semgrep and CodeQL ignore them.
- **When it goes:** when the Codacy GitHub App integration is removed (the in-repo Codacy workflow already is).

### 7. Dependabot: WiX major versions

- **What it would propose:** WiX Toolset v6 / v7 (`WixToolset.*`).
- **Why it's acceptable:** v6 and v7 binaries are distributed under the Open Source Maintenance Fee EULA (v7 also asks for the acceptance at build time). Moving past v5 is the owner's licensing decision, not a bot merge, for the same reason RAR was dropped from releases.
- **What exactly is excluded:** `version-update:semver-major` for `WixToolset.*` only; WiX 5.x patch and minor updates are still proposed (grouped as `wix`), and security updates are not affected.
- **When it goes:** if the owner decides to accept the WiX EULA.

### 8. Dependabot: .NET SDK major versions

- **What it would propose:** a new major .NET SDK in `global.json`.
- **Why it's acceptable:** the SDK major moves with the target framework (`net10.0-windows`, LTS), which is a deliberate change of its own, and every SDK bump needs the lock files regenerated with it.
- **What exactly is excluded:** `version-update:semver-major` for the `dotnet-sdk` ecosystem; patch and feature-band updates are still proposed.
- **When it goes:** never as such; the target-framework change bumps the SDK by hand.

### 9. Historical dismissals on GitHub

- **What they are:** 118 code-scanning alerts dismissed on the Security tab before this file existed: Codacy-reported PSScriptAnalyzer (96, mostly trailing whitespace and global variables) and DevSkim (11) on the old `JenkinsAsService.ps1` script, and Codacy-reported Remark-lint (11) on `README.md` and `.github/ISSUE_TEMPLATE/bug_report.md`.
- **Why they're listed:** for completeness. The script they mostly point at no longer exists (the service is the .NET 10 executable), DevSkim was removed, and the in-repo Codacy workflow was removed; no current scan reports them.
- **What exactly is excluded:** nothing current; a dismissal does not hide a new alert. Check the Security tab against this entry when reviewing.

### Decided

1 decided by the maintainer on 2026-11-11 (PR [#110](https://github.com/EliorMachlev/JenkinsAsService/pull/110), issue [#111](https://github.com/EliorMachlev/JenkinsAsService/issues/111)). 2 to 9 predate this file and were recorded here on 2026-11-11 without change.

## Not exceptions

- **xunit v3:** `xunit` stays on 2.9.3, the newest release of the v2 package id. v3 is a different package and a framework migration, deferred by decision (see `CLAUDE.md`); there is no Dependabot ignore entry for it, so nothing is hidden.

## Adding an exception

1. Establish that there is no real fix, and what the next-best outcome would cost.
2. Get the maintainer's decision. An exception is theirs to grant, not the author's.
3. Exclude the narrowest thing possible: the one finding, by rule or advisory id, where it occurs, with an expiry where the tool supports one. Never a rule switched off for the project, a file or directory left out of a scan, or a baseline.
4. Add a row and a section above under the next unused number: what the tool says, why it is acceptable, what exactly is excluded, when it goes. Point the marker itself (comment, `statement`) at the number.

## Removing an exception

When the tool is fixed upstream, or the code changes so the finding no longer applies, delete the marker and this row and section in the same change. The number is retired.
