// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

/// <summary>
/// Deny-by-default scrub of a child process environment block: keeps only a curated allow-list of
/// variables the JVM and common Windows build tooling need, plus a caller-supplied extra list. Prevents
/// the service's own environment (which may carry host secrets) from leaking into untrusted pipeline
/// scripts that run inside the agent.
/// </summary>
internal static class EnvironmentSanitizer
{
    private static readonly char[] Separators = [';', ','];

    // Covers what the JVM and common Windows build tooling need; everything else is stripped. Extend
    // per-deployment via ServiceSettings.Hardening.AllowedEnvironmentVariables rather than editing this list.
    private static readonly string[] DefaultAllowlist =
    [
        "SystemRoot", "windir", "SystemDrive", "ComSpec",
        "PATH", "PATHEXT",
        "TEMP", "TMP",
        "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER", "OS", "COMPUTERNAME",
        "USERNAME", "USERPROFILE", "USERDOMAIN", "HOMEDRIVE", "HOMEPATH",
        "APPDATA", "LOCALAPPDATA", "ProgramData",
        "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "CommonProgramFiles", "CommonProgramFiles(x86)",
        "JAVA_HOME",
    ];

    /// <summary>
    /// Removes every variable from <paramref name="environment"/> that is not in <see cref="DefaultAllowlist"/>
    /// or the caller-supplied <paramref name="extraAllowed"/> (semicolon/comma-separated, case-insensitive).
    /// </summary>
    internal static void Apply(IDictionary<string, string?> environment, string? extraAllowed)
    {
        var allow = new HashSet<string>(DefaultAllowlist, StringComparer.OrdinalIgnoreCase);
        foreach (var name in SplitAllowList(extraAllowed))
        {
            allow.Add(name);
        }

        foreach (var key in environment.Keys.ToList())
        {
            if (!allow.Contains(key))
            {
                environment.Remove(key);
            }
        }
    }

    private static IEnumerable<string> SplitAllowList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
