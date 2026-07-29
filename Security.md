# Security Policy

## Scope

This policy covers security issues in:

- **JenkinsAsService** Windows Service (the .NET executable)
- **JenkinsAsService.Installer** MSI package (WiX v5)
- **CI/CD workflows** in this repository (GitHub Actions)
- **Documentation** that could mislead users into insecure configurations

Out of scope:

- Jenkins itself (report to [Jenkins Security](https://www.jenkins.io/security/))
- The Java runtime or JDK (report to your JDK vendor)
- Third-party NuGet packages (report to the package maintainer; see [Dependencies](#dependencies) below)
- Windows OS or DPAPI/Credential Manager subsystems (report to Microsoft)
- **The interactive-session launch feature** (`Agent:LaunchInInteractiveSession`). This opt-in setting is a **documented, deliberate isolation downgrade** — it is off by default and, by design, requires `LocalSystem` (or a named account granted `SeTcbPrivilege`, `SeAssignPrimaryTokenPrivilege` and `SeIncreaseQuotaPrivilege`) plus an interactive logon, which inherently enable privilege escalation. Reduced isolation, privilege-escalation, or session-boundary reports that stem from **enabling this feature** are **not accepted** as vulnerabilities; they are the explicitly warned-about cost of turning it on. (A defect where the feature acts **without** being enabled, or fails to fall back safely, *is* in scope.)

## Supported Versions

Only the **latest release** receives security fixes. Older versions are not patched — upgrade to the latest release to stay protected.

| Version | Supported |
|---|---|
| Latest release | Yes |
| Older releases | No |

## Reporting a Vulnerability

**Do not open a public issue.** Security vulnerabilities must be reported privately.

### Preferred: GitHub Private Vulnerability Reporting

1. Go to the [Security Advisories](https://github.com/EliorMachlev/JenkinsAsService/security/advisories) tab
2. Click **"Report a vulnerability"**
3. Fill in the details and submit

GitHub will create a private advisory visible only to you and the maintainers.

### Alternative: Email

If you prefer email, contact the maintainer directly. You can find contact information on the [GitHub profile](https://github.com/EliorMachlev).

### What to Include

A good report helps us fix the issue faster. Please include:

- **Description** of the vulnerability and its potential impact
- **Affected component** (service, installer, workflow, configuration)
- **Steps to reproduce** or a proof-of-concept (if possible)
- **Environment** — OS version, .NET version, JDK version, JenkinsAsService version
- **Suggested fix** (optional, but welcome)

## Response Timeline

| Stage | Target |
|---|---|
| Acknowledge receipt | 3 business days |
| Initial assessment and severity | 7 business days |
| Patch development | 30 days for critical/high, 90 days for medium/low |
| Public disclosure | Coordinated with reporter, after patch is released |

Timelines are best-effort targets. This is a personal open-source project maintained by a single developer — not a commercial product with a dedicated security team.

## Coordinated Disclosure

We follow responsible disclosure practices:

- We will work with you to understand and reproduce the issue
- We will keep you informed of progress toward a fix
- We ask that you **do not share details publicly** until a patch is released
- We aim to publish a fix and advisory simultaneously
- You are welcome to self-disclose after 90 days if no fix has been released
- We will never publish your identity or communications without your permission

If you would like to be credited in the advisory, let us know — we are happy to acknowledge reporters.

## Security Architecture

JenkinsAsService handles sensitive data (Jenkins agent secrets) and runs as a privileged Windows Service. The following measures are in place:

### Secret Protection

Secrets are never stored in plaintext by default (the MSI installer defaults to `Dpapi`). Five protection modes are available, strongest first:

| Mode | Mechanism | Risk if host is compromised |
|---|---|---|
| `Tpm` | TPM-bound, non-exportable RSA key (Platform Crypto Provider) | Usable only on this machine's TPM, by a process that can access the key |
| `Dpapi` | Machine-scoped DPAPI encryption | Decryptable by any process on the same machine |
| `CredentialManager` | Windows Credential Manager vault | Accessible to processes running as the same user |
| `EnvironmentVariable` | Machine-level environment variable | Readable by any process on the machine |
| `Unprotected` | Plaintext in `appsettings.json` | Readable by anyone with file access |

### Secret Redaction

Agent secrets are scrubbed from all log output (file and Event Log) before being written. The redaction logic replaces any occurrence of the resolved secret with `*****`.

### Network Security

- TLS 1.2+ is enforced by default (.NET 10)
- Jenkins controller URL requires an explicit port (a URL with no port is rejected); a non-HTTPS URL is allowed but warned at startup because the agent secret would be sent unencrypted
- TCP connectivity is verified before each agent launch

### Build Integrity

- Deterministic builds with locked NuGet restore (`packages.lock.json`)
- Embedded PDB symbols (no separate symbol files to tamper with)
- SHA256 checksums published for all release artifacts
- Single-file self-contained deployment (no external DLLs to substitute)

### Automated Security Scanning

Six security scans run on every push and pull request:

| Scanner | What it checks |
|---|---|
| **CodeQL** | Static application security testing (SAST) for C# |
| **Semgrep** | Pattern-based SAST and secret detection |
| **Gitleaks** | Git history scanning for leaked secrets |
| **PSScriptAnalyzer** | PowerShell script security rules |
| **Dependency Review** | CVE gate on pull requests (blocks known-vulnerable dependencies) |
| **Trivy** | Software composition analysis (NVD, GHSA, OSV databases) |

## Dependencies

JenkinsAsService depends on third-party NuGet packages. We monitor these via:

- **Dependabot** — automated pull requests for version updates
- **Dependency Review** — blocks PRs that introduce known CVEs
- **Trivy SCA** — scans resolved dependencies against multiple vulnerability databases

If you discover a vulnerability in a dependency, please report it to the package maintainer. If the vulnerability is exploitable through JenkinsAsService specifically, report it to us as well.

## Bounties

This is a personal open-source project. All work is done on a volunteer basis. We are unable to offer monetary bounties for security reports. We do offer public credit in the advisory if desired.
