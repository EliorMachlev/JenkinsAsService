// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

/// <summary>
/// Resolves the writable <em>data</em> directory, kept separate from the read-only install directory
/// (where the signed binary lives). Runtime artifacts the service must write — <c>agent.jar</c>, the
/// ETag cache, log files, the secret file, and the agent work directory — live here so the install
/// folder can stay non-writable by the agent identity, closing the "agent overwrites its own .exe"
/// privilege-escalation vector. Default: <c>%ProgramData%\JenkinsAsService</c> (the MSI grants the
/// service account write on it); override via the <c>DataDirectory</c> setting.
/// </summary>
internal static class DataPaths
{
    internal const string AppFolderName = "JenkinsAsService";

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
