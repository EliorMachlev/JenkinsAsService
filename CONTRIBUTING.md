# Contributing to JenkinsAsService

Thanks for your interest in contributing! This document explains how to get involved, what to expect, and how to set up your development environment.

## Ways to Contribute

- **Bug reports** — found something broken? [Open an issue](https://github.com/EliorMachlev/JenkinsAsService/issues/new?template=bug_report.md)
- **Feature requests** — have an idea? Open an issue and describe the use case
- **Pull requests** — fixes, improvements, and new features are welcome
- **Documentation** — typos, unclear instructions, missing examples
- **Security vulnerabilities** — see [Security Policy](Security.md) (do not open public issues)

## Before You Start

1. Check the [existing issues](https://github.com/EliorMachlev/JenkinsAsService/issues) to avoid duplicates
2. For non-trivial changes, open an issue first to discuss the approach
3. One PR per concern — don't bundle unrelated changes

## Development Setup

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A [supported JDK or OpenJDK](https://www.jenkins.io/doc/book/platform-information/support-policy-java/#running-jenkins-system)
- Windows 10 / Server 2016 or later
- [WiX v5](https://wixtoolset.org/) (only if modifying the installer — auto-restored via NuGet)

### Build

```powershell
# Restore (locked mode)
dotnet restore -p:RestoreLockedMode=true

# Run tests
dotnet test -c Release

# Publish
dotnet publish src/JenkinsAsService `
    --nologo -c Release `
    -p:RestoreLockedMode=true `
    -r win-x64 --self-contained true `
    -o publish/x64/ `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishReadyToRun=true

# Optional: build MSI installer (after publishing — it's excluded from the solution build)
dotnet build src/JenkinsAsService.Installer -c Release `
    -p:RestoreLockedMode=true -p:PublishDir=../../publish/x64/ -p:Version=1.0.0
```

### Project Structure

| Path | Description |
|---|---|
| `src/JenkinsAsService/` | The Windows Service (worker, settings, secret protection, HTTP client) |
| `src/JenkinsAsService.Installer/` | WiX v5 MSI installer (dialogs, service registration, custom actions) |
| `tests/JenkinsAsService.Tests/` | xUnit tests with NSubstitute and FluentAssertions |
| `.github/workflows/` | CI/CD pipelines (build, release, security scans) |
| `misc/badges/` | Self-hosted SVG badges for the README |

## Pull Request Guidelines

### Branch Naming

Use a descriptive prefix:

- `feat/` — new functionality
- `fix/` — bug fixes
- `chore/` — maintenance, dependency updates, CI changes
- `docs/` — documentation only

### Code Standards

- Target framework is `net10.0-windows` — do not add cross-platform targets
- Follow the existing code style (no `.editorconfig` overrides)
- **Central Package Management**: every NuGet version lives in `Directory.Packages.props`. `PackageReference` entries in `.csproj` files must **not** carry a `Version` attribute
- No floating version ranges — pin all dependencies to exact versions
- Update the `packages.lock.json` files when adding or changing dependencies (`dotnet restore --force-evaluate`); the app, tests and installer each have one, and CI restores with `RestoreLockedMode=true`
- Prefer `internal static` testable units (validators, parsers, pure predicates) over widening a type to public; tests reach internals via `InternalsVisibleTo`
- Use the `ConfigKeys` constants for configuration paths — binding is by name, so a typo is silent
- Keep the single-file self-contained deployment model — avoid dependencies that break it
- Don't regress the security posture: secrets stay off the process table and out of logs, the agent child keeps its deny-by-default environment, and the install folder stays non-writable by the agent identity. If a change touches any of these, call it out in the PR

### Tests

- All PRs must pass the existing test suite
- New features should include tests
- Tests use **xUnit**, **NSubstitute** (mocking), and **FluentAssertions**
- Run `dotnet test -c Release` before submitting

### Commits

- Use [Conventional Commits](https://www.conventionalcommits.org/) format: `feat:`, `fix:`, `chore:`, `docs:`, `ci:`, `test:`
- Keep commits focused — one logical change per commit
- Write clear commit messages that explain *what* and *why*

### What Happens After You Submit

1. CI runs automatically — build, tests, and 6 security scans must pass
2. The maintainer reviews the PR (usually within a few days)
3. You may be asked for changes — this is normal, not a rejection
4. Once approved, the PR is squash-merged into `main`

## CI Pipeline

Every push and PR triggers:

| Check | What it does |
|---|---|
| **Build** | Restore, build, and test on .NET 10 |
| **CodeQL** | Static analysis for C# vulnerabilities |
| **Semgrep** | Pattern-based SAST and secret detection |
| **Gitleaks** | Scans git history for leaked secrets |
| **PSScriptAnalyzer** | PowerShell script linting |
| **Dependency Review** | Blocks PRs that introduce known CVEs |
| **Trivy** | Software composition analysis (NVD, GHSA, OSV) |
| **HTML & CSS lint** | Runs when `docs/` changes: W3C Nu HTML validation + Stylelint |

All checks must pass before merge.

## Documentation Changes

The published site lives in `docs/*.html` (plain HTML, no build step). `docs/configuration.html` is the authoritative settings reference — when code and docs disagree, the code wins and the doc gets fixed. If you add a page or a major section, add it to `docs/search-index.json` too, or it won't be findable from the in-page search. Docs edits must pass the HTML/CSS lint workflow.

## Installer Changes

The MSI installer uses **WiX v5** with a custom dialog sequence. If you modify the installer:

- Test the full install/uninstall cycle on a clean VM or sandbox
- Verify the service starts, connects to Jenkins, and survives a reboot
- Test silent install with `msiexec /qn` and all public properties
- Verify in-place upgrades from the previous version work correctly

## Questions?

Open an issue with the `question` label or start a [discussion](https://github.com/EliorMachlev/JenkinsAsService/discussions) if available.

## License

By contributing, you agree that your contributions will be licensed under the [BSD 3-Clause License](LICENSE).
