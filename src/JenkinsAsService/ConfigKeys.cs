// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

/// <summary>
/// Centralised configuration key names for the nested <c>Jenkins</c> section. These must match the
/// <see cref="ServiceSettings"/> property names exactly (binding is by name), so keeping them in one
/// place removes the typo risk of the same literal appearing across the writer, the CLI and startup.
/// <c>*Path</c> constants are the section-relative colon paths used with configuration indexers.
/// </summary>
internal static class ConfigKeys
{
    internal const string Section = "Jenkins";

    /// <summary>Top-level telemetry section (sibling of <see cref="Section"/>, binds to <c>TelemetrySettings</c>).</summary>
    internal const string TelemetrySection = "Telemetry";

    /// <summary>The settings file name, resolved relative to the application base directory. Single source
    /// of truth for the startup reader, the writer, and the CLI so the literal never drifts between them.</summary>
    internal const string FileName = "appsettings.json";

    internal static class Connection
    {
        internal const string Name = "Connection";
        internal const string Url = "Url";
        internal const string Method = "Method";
        internal const string AgentName = "AgentName";
        internal const string ControllerCertThumbprint = "ControllerCertThumbprint";

        internal const string UrlPath = Name + ":" + Url;
        internal const string AgentNamePath = Name + ":" + AgentName;
        internal const string ControllerCertThumbprintPath = Name + ":" + ControllerCertThumbprint;
    }

    internal static class Secret
    {
        internal const string Name = "Secret";
        internal const string Value = "Value";
        internal const string Mode = "Mode";
        internal const string DpapiScope = "DpapiScope";
        internal const string ViaFile = "ViaFile";

        internal const string ModePath = Name + ":" + Mode;
        internal const string ValuePath = Name + ":" + Value;
    }

    internal static class Agent
    {
        internal const string Name = "Agent";
        internal const string JavaPath = "JavaPath";
        internal const string CustomArguments = "CustomArguments";
        internal const string DataDirectory = "DataDirectory";

        internal const string JavaPathPath = Name + ":" + JavaPath;
        internal const string DataDirectoryPath = Name + ":" + DataDirectory;

        internal static class LaunchInInteractiveSession
        {
            internal const string Name = "LaunchInInteractiveSession";
            internal const string Enabled = "Enabled";
            internal const string LocalSystemOnly = "LocalSystemOnly";
            internal const string TargetUser = "TargetUser";
            internal const string RequireInteractiveSession = "RequireInteractiveSession";
            internal const string PreferDisconnectedSession = "PreferDisconnectedSession";
            internal const string SessionMigration = "SessionMigration";
        }
    }

    internal static class Hardening
    {
        internal const string Name = "Hardening";
        internal const string SanitizeEnvironment = "SanitizeEnvironment";
        internal const string AllowedEnvironmentVariables = "AllowedEnvironmentVariables";
    }

    internal static class Logging
    {
        internal const string Name = "Logging";
        internal const string DebugMode = "DebugMode";
        internal const string CompactLog = "CompactLog";
        internal const string RetainedLogs = "RetainedLogs";

        internal const string DebugModePath = Name + ":" + DebugMode;
        internal const string CompactLogPath = Name + ":" + CompactLog;
        internal const string RetainedLogsPath = Name + ":" + RetainedLogs;
    }

    internal static class Recovery
    {
        internal const string Name = "Recovery";
        internal const string MaxRetries = "MaxRetries";
    }
}
