# Privacy Policy

This policy explains what data **JenkinsAsService** — both the installed Windows Service and the
[documentation website](https://jenkinsasservice.machlev.org) — does and does not collect.

JenkinsAsService is a self-hosted, local-first tool. **The project collects nothing about you.**
There are no accounts, no analytics, and no "phone home." Everything the service handles stays on the
machine you install it on, unless *you* explicitly configure it to send data somewhere you control.

## Summary

- The **service** runs entirely on your machine and sends no data to the project or any third party.
- **Telemetry is opt-in and off by default.** When you enable it, metrics go only to an endpoint you configure.
- The **documentation website** and **GitHub repository** are static/standard hosting. The third parties
  involved — Cloudflare and GitHub (hosting/proxy), plus jsDelivr and Shields.io (a diagram CDN and a
  badge service) — are infrastructure, not operated by this project, and none receive anything beyond the
  normal request metadata (such as your IP) inherent to loading a web page.

## The Software (JenkinsAsService Windows Service)

### Data stored locally

The service reads and writes the following, all of which stay on the host machine and are never
transmitted to the project:

- **Configuration** (`appsettings.json`, in the install folder) — your Jenkins controller URL, agent
  name, and the protected secret value. ACL-restricted to SYSTEM, Administrators and the service
  account. See [Secrets](#secrets) below.
- **The secret file** (`.agent-secret`, in the data folder) — an ACL-restricted file created only while
  the agent runs and deleted on service stop.
- **Logs** (`agent.log` or `agent.clef`, in the data folder) — service and Java-agent output. These may
  contain your machine's hostname, the configured agent name, exit codes, and controller connection
  messages. Agent secrets are [redacted](#secrets) before anything is written.
- **The cached agent binary** (`agent.jar` plus its `.etag` or `.modified` and `.sha256` sidecars, under `agent\`) and the
  Jenkins work directory (under `work\`).

The install folder lives in `Program Files` (read-only to the service account) and the writable data
lives in `%ProgramData%\JenkinsAsService` — see the [Configuration reference](https://jenkinsasservice.machlev.org/configuration.html).

### How long it is kept

Logs roll and the oldest are deleted once `Logging:RetainedLogs` (default 3) is exceeded. The secret
file is deleted when the service stops.

**Uninstalling deletes everything above** — the install folder including `appsettings.json`, the whole
data folder with the logs, the cached `agent.jar` and the work directory, **and the secret itself from
wherever it was stored**: the TPM key, the Credential Manager entry, or the machine environment
variable, depending on `Secret:Mode`. Nothing is retained on the machine afterwards, and nothing was
ever sent off it. If you want to keep logs or a build
workspace, copy them out before uninstalling; there is no prompt. An in-place *upgrade* preserves the
data folder in full.

### Network connections the service makes

The service opens a network connection in exactly two cases, and no others — there is no update check,
no crash reporting, and no usage analytics:

1. **Your Jenkins controller** — before each launch it makes a TCP connectivity check to the configured
   host/port, then downloads `agent.jar` over HTTP(S) from `<your-controller>/jnlpJars/agent.jar`
   (skipped when the cached copy is current). The launched Java agent then connects to that same
   controller to do its job. This is the controller **you** configure; what it logs is governed by your
   own Jenkins instance, not by this project.
2. **An OpenTelemetry endpoint — only if you turn it on.** Telemetry is disabled by default
   (`Telemetry:Enabled = false`). When enabled, the service exports **metrics only** over OTLP to the
   `OtlpEndpoint` you specify. The exported data is limited to counters and runtime gauges —
   `jenkins_agent_restarts_total`, `jenkins_agent_severe_events_total`, and standard .NET runtime metrics
   (GC, threads, memory). **No secrets, log contents, or personal data are included**, and the
   destination is entirely under your control.

That is the complete list. The service never contacts any server operated by the project or its author.

### Secrets

Your Jenkins agent secret is encrypted at rest (TPM, DPAPI, or Windows Credential Manager — or stored as
an environment-variable name / plaintext if you choose those modes), passed to the agent off the process
command line via `-secret @<file>`, and redacted (`*****`) from all log output. The project never
receives, transmits, or has any access to your secret. See the
[Security Policy](https://github.com/EliorMachlev/JenkinsAsService/blob/main/Security.md) for details.

## The Documentation Website (jenkinsasservice.machlev.org)

The docs site is a **static** site. It sets no cookies, runs no analytics or tracking scripts, has no
forms or accounts, and collects nothing itself. Three third parties are involved in serving it:

- **Cloudflare** provides DNS and proxies the custom domain (`jenkinsasservice.machlev.org`) in front of
  the origin. Because traffic is proxied through Cloudflare, it terminates TLS and can see your IP address
  and request metadata, per the [Cloudflare Privacy Policy](https://www.cloudflare.com/privacypolicy/).
- **GitHub Pages** is the origin that hosts the site behind Cloudflare. GitHub may log request metadata as
  described in the
  [GitHub Privacy Statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement)
  and [GitHub Pages data collection](https://docs.github.com/en/pages/getting-started-with-github-pages/what-is-github-pages#data-collection).
- **jsDelivr** (a public CDN) serves the Mermaid diagram library on the pages that render diagrams
  (Overview, Architecture, Configuration). Loading it exposes your IP address to jsDelivr, per the
  [jsDelivr Privacy Policy](https://www.jsdelivr.com/terms/privacy-policy-jsdelivr-net).

## The GitHub Repository

The source code, issues, and release downloads are hosted on **GitHub**. Browsing the repository, cloning
it, and downloading release artifacts all involve GitHub, which may log your IP address and request
metadata per the
[GitHub Privacy Statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement).
This is standard GitHub behavior, not something this project controls or adds to.

The README displays status badges. Most (License, .NET, Platform, Docs) are **self-hosted inside this
repository** and involve no third party. Two are served by GitHub (the Build and CodeQL workflow-status
badges), and one — the *Latest Release* badge — is generated by the **[Shields.io](https://shields.io)**
service, which sees the request for that image. When you view the README **on github.com**, GitHub
proxies all external badge images through its own [Camo image proxy](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/about-anonymized-urls),
so your IP goes to GitHub rather than directly to Shields.io. If the README is rendered somewhere else,
those image requests can reach the badge host directly.

## Third Parties at a Glance

| Party | When it's involved | What it may receive | Their policy |
|---|---|---|---|
| Cloudflare (DNS + proxy) | Visiting the docs site | IP address, request metadata (proxied traffic) | [Cloudflare Privacy Policy](https://www.cloudflare.com/privacypolicy/) |
| GitHub (repo + Pages) | Browsing/cloning the repo, downloading releases, or the docs site | IP address, request logs | [GitHub Privacy Statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement) |
| Shields.io | Viewing the README's *Latest Release* badge (proxied via GitHub Camo when viewed on github.com) | IP address | [Shields.io](https://shields.io) |
| jsDelivr (CDN) | Viewing diagram pages on the docs site | IP address | [jsDelivr Privacy Policy](https://www.jsdelivr.com/terms/privacy-policy-jsdelivr-net) |
| Your Jenkins controller | Running the service | Connections from the service and Java agent | Your organization's policy |
| Your OTLP endpoint | Only if telemetry is enabled | The metrics listed above | Your configuration |

## Changes to This Policy

This policy may be updated as the software evolves. The **Last updated** date below reflects the latest
revision, and material changes are noted in the release notes.

## Contact

For privacy questions, open an issue on the
[GitHub repository](https://github.com/EliorMachlev/JenkinsAsService/issues). For anything sensitive, use
the private contact channel described in the
[Security Policy](https://github.com/EliorMachlev/JenkinsAsService/blob/main/Security.md). Please never
include secrets or credentials in a report.

---

_Last updated: 2026-07-12_
