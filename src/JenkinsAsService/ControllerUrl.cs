// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

/// <summary>
/// The one statement of what a usable <c>Connection:Url</c> is: an absolute URL whose port is written out.
/// Shared by <see cref="ServiceSettingsValidator"/> (service start) and <see cref="UpdateSecretVerifier"/>
/// (<c>update-secret</c>, before the value is written), so the CLI cannot accept a URL the service then
/// refuses at startup, or the other way round.
/// </summary>
internal static class ControllerUrl
{
    /// <summary>Shown after either rejection, so the operator sees the expected shape.</summary>
    internal const string Example = "https://jenkins.example.com:8443";

    internal enum Problem
    {
        None,
        NotAbsolute,
        NoExplicitPort
    }

    /// <summary>
    /// Checks <paramref name="url"/> against the rule. A null or blank value is <see cref="Problem.NotAbsolute"/>;
    /// callers that treat blank as "missing" check that first, with their own message.
    /// </summary>
    internal static Problem Check(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            return Problem.NotAbsolute;
        }

        return HasExplicitPort(url) ? Problem.None : Problem.NoExplicitPort;
    }

    /// <summary>The reason, phrased to follow the setting or option name: "Connection:Url {reason}".</summary>
    internal static string Describe(Problem problem) => problem switch
    {
        Problem.NotAbsolute => "is not a valid absolute URL",
        Problem.NoExplicitPort => "must include an explicit port",
        _ => "is valid"
    };

    // Uri normalizes away a scheme-default port, so uri.IsDefaultPort can't tell "https://h:443" (a valid
    // reverse-proxied controller) from "https://h" (port omitted). Inspect the authority of the original
    // string: a colon after the host — outside any IPv6 [...] literal — means the port was written explicitly.
    internal static bool HasExplicitPort(string url)
    {
        var authority = Authority(url, out _);

        var closeBracket = authority.LastIndexOf(']'); // end of an IPv6 literal, if any
        var searchFrom = closeBracket >= 0 ? closeBracket + 1 : 0;
        var colon = authority.IndexOf(':', searchFrom);
        return colon >= 0 && colon < authority.Length - 1;
    }

    /// <summary>
    /// <paramref name="url"/> without its userinfo (<c>https://user:pass@host:8443/</c> becomes
    /// <c>https://host:8443/</c>), for messages. A URL is not a secret and is printed; credentials embedded in
    /// one are, and are not. Textual rather than through <see cref="Uri"/>, so a URL that does not even parse
    /// - which is exactly when it gets reported - is still stripped, and is otherwise shown as typed.
    /// <para>
    /// Everything from the start of the authority up to the LAST <c>@</c> in the whole string is dropped, not
    /// just userinfo inside a well-formed authority: a mistyped URL can carry credentials where no parser would
    /// find them (<c>https://alice/pass@host:8443</c>, <c>not-a-url/user:pass@host</c>). When that <c>@</c> is
    /// outside the authority the cut is marked <c>***@</c>, since it may have taken more than userinfo - a
    /// legitimate <c>@</c> in a path is over-redacted, which costs only some of a diagnostic.
    /// </para>
    /// </summary>
    internal static string WithoutUserInfo(string url)
    {
        var authority = Authority(url, out var authorityStart);
        var at = url.LastIndexOf('@');
        if (at < 0)
        {
            return url;
        }

        if (at < authorityStart)
        {
            return string.Concat("***@", url.AsSpan(at + 1)); // '@' before any "://": nothing parses, hide it all
        }

        var marker = at < authorityStart + authority.Length ? "" : "***@";
        return string.Concat(url.AsSpan(0, authorityStart), marker, url.AsSpan(at + 1));
    }

    // The authority of a URL as written: after "scheme://", up to the first '/', '?' or '#'.
    private static string Authority(string url, out int start)
    {
        var schemeIdx = url.IndexOf("://", StringComparison.Ordinal);
        start = schemeIdx >= 0 ? schemeIdx + 3 : 0;
        var end = url.IndexOfAny(['/', '?', '#'], start);
        return end >= 0 ? url[start..end] : url[start..];
    }
}
