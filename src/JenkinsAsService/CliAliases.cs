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
    /// <param name="longOptions">Options that get a short alias.</param>
    /// <param name="unaliased">
    /// Options that keep their full name only, because a short form is too easy to get wrong for what they
    /// carry - <c>update-secret</c>'s <c>--secret*</c> family, where <c>--se</c> and <c>--sec</c> once meant
    /// two different secret sources. Their names <em>and every prefix an alias could take</em> are reserved,
    /// so no other option inherits <c>--s</c>/<c>--se</c>/<c>--sec</c> either: a mistyped secret option then
    /// fails as unknown instead of silently feeding the secret to an unrelated option.
    /// </param>
    internal CliAliases(IEnumerable<string> longOptions, IEnumerable<string>? unaliased = null)
    {
        var options = longOptions.ToList();
        var fullNameOnly = unaliased?.ToList() ?? [];

        // Long options are reserved up front so an alias can never shadow one (a three-letter "--url").
        var taken = new HashSet<string>(options.Concat(fullNameOnly), StringComparer.Ordinal);
        foreach (var option in fullNameOnly)
        {
            foreach (var prefix in Candidates(option))
            {
                taken.Add(prefix);
            }
        }

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

    private static string? Allocate(string option, HashSet<string> taken) =>
        Candidates(option).FirstOrDefault(taken.Add);

    // The aliases an option could take, shortest first: "--secret-env" -> --s, --se, --sec.
    private static IEnumerable<string> Candidates(string option)
    {
        var letters = option.TrimStart('-').Replace("-", "", StringComparison.Ordinal);
        for (var length = 1; length <= Math.Min(MaxPrefixLength, letters.Length); length++)
        {
            yield return "--" + letters[..length];
        }
    }
}
