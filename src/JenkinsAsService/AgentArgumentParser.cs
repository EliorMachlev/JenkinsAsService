// Copyright (c) 2024 All rights reserved

using System.Text;

namespace JenkinsAsService;

/// <summary>
/// Splits a single command-line string into individual arguments, honouring double-quoted spans (which
/// may contain spaces) and backslash-escaped quotes (<c>\"</c>). Used to parse the user-supplied
/// <see cref="AgentSettings.CustomArguments"/> and <see cref="AgentSettings.JvmArguments"/> before they are added to the Java agent command line.
/// A quote may open a span anywhere in a token (<c>-Dx="a b"</c> is one argument, <c>-Dx=a b</c>).
/// </summary>
internal static class AgentArgumentParser
{
    private const int EscapedQuoteWidth = 2;

    internal static List<string> Parse(string? input)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(input))
        {
            return result;
        }

        var buf = new StringBuilder();
        var inToken = false; // true once a char or an opening quote was seen (so "" yields an empty arg)
        var inQuotes = false;

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (inQuotes && c == '\\' && i + 1 < input.Length && input[i + 1] == '"')
            {
                buf.Append('"');
                i += EscapedQuoteWidth - 1;
            }
            else if (c == '"')
            {
                // A quote opens or closes a span anywhere in a token, not only at its start:
                // -Dx="C:\Program Files\a.jks" is one argument, -Dx=C:\Program Files\a.jks.
                inQuotes = !inQuotes;
                inToken = true;
            }
            else if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (inToken)
                {
                    result.Add(buf.ToString());
                    buf.Clear();
                    inToken = false;
                }
            }
            else
            {
                buf.Append(c);
                inToken = true;
            }
        }

        if (inToken)
        {
            result.Add(buf.ToString());
        }

        return result;
    }
}
