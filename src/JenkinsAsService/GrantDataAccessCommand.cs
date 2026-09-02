// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

/// <summary>
/// The <c>grant-data-access</c> CLI command: gives the service account write access to the runtime data
/// directory. A verb because the MSI invokes it as an EXE custom action (see the installer .wixproj), and
/// because an operator needs it after a manual install or after changing the service identity by hand.
/// </summary>
internal static class GrantDataAccessCommand
{
    internal const string Name = "grant-data-access";

    private const string UsageText = """
        Usage: JenkinsAsService grant-data-access --path <directory> --account <account>

        Grants <account> Modify access to <directory>, inheritable by its files and subfolders.

        The runtime data directory is the writable half of the install: agent.jar and its integrity
        sidecars, the logs, the secret file and the agent -workDir all live there, while the install
        folder stays read-only so a pipeline running as the agent cannot overwrite the service binary.

        Existing entries and the folder's inheritance are preserved, so it keeps the SYSTEM and
        Administrators full control it inherits from its parent. Re-running is safe: the account's own
        entry is replaced rather than duplicated.

        Options:
          --path <directory>      Directory to grant access to (default: the configured data directory)
          --account <account>     Account to grant, e.g. "NT SERVICE\\Jenkins"
          --help                  Show this help
        """;

    /// <summary>Runs the command. Returns a process exit code.</summary>
    internal static int Run(string[] args, string? basePath = null)
    {
        if (Array.Exists(args, a => a is "--help" or "-h"))
        {
            Console.WriteLine(UsageText);
            return 0;
        }

        var (path, account, error) = ParseArguments(args);
        if (error is not null)
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        // Honours a relocated Agent:DataDirectory the same way purge does.
        path ??= DataPaths.ComputeDataDirectory(ReadConfiguredDataDirectory(basePath ?? AppContext.BaseDirectory));

        return DataDirectoryAcl.Grant(path, account!, Console.WriteLine) ? 0 : 1;
    }

    /// <summary>
    /// Parses <c>--path</c> and <c>--account</c>. The account has no default: falling back to a built-in
    /// name would grant the wrong identity on any install that overrode SERVICE_ACCOUNT.
    /// </summary>
    internal static (string? Path, string? Account, string? Error) ParseArguments(string[] args)
    {
        string? path = null;
        string? account = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case Name:
                    break;
                case "--path" when i + 1 < args.Length:
                    path = UpdateSecretCommand.SanitizePathArgument(args[++i]);
                    break;
                case "--account" when i + 1 < args.Length:
                    account = args[++i].Trim().Trim('"').Trim();
                    break;
                case "--path":
                case "--account":
                    return (null, null, $"Error: {args[i]} requires a value.");
                default:
                    return (null, null, $"Error: unknown argument '{args[i]}'.");
            }
        }

        return string.IsNullOrWhiteSpace(account)
            ? (null, null, "Error: --account is required.")
            : (path, account, null);
    }

    private static string? ReadConfiguredDataDirectory(string basePath)
    {
        try
        {
            return new ConfigurationBuilder()
                .SetBasePath(basePath)
                .AddJsonFile(ConfigKeys.FileName, optional: true)
                .Build()
                .GetSection(ConfigKeys.Section)[ConfigKeys.Agent.DataDirectoryPath];
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Could not read {ConfigKeys.FileName} ({ex.Message}); using the default data directory.");
            return null;
        }
    }
}
