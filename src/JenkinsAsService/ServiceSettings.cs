// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

/// <summary>
/// Root of the <c>Jenkins</c> configuration section. Settings are grouped into topic sub-sections
/// (<c>Connection</c>, <c>Secret</c>, <c>Agent</c>, <c>Hardening</c>, <c>Logging</c>, <c>Recovery</c>)
/// that bind from the matching nested objects in appsettings.json.
/// </summary>
public sealed class ServiceSettings
{
    public ConnectionSettings Connection { get; set; } = new();
    public SecretSettings Secret { get; set; } = new();
    public AgentSettings Agent { get; set; } = new();
    public HardeningSettings Hardening { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();
    public RecoverySettings Recovery { get; set; } = new();
}

/// <summary>Controller connection and identity (<c>Jenkins:Connection</c>).</summary>
public sealed class ConnectionSettings
{
    /// <summary>Full Jenkins URL including port. Example: https://jenkins.example.com:8443</summary>
    public string Url { get; set; } = "";

    /// <summary>Agent/node name in Jenkins (case-sensitive). Defaults to machine hostname.</summary>
    public string AgentName { get; set; } = "";

    /// <summary>
    /// Optional SHA-256 thumbprint (hex, colons/spaces ignored) of the Jenkins controller's TLS
    /// certificate. When set, the agent.jar download pins the server certificate to this value,
    /// rejecting any other certificate even if chain-trusted. Empty = standard chain validation only.
    /// </summary>
    public string ControllerCertThumbprint { get; set; } = "";
}

/// <summary>Agent secret storage and protection (<c>Jenkins:Secret</c>).</summary>
public sealed class SecretSettings
{
    /// <summary>The JNLP secret, or (depending on <see cref="Mode"/>) the ciphertext, env var name, or
    /// credential target that resolves to it.</summary>
    public string Value { get; set; } = "";

    /// <summary>Secret protection mode: Unprotected, Dpapi, Tpm, EnvironmentVariable, CredentialManager.</summary>
    public SecretMode Mode { get; set; } = SecretMode.Unprotected;

    /// <summary>
    /// DPAPI protection scope (only used when <see cref="Mode"/> is <c>Dpapi</c>): <c>Machine</c>
    /// (default, any local process can decrypt) or <c>User</c> (only the encrypting identity can
    /// decrypt). User-scope requires the secret to be written by the service account.
    /// </summary>
    public DpapiScope DpapiScope { get; set; } = DpapiScope.Machine;

    /// <summary>
    /// When <c>true</c> (default), the resolved secret is written to an ACL-restricted file and passed to
    /// the Java agent as <c>-secret @&lt;file&gt;</c> instead of inline on the command line, keeping it out
    /// of the process table. Set <c>false</c> to pass the secret directly as a command-line argument.
    /// </summary>
    public bool ViaFile { get; set; } = true;
}

/// <summary>Java agent process and runtime data (<c>Jenkins:Agent</c>).</summary>
public sealed class AgentSettings
{
    /// <summary>Path to JDK/OpenJDK bin folder. Falls back to JAVA_HOME if empty.</summary>
    public string JavaPath { get; set; } = "";

    /// <summary>Extra arguments for java.exe (e.g. -noCertificateCheck).</summary>
    public string CustomArguments { get; set; } = "";

    /// <summary>
    /// Writable data directory for runtime artifacts the service produces — <c>agent.jar</c> (+ ETag
    /// cache), log files, the secret file, and the agent work directory. Kept separate from the
    /// read-only install folder so the binary can't be overwritten by the agent identity. Environment
    /// variables are expanded. Empty (default) resolves to <c>%ProgramData%\JenkinsAsService</c>.
    /// </summary>
    public string DataDirectory { get; set; } = "";
}

/// <summary>Process-hardening toggles for the spawned agent (<c>Jenkins:Hardening</c>).</summary>
public sealed class HardeningSettings
{
    /// <summary>
    /// When <c>true</c> (default), the Java agent child process is launched with a sanitized environment
    /// containing only a curated allow-list plus <see cref="AllowedEnvironmentVariables"/>, preventing the
    /// service's own environment block (and any secrets in it) from leaking into untrusted pipeline scripts.
    /// </summary>
    public bool SanitizeEnvironment { get; set; } = true;

    /// <summary>
    /// Additional environment variable names (semicolon- or comma-separated) to pass through to the Java
    /// agent child when <see cref="SanitizeEnvironment"/> is enabled. Use for build tooling that needs
    /// specific host variables (e.g. <c>GRADLE_USER_HOME;MAVEN_OPTS</c>). Names are case-insensitive.
    /// </summary>
    public string AllowedEnvironmentVariables { get; set; } = "";
}

/// <summary>Logging output options (<c>Jenkins:Logging</c>).</summary>
public sealed class LoggingSettings
{
    /// <summary>Enable verbose Java agent output in logs.</summary>
    public bool DebugMode { get; set; }

    /// <summary>Use compact JSON format for log file (Serilog CompactJsonFormatter). Default: false.</summary>
    public bool CompactLog { get; set; }

    /// <summary>Number of rolled log files to keep. Oldest are permanently deleted. Default: 3.</summary>
    public int RetainedLogs { get; set; } = 3;
}

/// <summary>Auto-recovery behaviour (<c>Jenkins:Recovery</c>).</summary>
public sealed class RecoverySettings
{
    /// <summary>Max auto-recovery attempts. 0 = infinite (default).</summary>
    public int MaxRetries { get; set; }
}
