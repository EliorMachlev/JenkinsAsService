// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

/// <summary>
/// Validates the mandatory <see cref="ServiceSettings"/> required to start the agent: the controller URL
/// (present, parseable, with an explicit non-default port) and a non-empty secret.
/// </summary>
internal static class ServiceSettingsValidator
{
    internal static void Validate(ServiceSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Connection.Url))
        {
            throw new InvalidOperationException("'Connection:Url' is a mandatory field.");
        }

        if (string.IsNullOrWhiteSpace(settings.Secret.Value))
        {
            throw new InvalidOperationException("'Secret:Value' is a mandatory field.");
        }

        // Validate the URL format up front with a friendly message (consistent with the other checks),
        // rather than letting a raw UriFormatException surface later from the worker/HTTP client.
        if (!Uri.TryCreate(settings.Connection.Url, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException(
                $"Connection:Url is not a valid absolute URL: '{settings.Connection.Url}'. " +
                "Example: https://jenkins.example.com:8443");
        }

        if (!HasExplicitPort(settings.Connection.Url))
        {
            throw new InvalidOperationException(
                $"Connection:Url must include an explicit port: '{settings.Connection.Url}'. " +
                "Example: https://jenkins.example.com:8443");
        }
    }

    // Uri normalizes away a scheme-default port, so uri.IsDefaultPort can't tell "https://h:443" (a valid
    // reverse-proxied controller) from "https://h" (port omitted). Inspect the authority of the original
    // string: a colon after the host — outside any IPv6 [...] literal — means the port was written explicitly.
    private static bool HasExplicitPort(string url)
    {
        var schemeIdx = url.IndexOf("://", StringComparison.Ordinal);
        var authorityStart = schemeIdx >= 0 ? schemeIdx + 3 : 0;
        var authorityEnd = url.IndexOfAny(['/', '?', '#'], authorityStart);
        var authority = authorityEnd >= 0
            ? url[authorityStart..authorityEnd]
            : url[authorityStart..];

        var closeBracket = authority.LastIndexOf(']'); // end of an IPv6 literal, if any
        var searchFrom = closeBracket >= 0 ? closeBracket + 1 : 0;
        var colon = authority.IndexOf(':', searchFrom);
        return colon >= 0 && colon < authority.Length - 1;
    }
}
