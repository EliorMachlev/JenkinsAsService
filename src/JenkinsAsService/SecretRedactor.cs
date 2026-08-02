// Copyright (c) 2024 All rights reserved

using System.Text;

namespace JenkinsAsService;

/// <summary>
/// Builds the set of strings that must never reach a log sink, and scrubs them out of a line.
/// <para>
/// Redacting only the literal secret is not enough: the value passes through a URL, a JVM argument and the
/// controller's own error messages before it comes back to us on the agent's stderr, and any of those can
/// re-encode it. A percent-encoded or HTML-escaped secret in a log file is just as disclosed as a plain one,
/// so every form we can predict is replaced.
/// </para>
/// </summary>
internal static class SecretRedactor
{
    internal const string Replacement = "*****";

    /// <summary>
    /// Encoded forms of <paramref name="secret"/> to scrub, longest first.
    /// <para>
    /// Order matters. When one form contains another (a secret with no special characters encodes to itself,
    /// and Base64 output can embed the original), replacing the longest first stops a shorter match from
    /// carving up a longer one and leaving recognisable fragments behind.
    /// </para>
    /// Returns an empty array for an empty secret, so callers need no null-check of their own.
    /// </summary>
    internal static string[] BuildPatterns(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return [];
        }

        var forms = new HashSet<string>(StringComparer.Ordinal)
        {
            secret,
            Uri.EscapeDataString(secret),                       // %-encoded in a URL or form body
            secret.Replace("&", "&amp;", StringComparison.Ordinal)
                  .Replace("<", "&lt;", StringComparison.Ordinal)
                  .Replace(">", "&gt;", StringComparison.Ordinal), // XML/HTML-escaped in a controller error page
            Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)),
        };

        return [.. forms.OrderByDescending(f => f.Length)];
    }

    /// <summary>
    /// Replaces every pattern in <paramref name="patterns"/> found in <paramref name="line"/> with
    /// <see cref="Replacement"/>. Ordinal comparison — a secret is case-sensitive, and a case-insensitive
    /// scrub would mangle unrelated text that merely differs in case.
    /// </summary>
    internal static string Redact(string line, IReadOnlyList<string> patterns)
    {
        if (string.IsNullOrEmpty(line) || patterns.Count == 0)
        {
            return line;
        }

        foreach (var pattern in patterns)
        {
            if (pattern.Length > 0)
            {
                line = line.Replace(pattern, Replacement, StringComparison.Ordinal);
            }
        }

        return line;
    }
}
