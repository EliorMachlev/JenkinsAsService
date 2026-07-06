# CLAUDE.md

Guidance for AI agents working in this repository.

## What This Is

**JenkinsAsService** runs a Jenkins inbound (JNLP) agent as a native **.NET 10 Windows Service** — no login session, no scheduled tasks, no manual restarts. It validates config, resolves Java, then (inside a supervision loop) tests connectivity, downloads `agent.jar` (ETag-cached), launches the Java agent, and supervises it with an event-driven watchdog that auto-recovers on crash. Heavy emphasis on **security hardening** (TPM/DPAPI secret protection, cert pinning, least-privilege service account, process mitigations, binary/data separation).

- **Target:** `net10.0-windows`, `Microsoft.NET.Sdk.Worker`, self-contained single-file (win-x64 + win-x86)
- **License:** BSD 3-Clause · **Repo:** github.com/EliorMachlev/JenkinsAsService · **Docs site:** jenkinsasservice.machlev.org

## Commands

Run from repo root. Always use PowerShell 7 (`pwsh`). Locked restore is enforced — keep `packages.lock.json` in sync.

```powershell
# Restore (locked mode — fails if lock files are stale)
dotnet restore -p:RestoreLockedMode=true

# Test (xUnit — run before claiming anything works)
dotnet test -c Release

# Publish optimized self-contained exe (x64; swap win-x86 for 32-bit)
dotnet publish src/JenkinsAsService `
    --nologo -c Release -p:RestoreLockedMode=true `
    -r win-x64 --self-contained true -o publish/x64/ `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:PublishReadyToRun=true

# Build WiX v5 MSI (excluded from solution build — build explicitly AFTER publishing)
dotnet build src/JenkinsAsService.Installer -c Release `
    -p:RestoreLockedMode=true -p:PublishDir=../../publish/x64/ -p:Version=1.0.0
```

The `.slnx` builds the app + tests only; the installer has `Build=false` and must be built by hand. There is no live Jenkins/TPM/MSI-install harness in this environment — logic is verified by the xUnit suite; installer/TPM/ACL behavior against a real box is manual.

## Source Layout (`src/JenkinsAsService/`)

`configuration.html` is current; `architecture.html`/`api-reference.html`/`overview.html` lag — verify against code. Grouped by concern:

- **Host / entry:** `Program.cs` (top-level statements: CLI dispatch → Serilog → host/DI → OTel → pre-flight validate → run), `JenkinsAgentWorker.cs` (`BackgroundService` — lifecycle, supervision loop, watchdog, metrics)
- **Config POCOs:** `ServiceSettings.cs` (nested: `Connection`/`Secret`/`Agent`/`Hardening`/`Logging`/`Recovery`), `TelemetrySettings.cs`, `ConfigKeys.cs` (centralized section/key name constants — **use these, don't hardcode config strings**), `ConnectionMethod.cs`, `SecretMode.cs`, `DpapiScope.cs`
- **Secrets:** `ISecretResolver.cs`/`SecretResolver.cs`, `SecretWriter.cs`, `UpdateSecretCommand.cs` (the `update-secret` CLI), `TpmSecretProtector.cs`, `AgentSecretFile.cs` (`-secret @<file>` off-argv), `CertificateThumbprintValidator.cs`
- **Agent process:** `AgentProcessLauncher.cs` (the `IAgentProcessLauncher`/`IAgentProcess` seam — production wraps `System.Diagnostics.Process`; fakes drive the watchdog in tests), `AgentArgumentParser.cs`, `AgentTransport.cs` (pure transport-selection: `InitialMethod`/`Toggle`/`ShouldFallback`), `JavaPathResolver.cs`, `ServiceSettingsValidator.cs`
- **Networking:** `IJarDownloader.cs`/`HttpJarDownloader.cs` (ETag conditional GET; downloads to a temp file + atomic move so a truncated download can't sit behind a valid ETag), `IConnectivityChecker.cs`/`TcpConnectivityChecker.cs`
- **Hardening:** `ProcessMitigations.cs`, `EnvironmentSanitizer.cs`, `ConfigAclHardener.cs`, `EventLogSourceInstaller.cs`, `DataPaths.cs` (binary/data split)
- **Other:** `Properties/AssemblyInfo.cs` (explicit assembly attributes — required for Codacy; do not delete)

`src/JenkinsAsService.Installer/` — WiX v5 MSI (`.wxs` files + `License.rtf`). `tests/JenkinsAsService.Tests/` — xUnit suite.

## Runtime behavior: the supervision loop (watchdog)

`JenkinsAgentWorker.RunSupervisionLoop` is the heart of the service. Two states, no polling:

- **Bring-up (no live agent):** connectivity → jar download → start, with exponential backoff (10s→300s) between attempts. This path serves **both** first launch and post-crash recovery, so a boot-order transient (DNS/network down, controller mid-restart) retries instead of faulting the service.
- **Live agent:** `await Task.WhenAny(agent.Exited, Task.Delay(StabilityMs))`. After 60s of stability the crash counter resets. A real exit before stability is a crash.

Key invariants (each has regression tests — don't regress them):
- **Crash vs. unreachable:** only real agent *crashes* count toward `Recovery:MaxRetries`. An unreachable controller is retried forever in bring-up and never trips give-up (a maintenance window must not permanently stop the service).
- **Give-up stops the service:** exceeding `MaxRetries` calls `IHostApplicationLifetime.StopApplication()` so SCM/Windows Service Recovery can act — never leave a dead agent behind a `RUNNING` service by just returning from the loop.
- **Exit signal lives in the launcher:** the exit `TaskCompletionSource` is captured in a local inside `AgentProcessLauncher`, not a shared field — a late exit from a killed process must not complete a newer process's signal (that caused spurious restarts).
- **Clean shutdown ≠ crash:** `StopAsync` sets `_stopping` before killing, so the kill-induced exit isn't counted/recovered; it also deletes the on-disk secret file.
- **Shutdown gate reaps late-started agents:** the loop is `while (true)` with a single `_stopping || cancelled → KillAgent(); return` gate at the top. Bring-up can start an agent *after* `StopAsync`'s own `KillAgent` ran (it was a no-op while `_agent` was null); without the gate that agent is orphaned past service shutdown. Don't convert the loop back to `while (!ct.IsCancellationRequested)`.
- **Never dereference the `_agent` field after a null-check** — `StopAsync` (another thread) nulls it via `KillAgent`. Snapshot `var agent = _agent` per iteration and use the local.
- **Auto transport fallback:** in `Connection:Method = Auto`, a *fast* crash toggles WebSocket ↔ direct-TCP on the next bring-up; a run that stabilised then died stays on the same transport.
- Timing (`_stabilityMs`/backoff) is injectable via `UseFastTimingForTests(...)`; `ComputeBackoffDelaySeconds`/`HasExceededMaxRetries` are `internal static` pure functions.

## Configuration

Settings live under the `Jenkins` section of `appsettings.json`, grouped into sub-sections addressed as `Section:Key`. Full reference: `docs/configuration.html` (authoritative). Required: `Connection:Url` and `Secret:Value`.

- **`Connection:Url`** must carry an **explicit port**. A URL with no port is rejected; an explicitly-written standard `:443` (e.g. a reverse-proxied controller) is accepted. The rule checks the raw string for a textual port, not `Uri.IsDefaultPort`, and is **scheme-agnostic** — plain `http` (incl. `:80`) is *not* blocked here, only warned separately (secret sent unencrypted). Don't advertise `:80` in examples.
- Key axes: `Connection:Method` (`Auto`/`WebSocket`/`Https` — `Https` means direct TCP inbound, not literal HTTPS), `Secret:Mode` (`Unprotected`/`Dpapi`/`Tpm`/`EnvironmentVariable`/`CredentialManager`), `Secret:ViaFile`, `Secret:DpapiScope`, `Hardening:SanitizeEnvironment`, `Agent:DataDirectory`, `Recovery:MaxRetries` (0 = infinite).
- Runtime data (jar, logs, secret file, workdir) lives in `%ProgramData%\JenkinsAsService`, **separate** from the read-only install folder in `Program Files`.

## Conventions & Invariants

- **Central Package Management:** all NuGet versions live in `Directory.Packages.props`; `.csproj` files reference packages **without** version attributes. Never add a `Version=` to a `PackageReference`.
- **Locked restore everywhere:** `RestorePackagesWithLockFile=true`. Adding/bumping a package means regenerating `packages.lock.json` (app, tests, and installer each have one) — avoid new deps unless necessary (e.g. `Xunit.SkippableFact` was declined to avoid lock churn; xUnit is 2.9.3, so **no `Assert.Skip`** — TPM-dependent tests early-`return`).
- `Nullable` + `ImplicitUsings` enabled. Tests see internals via `InternalsVisibleTo` — prefer `internal static` testable methods (validators, parsers, pure predicates) over exposing types publicly. DI-injected interfaces must be `public` (e.g. `IAgentProcessLauncher`) because the worker's ctor is public.
- **Config key strings** come from `ConfigKeys` constants, not literals — binding is by-name and typos are silent.
- **Security posture is a hard requirement, not optional polish.** Do not regress: secrets stay off the process table (`-secret @file`), out of logs (redaction), and encrypted at rest; the agent child gets a deny-by-default environment; the install folder stays non-writable by the agent identity; the ACL-restricted secret file must stay writable/deletable by the *low-priv service account itself* (grant `Modify`, don't `SetOwner` to a group it can't assign). If a change touches these, call it out.
- **GitHub Actions are SHA-pinned** (tag as trailing comment). Don't replace a pin with a floating tag; Dependabot keeps pins current.
- Deterministic builds; embedded PDB; no floating version ranges.
- Commit messages end with `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`. Commit/push only when asked.

## Testing

xUnit + NSubstitute + FluentAssertions; **143 tests** currently (trust the runner, not any hard-coded number in docs). Test classes mirror units. Notable:
- `SupervisionLoopTests` drives the full watchdog end-to-end via `FakeAgentProcess`/`FakeAgentProcessLauncher` with millisecond timing (restart-on-crash, give-up-and-stop, unreachable-never-gives-up).
- `WatchdogTimingTests` — pure backoff-curve + max-retry unit tests.
- `ConfigBindingTests` — binds the nested schema (incl. all three enums) and the shipped `appsettings.json` through real `IConfiguration`.
- `TpmSecretProtectorTests` — most tests early-`return` (report as *passed*) when no TPM/elevation; green there means "did not regress", not "TPM verified".

Add tests alongside new logic; run `dotnet test -c Release` and confirm green before claiming completion.

## Installer (`src/JenkinsAsService.Installer/`, WiX v5)

- Default secret mode is **`Dpapi`** (encrypted at rest, machine-scoped) — deliberately **not** the world-readable machine env var. The Security Options radio group lists DPAPI (recommended) → TPM (strongest) → Credential Manager → Environment Variable → Unprotected.
- The install-time secret is passed on the `WriteConfig` deferred CA command line — an accepted MSI trade-off (EXE custom actions get no `CustomActionData`); mitigated by `Hidden="yes"` + `Impersonate="no"` (runs as SYSTEM). Scripted installs that must avoid even a verbose MSI log should call `update-secret` directly with the secret-env/secret-file input options.
- Default identity is the virtual account `NT SERVICE\Jenkins`. Runtime data dir is set by a second CA (`--set-data-dir`) because folding it into the main command would exceed the MSI 255-char CA limit.
- XML comments must not contain `--` (WIX0104); validate `.wxs` well-formedness after edits.

## CI/CD & Security Scans

`.github/workflows/`: `build.yml` (restore→test→dual-arch publish→MSI on every push/PR), `release.yml` (tag `v*` or manual dispatch → dual-arch MSI + 7z/rar + SHA256 checksums + SBOM), plus security scans — `codeql.yml` (C# + Actions YAML), `semgrep.yml`, `gitleaks.yml`, `powershell.yml` (PSScriptAnalyzer via direct `pwsh` step), `dependency-review.yml`, `osv-scanner.yml`/`trivy-reusable.yml`. Also `html-lint.yml` for `docs/` (html5lib + Stylelint — HTML edits must pass). `build.yml` mirrors every release compile/package step so failures surface on PRs.

## Documentation

- **Official docs (in-repo, published site):** `docs/*.html`. `configuration.html` is authoritative for settings. `architecture.html`/`overview.html`/`api-reference.html` may lag on some internals (notably `api-reference.html`'s file list) — verify against code before trusting them. When code and docs disagree, **the code wins** — and update the affected doc.
- **Owner's private notes (not in repo):** `Q:\Git\Obsidian Vaults\eliormachlev\Docs\Projects\Developments\JenkinsAsService\`.
- `README.md`, `Security.md`, `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md` at root are current.

## Current state / open items

- The hardening batch (PR #47 — secret-file ACL fix, watchdog resilience rewrite + process seam, installer security defaults, +20 tests, docs) is **merged to `main`**. A follow-up bug-review pass (PR #48, branch `hotfix/Fable`) fixed two shutdown races in the supervision loop (orphaned agent on stop-during-bring-up; `_agent` NRE) plus a Process-handle leak and an `update-secret --secret-file` delete crash.
- **Deferred (by decision, not oversight):** release-artifact **code signing** (waiting on a free OSS cert; CI step not yet wired); **post-quantum at-rest encryption** (ML-KEM/ML-DSA declined — rotate secrets at the PQC transition instead); making plain-`http` a hard failure (still only warns); visible xUnit skips for TPM tests (needs a package or xUnit v3).
- **Not yet done against real infra:** a real MSI install validating ACL/TPM-decrypt, and live WebSocket/`Auto`-fallback verification against a controller.
