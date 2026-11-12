// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

/// <summary>
/// The CLI verbs <c>Program</c> dispatches before it falls through to service mode.
/// <para>
/// Extracted from <c>Program.cs</c> so it can be tested: a verb that exists but is never registered here
/// still compiles, and its only symptom is the exe silently starting in service mode instead — which for
/// an MSI custom action means a failed install. <c>CliCommandsTests</c> asserts by reflection that every
/// command type is present, so adding one and forgetting this table fails the build's test run.
/// </para>
/// </summary>
internal static class CliCommands
{
    /// <summary>Verb → handler. Ordinal matching: these are argv tokens, not user-facing text.</summary>
    internal static readonly IReadOnlyDictionary<string, Func<string[], int>> All =
        new Dictionary<string, Func<string[], int>>(StringComparer.Ordinal)
        {
            [UpdateSecretCommand.Name] = UpdateSecretCommand.Run,
            // Lambdas rather than method groups: these Run methods take an optional basePath, and a method
            // group cannot convert to Func<string[], int> while omitting an optional parameter.
            [PurgeCommand.Name] = args => PurgeCommand.Run(args),
            [GrantDataAccessCommand.Name] = args => GrantDataAccessCommand.Run(args),
            [ConfigureRecoveryCommand.Name] = ConfigureRecoveryCommand.Run,
        };

    /// <summary>
    /// Strips the quotes an MSI property arrives wrapped in. Custom-action commands are authored as
    /// <c>--account "[SERVICE_ACCOUNT]"</c> so an empty or space-bearing property cannot swallow the next
    /// flag, and CommandLineToArgvW does not always strip the pair cleanly.
    /// </summary>
    internal static string TrimQuoted(string value) => value.Trim().Trim('"').Trim();

    /// <summary>
    /// True when the caller asked for usage text rather than an action. A help flag counts at any
    /// position except as the value of an option that takes free-form text: <c>--custom-args "--h"</c>
    /// passes <c>--h</c> to the agent instead of cancelling the run (a silent install would otherwise print
    /// usage, exit 0 and write nothing - reported as success). A value that can never be a flag - a URL, a
    /// mode, a path - is not skipped, so <c>--url --help</c> still asks for help.
    /// </summary>
    /// <param name="args">The command line, verb included.</param>
    /// <param name="takesValue">
    /// Whether an argument is an option whose next argument is free-form, so it must not be read as a
    /// flag. Null for a command with no such option. Pass the same resolution the command's parser uses,
    /// aliases included.
    /// </param>
    internal static bool WantsHelp(string[] args, Func<string, bool>? takesValue = null)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--help" or "--h" or "-h")
            {
                return true;
            }

            if (takesValue?.Invoke(args[i]) == true)
            {
                i++; // the value, skipped unread
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves <paramref name="args"/>[0] to a handler. False means service mode.
    /// </summary>
    internal static bool TryResolve(string[] args, out Func<string[], int> command)
    {
        if (args.Length > 0)
        {
            return All.TryGetValue(args[0], out command!);
        }

        command = null!;
        return false;
    }
}
