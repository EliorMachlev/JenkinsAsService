// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

/// <summary>
/// The <c>purge</c> CLI command: removes the runtime data directory and the generated
/// <c>appsettings.json</c>.
/// <para>
/// A top-level verb rather than a flag on <c>update-secret</c>. It updates no secret — it deletes the config
/// and the data folder — and a destructive command must not be spelled as a modifier of an unrelated,
/// non-destructive one. The MSI's uninstall custom action invokes it; a manual (non-MSI) install has no
/// custom action to run the cleanup, so operators call it directly.
/// </para>
/// </summary>
internal static class PurgeCommand
{
    internal const string Name = "purge";

    private const string UsageText = """
        Usage: JenkinsAsService purge

        Removes this installation's runtime data directory and appsettings.json.

        Windows Installer removes only files it installed. appsettings.json is written by a custom action
        and the data directory is created at runtime, so neither is a tracked MSI file and an uninstall
        cannot remove them on its own. The MSI runs this command for you; run it by hand after a manual
        (non-MSI) removal.

        DESTRUCTIVE and unprompted. Deletes, from the data directory:
          * the logs (agent.log / agent.clef)
          * the cached agent.jar and its integrity sidecars
          * the work\ tree - build workspaces and remoting state
        and appsettings.json, which may hold the (protected) agent secret. Copy out anything you need first.

        The data directory is read from appsettings.json, so a relocated Agent:DataDirectory is honoured.
        Paths that are not fully qualified, drive roots, and well-known system directories are refused.

        Options:
          --help                  Show this help
        """;

    /// <summary>Runs the command. Returns a process exit code.</summary>
    internal static int Run(string[] args, string? basePath = null)
    {
        if (CliCommands.WantsHelp(args))
        {
            Console.WriteLine(UsageText);
            return 0;
        }

        basePath ??= AppContext.BaseDirectory;

        // Read everything needed BEFORE anything is deleted. The config is both the map (where the data
        // directory is, which store holds the secret) and one of the things being removed.
        var settings = ReadSettings(basePath);

        var outcome = SecretPurger.Purge(settings.Mode, settings.SecretValue, Console.WriteLine);
        UninstallCleanup.Purge(basePath, DataPaths.ComputeDataDirectory(settings.DataDirectory), Console.WriteLine);

        if (outcome == SecretStoreOutcome.Failed)
        {
            // Said once more at the end, because by now it has scrolled past several lines of file deletions
            // and it is the only part of the uninstall that leaves something behind for a human to do.
            Console.WriteLine(
                $"WARNING: the secret could not be removed from its {settings.Mode} store and is still on this machine.");
        }

        // Still 0: the custom action runs with Return="ignore" and a failed secret removal must not fail the
        // uninstall. The exit code is for humans and scripts invoking this directly after a manual removal.
        return outcome == SecretStoreOutcome.Failed ? SecretPurgeIncompleteExitCode : 0;
    }

    /// <summary>
    /// Non-zero, but distinct from a general error: everything else was removed and only the external secret
    /// store survived. A script can treat it as "needs attention" without treating it as "purge did nothing".
    /// </summary>
    internal const int SecretPurgeIncompleteExitCode = 2;

    /// <summary>What the purge needs out of the config before it deletes it.</summary>
    private readonly record struct PurgeSettings(SecretMode Mode, string? SecretValue, string? DataDirectory);

    /// <summary>
    /// Reads the data directory and the secret's storage details. An operator who moved
    /// <c>Agent:DataDirectory</c> must have <em>their</em> directory removed, not the default, and for the
    /// pointer-style secret modes <c>Secret:Value</c> names the environment variable or credential target.
    /// A missing or unreadable config falls back to defaults, which is where those artifacts would be anyway.
    /// </summary>
    private static PurgeSettings ReadSettings(string basePath)
    {
        var jenkins = InstalledConfig.TryReadJenkinsSection(
            basePath,
            error => Console.WriteLine(
                $"{error} Using defaults. A secret held outside the config may need removing by hand."));

        if (jenkins is null)
        {
            return default;
        }

        _ = Enum.TryParse<SecretMode>(jenkins[ConfigKeys.Secret.ModePath], ignoreCase: true, out var mode);

        return new PurgeSettings(
            mode,
            jenkins[ConfigKeys.Secret.ValuePath],
            jenkins[ConfigKeys.Agent.DataDirectoryPath]);
    }
}
