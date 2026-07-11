// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

/// <summary>
/// Resolves the writable <em>data</em> directory, kept separate from the read-only install directory
/// (where the signed binary lives). Runtime artifacts the service must write live here so the install
/// folder can stay non-writable by the agent identity, closing the "agent overwrites its own .exe"
/// privilege-escalation vector. Within the data directory the cached agent binary lives in the
/// <see cref="AgentFolderName"/> subfolder and the Jenkins <c>-workDir</c> in <see cref="WorkFolderName"/>,
/// so build/workspace churn (git clean, workspace collisions) can never clobber <c>agent.jar</c> or its
/// integrity metadata. Default root: <c>%ProgramData%\JenkinsAsService</c> (the MSI grants the service
/// account write on it); override via the <c>DataDirectory</c> setting.
/// </summary>
internal static class DataPaths
{
    internal const string AppFolderName = "JenkinsAsService";

    /// <summary>Subfolder holding the cached <c>agent.jar</c> plus its ETag and SHA-256 sidecars — isolated
    /// from the work directory so a build step can't tamper with or corrupt the binary the watchdog launches.</summary>
    internal const string AgentFolderName = "agent";

    /// <summary>Subfolder used as the Jenkins agent <c>-workDir</c> (remoting/, workspace/, build output).</summary>
    internal const string WorkFolderName = "work";

    /// <summary>
    /// Returns the absolute data directory (creating it best-effort): the configured value if set
    /// (environment variables expanded), otherwise <c>%ProgramData%\JenkinsAsService</c>.
    /// </summary>
    internal static string ResolveDataDirectory(string? configured)
    {
        var dir = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                AppFolderName)
            : Environment.ExpandEnvironmentVariables(configured.Trim());

        return EnsureDirectory(dir);
    }

    /// <summary>Returns (creating best-effort) the agent-binary cache subfolder under the data directory.</summary>
    internal static string ResolveAgentDirectory(string dataDirectory) =>
        EnsureDirectory(Path.Combine(dataDirectory, AgentFolderName));

    /// <summary>Returns (creating best-effort) the agent work subfolder under the data directory.</summary>
    internal static string ResolveWorkDirectory(string dataDirectory) =>
        EnsureDirectory(Path.Combine(dataDirectory, WorkFolderName));

    private static string EnsureDirectory(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Best-effort: a managed install pre-creates and ACLs this folder, so creation here is a
            // fallback for portable/manual deployments. If it fails, downstream file ops surface the error.
        }

        return dir;
    }
}
