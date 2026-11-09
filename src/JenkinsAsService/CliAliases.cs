// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

/// <summary>
/// Short spellings for long command-line options: <c>--m</c> for <c>--mitigations</c>. The long form is
/// never removed - the MSI custom actions and existing scripts use it - so an alias only adds a spelling.
/// <para>
/// The rule is the shortest unused prefix of the option name, hyphens ignored: the first letter if no
/// earlier option has it, else the first two, else the first three. Allocation follows the order of the
/// list passed in, so earlier options win the shorter forms; reordering the list changes the aliases and
/// is a breaking change for anyone who has typed them.
/// </para>
/// </summary>
internal static class CliAliases
{
    private const int MaxPrefixLength = 3;

    /// <summary>
    /// Maps each long option (<c>--mitigations</c>) to its alias (<c>--m</c>). Throws if an option cannot
    /// get one within three letters, so a new option that collides fails the test run rather than shipping
    /// without a short form.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> Build(IEnumerable<string> longOptions)
    {
        var options = longOptions.ToList();
        var taken = new HashSet<string>(options, StringComparer.Ordinal);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var option in options)
        {
            var letters = option.TrimStart('-').Replace("-", "", StringComparison.Ordinal);
            string? alias = null;
            for (var length = 1; length <= Math.Min(MaxPrefixLength, letters.Length) && alias is null; length++)
            {
                var candidate = "--" + letters[..length];
                if (taken.Add(candidate))
                {
                    alias = candidate;
                }
            }

            map[option] = alias
                ?? throw new InvalidOperationException($"No free alias of up to {MaxPrefixLength} letters for {option}.");
        }

        return map;
    }

    /// <summary>Inverts <see cref="Build"/> so an argument can be resolved to its long form.</summary>
    internal static IReadOnlyDictionary<string, string> Reverse(IReadOnlyDictionary<string, string> aliases) =>
        aliases.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);

    /// <summary>The long form of <paramref name="arg"/> if it is an alias, otherwise <paramref name="arg"/> unchanged.</summary>
    internal static string Resolve(string arg, IReadOnlyDictionary<string, string> reverse) =>
        reverse.TryGetValue(arg, out var longForm) ? longForm : arg;
}
