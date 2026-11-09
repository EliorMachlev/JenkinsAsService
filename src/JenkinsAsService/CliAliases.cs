// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

/// <summary>
/// Short spellings for a command's long options: <c>--m</c> for <c>--mitigations</c>. The long form is
/// never removed - the MSI custom actions and existing scripts use it - so an alias only adds a spelling.
/// <para>
/// The rule is the shortest unused prefix of the option name, hyphens ignored: the first letter if no
/// earlier option has it, else the first two, else the first three. Allocation follows the order of the
/// list passed in, so earlier options win the shorter forms; reordering the list changes the aliases and
/// is a breaking change for anyone who has typed them.
/// </para>
/// </summary>
internal sealed class CliAliases
{
    private const int MaxPrefixLength = 3;

    private readonly Dictionary<string, string> _aliasByOption = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _optionByAlias = new(StringComparer.Ordinal);

    /// <summary>
    /// Allocates an alias for each option. Throws if one cannot get one within three letters, so a new
    /// option that collides fails the test run rather than shipping without a short form.
    /// </summary>
    internal CliAliases(IEnumerable<string> longOptions)
    {
        var options = longOptions.ToList();

        // Long options are reserved up front so an alias can never shadow one (a three-letter "--url").
        var taken = new HashSet<string>(options, StringComparer.Ordinal);

        foreach (var option in options)
        {
            var alias = Allocate(option, taken)
                ?? throw new InvalidOperationException($"No free alias of up to {MaxPrefixLength} letters for {option}.");
            _aliasByOption[option] = alias;
            _optionByAlias[alias] = option;
        }
    }

    /// <summary>The alias for a long option, e.g. <c>--m</c> for <c>--mitigations</c>.</summary>
    internal string this[string longOption] => _aliasByOption[longOption];

    /// <summary>Every long option with its alias, in allocation order.</summary>
    internal IReadOnlyDictionary<string, string> All => _aliasByOption;

    /// <summary>The long form of <paramref name="arg"/> if it is an alias, otherwise <paramref name="arg"/> unchanged.</summary>
    internal string Resolve(string arg) => _optionByAlias.GetValueOrDefault(arg, arg);

    private static string? Allocate(string option, HashSet<string> taken)
    {
        var letters = option.TrimStart('-').Replace("-", "", StringComparison.Ordinal);
        for (var length = 1; length <= Math.Min(MaxPrefixLength, letters.Length); length++)
        {
            var candidate = "--" + letters[..length];
            if (taken.Add(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
