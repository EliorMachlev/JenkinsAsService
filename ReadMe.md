# Jenkins As Service

Run a Jenkins inbound (JNLP) agent as a native Windows Service — no login session, no scheduled tasks, no manual restarts.

[![GitHub Downloads (all assets, latest release)](https://img.shields.io/github/downloads/EliorMachlev/JenkinsAsService/latest/total?sort=date&style=flat-square&label=Download%20Latest%20Release&labelColor=%23008000&color=%23808080)](https://github.com/EliorMachlev/JenkinsAsService/releases/latest)
[![GitHub Downloads (all assets, latest release)](https://img.shields.io/github/downloads-pre/EliorMachlev/JenkinsAsService/latest/total?sort=date&style=flat-square&label=Download%20Latest%20Pre-Release&labelColor=%23cc5500&color=%23808080)](https://github.com/EliorMachlev/JenkinsAsService/releases/tag/Pre-Release)
[![License](https://img.shields.io/github/license/EliorMachlev/JenkinsAsService?style=flat-square)](LICENSE)

## What It Does

JenkinsAsService is a .NET 10 Windows Service that wraps the standard Jenkins `agent.jar` connection. On every service start it:

1. Reads configuration from `appsettings.json`
2. Validates settings (Jenkins URL with explicit port, agent secret, Java path)
3. Tests TCP connectivity to the Jenkins controller
4. Downloads a fresh `agent.jar` from the controller (ETag-based conditional GET — skips on 304 Not Modified)
5. Launches the Java agent process with structured logging and secret redaction

The service starts automatically on boot, runs under LocalSystem, and includes a built-in event-driven watchdog with auto-recovery. Log entries are enriched with machine name and environment for multi-agent deployments.

## Prerequisites

- **Windows** — Windows 10 / Server 2016 or later
- **Java** — JDK or OpenJDK 11+ installed, with either:
  - `JAVA_HOME` environment variable set, **or**
  - The path to the `bin` folder configured in `appsettings.json`
- **Jenkins** — A Jenkins controller with an inbound (JNLP) agent node configured

> No .NET runtime required on target — the published executable is self-contained.

## Installation

### MSI Installer (Recommended)

1. Download the `.msi` for your architecture (x64 or x86) from [Releases](https://github.com/EliorMachlev/JenkinsAsService/releases)
2. Run the installer — it installs to `C:\Program Files\Jenkins` and registers the Windows Service automatically
3. Edit `C:\Program Files\Jenkins\appsettings.json` — fill in `JenkinsURL` and `AgentSecret`
4. Start the service:

```powershell
Start-Service -Name 'Jenkins'
```

The MSI handles service registration, startup type (Automatic), and clean uninstall. Upgrades are in-place — install the new version over the old one.

### From Archive (Manual)

1. Download the `.7z` or `.rar` archive from [Releases](https://github.com/EliorMachlev/JenkinsAsService/releases)
2. Extract to `C:\Program Files\Jenkins`
3. Edit `appsettings.json` — fill in `JenkinsURL` and `AgentSecret`
4. Register and start the service:

```powershell
New-Service -Name 'Jenkins' -BinaryPathName 'C:\Program Files\Jenkins\JenkinsAsService.exe' -StartupType Automatic -Description 'Jenkins Agent as Service'
Start-Service -Name 'Jenkins'
```

### Build From Source

```powershell
# Restore (locked)
dotnet restore -p:RestoreLockedMode=true

# Publish optimized single-file executable
dotnet publish src/JenkinsAsService `
    --nologo --configuration Release `
    -p:RestoreLockedMode=true `
    --runtime win-x64 --self-contained true `
    --output publish/x64/ `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishReadyToRun=true

# Build MSI (optional, requires WiX v5 SDK)
dotnet build src/JenkinsAsService.Installer -c Release -p:PublishDir=../../publish/x64/ -p:Version=1.0.0
```

## Configuration

The service reads settings from `appsettings.json` in the same directory as the executable.

| Setting | Required | Default | Description |
|---|:---:|---|---|
| `JenkinsURL` | Yes | — | Full URL with explicit port, e.g. `https://jenkins.example.com:8443` |
| `AgentSecret` | Yes | — | JNLP secret from Jenkins node configuration (case-sensitive) |
| `SecretMode` | No | `Unprotected` | How the secret is stored — see [Secret Protection](#secret-protection) |
| `AgentName` | No | Hostname | Node name in Jenkins (case-sensitive) |
| `JavaPath` | No | `JAVA_HOME` | Path to JDK `bin` folder, e.g. `C:\Program Files\Java\jdk-21\bin` |
| `CustomArguments` | No | *(empty)* | Extra arguments for `java.exe`, e.g. `-noCertificateCheck`. Supports quoted values with spaces and escaped quotes (`\"`). |
| `DebugMode` | No | `false` | Set to `true` for verbose logging |
| `CompactLog` | No | `false` | CLEF (Compact Log Event Format) JSON output instead of human-readable text |
| `MaxRetries` | No | `0` | Max auto-recovery attempts (`0` = infinite) |

```json
{
  "Jenkins": {
    "JenkinsURL": "https://jenkins.example.com:8443",
    "AgentSecret": "your-secret-here",
    "SecretMode": "Unprotected",
    "AgentName": "",
    "JavaPath": "",
    "CustomArguments": "",
    "DebugMode": false,
    "CompactLog": false,
    "MaxRetries": 0
  },
  "Telemetry": {
    "Enabled": false,
    "OtlpEndpoint": "http://localhost:4317",
    "ServiceName": "JenkinsAsService"
  }
}
```

### Getting Your Agent Secret

1. In Jenkins, go to **Manage Jenkins** > **Nodes**
2. Click on your agent node
3. The secret is shown in the agent connection command

### Secret Protection

`SecretMode` controls how `AgentSecret` is stored and resolved at runtime.

| Mode | `AgentSecret` contains | How resolved |
|---|---|---|
| `Unprotected` | Plaintext secret | Returned as-is |
| `Dpapi` | Base64-encoded DPAPI ciphertext | `ProtectedData.Unprotect` (machine-scoped) |
| `EnvironmentVariable` | Name of a system env var | Reads machine-level environment variable |
| `CredentialManager` | Credential Manager target name | Reads from Windows Credential Manager |

Use the `update-secret` CLI subcommand to write the secret in your chosen mode:

```powershell
JenkinsAsService.exe update-secret --secret "your-secret" --url "https://jenkins:8443" --mode Dpapi --silent
```

> **Note:** DPAPI mode binds the secret to the machine. Re-run `update-secret` after migrating to a different machine.

### OpenTelemetry (Optional)

Set `Telemetry:Enabled` to `true` to export metrics via OTLP. Exported metrics:
- `jenkins_agent_restarts_total` — agent restart counter
- `jenkins_agent_severe_events_total` — SEVERE log line counter
- .NET runtime metrics (GC, thread pool, memory)

## Logging

Logs are written to `agent.log` in the installation directory (or `agent.clef` in compact JSON mode). Default format:

```
<PID> | <datetime> | <Level> | <message>
```

Set `CompactLog: true` for machine-parseable CLEF (Compact Log Event Format) JSON output — useful for log aggregation tools like Seq or Datadog.

Log levels: `Information`, `Warning`, `Error`, `Debug` (debug only when `DebugMode=true`).

### Log Rotation

When `agent.log` exceeds 10MB, it rotates automatically:
- `agent.log` → `agent_001.log` → `agent_002.log` → `agent_003.log` (oldest deleted)

### Windows Event Log

Warnings and errors are also written to the Windows Application event log under source `JenkinsAsService`, providing crash evidence even if the file log is unavailable.

```powershell
Get-EventLog -LogName Application -Source JenkinsAsService -Newest 20
```

## Service Management

```powershell
# Start the service
Start-Service -Name 'Jenkins'

# Stop the service
Stop-Service -Name 'Jenkins'

# Check status
Get-Service -Name 'Jenkins'

# View recent logs
Get-Content -Path 'C:\Program Files\Jenkins\agent.log' -Tail 50
```

## Auto-Recovery

The service includes an event-driven watchdog that monitors the Java agent process. If the agent dies (network drop, Jenkins restart, Java crash):

1. The watchdog detects the failure instantly via process exit event
2. Captures the exit code and logs the SEVERE event count
3. Waits with exponential backoff (10s → 20s → 40s → ... → 5min max)
4. Tests TCP connectivity before attempting recovery
5. Re-downloads `agent.jar` (ETag-based — skips if unchanged)
6. Restarts the agent
7. Resets the retry counter after 60 seconds of stability

Set `MaxRetries` to limit recovery attempts (`0` = infinite, default).

### Windows Service Recovery (Optional)

For full service host crashes (rare), also configure Windows-level recovery in `services.msc` > Jenkins > Properties > Recovery:
- First/Second failure: **Restart the Service**
- Reset fail count after: **1 day**

## Troubleshooting

| Symptom | Likely Cause | Fix |
|---|---|---|
| Service starts and stops immediately | Mandatory fields empty | Check `agent.log` for which field is missing |
| "JenkinsURL must include an explicit port" | Using default port 80/443 | Add explicit port: `https://jenkins:8443` |
| "Cannot reach Jenkins at..." | Jenkins unreachable | Verify URL, port, firewall, and that Jenkins is running |
| "Cannot find java.exe" | Wrong Java path | Point to the JDK `bin` folder, not the JDK root |
| Agent connects then disconnects | Name/secret mismatch | Verify `AgentName` and `AgentSecret` match Jenkins exactly |
| Watchdog keeps restarting | Persistent connectivity issue | Enable `DebugMode: true`, check `Watchdog:` entries in log |
| "Max retries exceeded" | Too many consecutive failures | Fix root cause, then restart service (or set `MaxRetries: 0`) |

For detailed troubleshooting, enable `DebugMode: true` and inspect `agent.log`.

## Security

- TLS 1.2+ enforced by default (.NET 10 runtime)
- Secret protection via DPAPI, Windows Credential Manager, or environment variables
- Agent secrets redacted from all log output
- HTTP resilience (automatic retry, circuit breaker, timeout) via Polly
- ETag-based jar caching prevents unnecessary downloads
- Deterministic builds with locked NuGet restore
- CI pipeline runs 59 unit tests on every push and PR
- Six security scanning workflows:
  - **CodeQL** — Source code vulnerability analysis
  - **Semgrep** — Pattern-based SAST (C# + secrets)
  - **Gitleaks** — Secret scanning across full git history
  - **PSScriptAnalyzer** — PowerShell static analysis
  - **Dependency Review** — Block PRs with known CVEs
  - **Trivy** — Full-repo dependency vulnerability scan (NVD + GHSA + OSV)
- Automated release on version tags (`v*` → test → publish x64 + x86 → MSI → archives → SHA256 checksums → GitHub Release)
- See [Security Policy](Security.md) for reporting vulnerabilities

## Project Structure

```
JenkinsAsService/
├── src/
│   └── JenkinsAsService/
│       ├── JenkinsAsService.csproj     # .NET 10 Worker Service project
│       ├── Program.cs                  # Host builder, Serilog, DI, OpenTelemetry
│       ├── ServiceSettings.cs          # Configuration model
│       ├── TelemetrySettings.cs        # OpenTelemetry configuration
│       ├── SecretMode.cs               # Secret protection enum
│       ├── ISecretResolver.cs          # Secret resolution abstraction
│       ├── SecretResolver.cs           # DPAPI / CredMgr / EnvVar / plaintext
│       ├── SecretWriter.cs             # Writes appsettings.json per mode
│       ├── UpdateSecretCommand.cs      # CLI: update-secret subcommand
│       ├── JenkinsAgentWorker.cs       # Service logic + watchdog
│       ├── IJarDownloader.cs           # Jar download abstraction
│       ├── HttpJarDownloader.cs        # HTTP downloader with ETag caching
│       ├── IConnectivityChecker.cs     # Connectivity check abstraction
│       ├── TcpConnectivityChecker.cs   # TCP connectivity checker
│       └── appsettings.json            # Configuration template
├── src/
│   └── JenkinsAsService.Installer/     # WiX v5 MSI installer
├── tests/
│   └── JenkinsAsService.Tests/         # 59 unit tests (xUnit + NSubstitute + FluentAssertions)
├── .github/workflows/                  # CI/CD: build, test, 6 security scans, release
├── Security.md
├── LICENSE                             # BSD 3-Clause
└── ReadMe.md
```

## License

[BSD 3-Clause License](LICENSE) — Copyright (c) 2024, EliorMachlev
