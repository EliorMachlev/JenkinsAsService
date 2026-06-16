// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

public sealed class ServiceSettings
{
    /// <summary>Full Jenkins URL including port. Example: https://jenkins.example.com:8443</summary>
    public string JenkinsUrl { get; set; } = "";

    /// <summary>Agent/node name in Jenkins (case-sensitive). Defaults to machine hostname.</summary>
    public string AgentName { get; set; } = "";

    /// <summary>JNLP secret from Jenkins node configuration (case-sensitive).</summary>
    public string AgentSecret { get; set; } = "";

    /// <summary>Path to JDK/OpenJDK bin folder. Falls back to JAVA_HOME if empty.</summary>
    public string JavaPath { get; set; } = "";

    /// <summary>Extra arguments for java.exe (e.g. -noCertificateCheck).</summary>
    public string CustomArguments { get; set; } = "";

    /// <summary>Enable verbose Java agent output in logs.</summary>
    public bool DebugMode { get; set; }

    /// <summary>Max auto-recovery attempts. 0 = infinite (default).</summary>
    public int MaxRetries { get; set; }

    /// <summary>Use compact JSON format for log file (Serilog CompactJsonFormatter). Default: false.</summary>
    public bool CompactLog { get; set; }

    /// <summary>Number of rolled log files to keep. Oldest are permanently deleted. Default: 3.</summary>
    public int RetainedLogs { get; set; } = 3;

    /// <summary>Secret protection mode: Unprotected, Dpapi, EnvironmentVariable, CredentialManager.</summary>
    public SecretMode SecretMode { get; set; } = SecretMode.Unprotected;
}
