// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

/// <summary>
/// Removes what the MSI cannot: the CA-generated <c>appsettings.json</c> and the runtime data directory.
/// <para>
/// Windows Installer only removes files it installed. <c>appsettings.json</c> is written by the
/// <c>WriteConfig</c> custom action rather than laid down as a tracked file, and everything under the data
/// directory (logs, the cached <c>agent.jar</c>, the work tree) is created at runtime — so an uninstall left
/// all of it on disk, including a file holding the agent secret. This is the other half of the uninstall.
/// </para>
/// <para>
/// Invoked as <c>update-secret --purge</c> from a deferred custom action sequenced before <c>RemoveFiles</c>
/// and conditioned on a genuine uninstall. It must <em>not</em> run during a major upgrade: WiX removes the
/// old product first (<c>Schedule="afterInstallInitialize"</c>), so a purge there would delete the config the
/// upgrade is supposed to reconcile and preserve.
/// </para>
/// </summary>
internal static class UninstallCleanup
{
    /// <summary>
    /// Directory names that must never be deleted as a data directory, even if configured as one. Compared
    /// against the full resolved path, case-insensitively.
    /// </summary>
    private static readonly Environment.SpecialFolder[] ProtectedFolders =
    [
        Environment.SpecialFolder.Windows,
        Environment.SpecialFolder.System,
        Environment.SpecialFolder.SystemX86,
        Environment.SpecialFolder.ProgramFiles,
        Environment.SpecialFolder.ProgramFilesX86,
        Environment.SpecialFolder.CommonApplicationData,
        Environment.SpecialFolder.UserProfile,
    ];

    /// <summary>
    /// Whether <paramref name="path"/> is safe to delete recursively.
    /// <para>
    /// <c>Agent:DataDirectory</c> is operator-supplied, and this runs as SYSTEM during uninstall with a
    /// recursive delete — the one place in this product where a bad config value could take the machine with
    /// it. A path is rejected unless it is absolute, is not a drive root, sits at least two levels below the
    /// root (so <c>C:\Data</c> is refused), and is not a well-known system directory.
    /// </para>
    /// </summary>
    internal static bool IsSafeToDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var trimmed = path.Trim();

        // Must be fully qualified BEFORE resolving. Path.GetFullPath would happily resolve "data\jenkins"
        // against the current directory - which, for a deferred custom action, is whatever msiexec happened
        // to be started from - and the result could then look deep enough to pass every check below.
        // Also rejects the drive-relative "C:dir" form.
        if (!Path.IsPathFullyQualified(trimmed))
        {
            return false;
        }

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root) || string.Equals(full, Path.TrimEndingDirectorySeparator(root),
                StringComparison.OrdinalIgnoreCase))
        {
            return false; // a drive root, or not rooted at all
        }

        // At least two segments below the root: deleting C:\Something wholesale is never what was meant.
        var relative = full[root.Length..].Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries).Length < 2)
        {
            return false;
        }

        foreach (var folder in ProtectedFolders)
        {
            var protectedPath = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(protectedPath)
                && string.Equals(full, Path.TrimEndingDirectorySeparator(protectedPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Deletes the data directory and the generated config. Never throws: an uninstall must complete even if
    /// a log file is still held open, so every failure is reported and swallowed.
    /// </summary>
    /// <param name="basePath">The install folder holding <c>appsettings.json</c>.</param>
    /// <param name="dataDirectory">The resolved data directory, or <c>null</c> to skip it.</param>
    internal static void Purge(string basePath, string? dataDirectory, Action<string> report)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            report("No data directory configured — nothing to remove.");
        }
        else if (!IsSafeToDelete(dataDirectory))
        {
            // Loud, not silent: leaving data behind is recoverable, deleting the wrong tree is not.
            report($"Refusing to delete data directory '{dataDirectory}' — it is a system or root path.");
        }
        else if (Directory.Exists(dataDirectory))
        {
            TryDo(() => Directory.Delete(dataDirectory, recursive: true),
                $"Removed data directory {dataDirectory}", $"Could not remove data directory {dataDirectory}", report);
        }

        var configPath = Path.Combine(basePath, ConfigKeys.FileName);
        if (File.Exists(configPath))
        {
            TryDo(() => File.Delete(configPath),
                $"Removed {configPath}", $"Could not remove {configPath}", report);
        }
    }

    private static void TryDo(Action action, string success, string failure, Action<string> report)
    {
        try
        {
            action();
            report(success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report($"{failure}: {ex.Message}");
        }
    }
}
