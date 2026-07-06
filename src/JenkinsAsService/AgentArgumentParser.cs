// Copyright (c) 2024 All rights reserved

using System.Text;

namespace JenkinsAsService;

/// <summary>
/// Splits a single command-line string into individual arguments, honouring double-quoted spans (which
/// may contain spaces) and backslash-escaped quotes (<c>\"</c>). Used to parse the user-supplied
/// <see cref="AgentSettings.CustomArguments"/> before they are appended to the Java agent command line.
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

        var i = 0;

        while (i < input.Length)
        {
            while (i < input.Length && char.IsWhiteSpace(input[i]))
            {
                i++;
            }

            if (i < input.Length)
            {
                if (input[i] == '"')
                {
                    i++; // skip opening quote
                    result.Add(ParseQuotedArg(input, ref i));
                }
                else
                {
                    result.Add(ParseUnquotedArg(input, ref i));
                }
            }
        }

        return result;
    }

    private static string ParseQuotedArg(string input, ref int i)
    {
        var buf = new StringBuilder();
        while (i < input.Length && input[i] != '"')
        {
            if (input[i] == '\\' && i + 1 < input.Length && input[i + 1] == '"')
            {
                buf.Append('"');
                i += EscapedQuoteWidth;
            }
            else
            {
                buf.Append(input[i]);
                i++;
            }
        }

        if (i < input.Length)
        {
            i++; // skip closing quote
        }

        return buf.ToString();
    }

    private static string ParseUnquotedArg(string input, ref int i)
    {
        var end = i;
        while (end < input.Length && !char.IsWhiteSpace(input[end]))
        {
            end++;
        }

        var token = input[i..end];
        i = end;
        return token;
    }
}
