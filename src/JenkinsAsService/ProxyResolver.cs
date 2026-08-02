// Copyright (c) 2024 All rights reserved

using System.Net;

namespace JenkinsAsService;

/// <summary>What <c>Connection:Proxy</c> asks the <c>agent.jar</c> download to do.</summary>
internal enum ProxyMode
{
    /// <summary>Inherit the machine/WinHTTP proxy configuration — the .NET default, and the default here.</summary>
    System,

    /// <summary>Bypass any configured proxy and connect directly.</summary>
    Direct,

    /// <summary>Use the explicitly configured proxy address.</summary>
    Explicit
}

/// <summary>
/// Turns the <c>Connection:Proxy</c> / <c>Connection:ProxyBypass</c> strings into an <see cref="IWebProxy"/>.
/// <para>
/// Split out from the HTTP wiring so the parsing rules are unit-testable: this is configuration a build node
/// behind a corporate proxy depends on, and a silently-misparsed value looks exactly like an unreachable
/// controller.
/// </para>
/// <para>
/// Only the .NET-side <c>agent.jar</c> download is affected. The Java agent's own control-channel connection
/// is the JVM's business and needs <c>-Dhttps.proxyHost=...</c> style flags via <c>Agent:CustomArguments</c>.
/// </para>
/// </summary>
internal static class ProxyResolver
{
    private const string DirectKeyword = "direct";
    private const string NoneKeyword = "none";
    private static readonly char[] BypassSeparators = [';', ','];

    /// <summary>
    /// Classifies the configured value: empty/whitespace &rarr; <see cref="ProxyMode.System"/>,
    /// <c>direct</c>/<c>none</c> (case-insensitive) &rarr; <see cref="ProxyMode.Direct"/>, anything else
    /// &rarr; <see cref="ProxyMode.Explicit"/>.
    /// </summary>
    internal static ProxyMode ClassifyMode(string? proxy)
    {
        if (string.IsNullOrWhiteSpace(proxy))
        {
            return ProxyMode.System;
        }

        var trimmed = proxy.Trim();
        return trimmed.Equals(DirectKeyword, StringComparison.OrdinalIgnoreCase)
               || trimmed.Equals(NoneKeyword, StringComparison.OrdinalIgnoreCase)
            ? ProxyMode.Direct
            : ProxyMode.Explicit;
    }

    /// <summary>
    /// Normalizes an explicit proxy address to an absolute URI. A bare <c>host:port</c> is accepted and
    /// given the <c>http</c> scheme, because that is how proxies are conventionally written (and how the
    /// <c>HTTP_PROXY</c> environment variable is usually set) &mdash; requiring a scheme would reject the
    /// form most operators type.
    /// </summary>
    /// <exception cref="InvalidOperationException">The address cannot be parsed as a proxy URI.</exception>
    internal static Uri ParseAddress(string address)
    {
        var trimmed = address.Trim();
        var candidate = trimmed.Contains("://", StringComparison.Ordinal) ? trimmed : "http://" + trimmed;

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            throw new InvalidOperationException(
                $"Connection:Proxy is not a valid proxy address: '{address}'. " +
                "Use host:port, or a full URL such as http://proxy.example.com:8080. " +
                "Leave it empty to use the system proxy, or set it to 'direct' to bypass proxies entirely.");
        }

        return uri;
    }

    /// <summary>Splits the semicolon/comma-separated bypass list, dropping blanks.</summary>
    internal static string[] ParseBypassList(string? bypass) =>
        string.IsNullOrWhiteSpace(bypass)
            ? []
            : [.. bypass.Split(BypassSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>
    /// The proxy to attach to the download handler, or <c>null</c> to leave the handler's default (system)
    /// behaviour alone. <see cref="ProxyMode.Direct"/> returns an empty <see cref="WebProxy"/>, which is how
    /// .NET expresses "no proxy" &mdash; the caller must still disable proxy use for a true bypass.
    /// </summary>
    internal static IWebProxy? Create(string? proxy, string? bypass)
    {
        switch (ClassifyMode(proxy))
        {
            case ProxyMode.System:
            case ProxyMode.Direct:
                return null;
            default:
                var address = ParseAddress(proxy!);
                var result = new WebProxy(address) { BypassProxyOnLocal = true };
                var list = ParseBypassList(bypass);
                if (list.Length > 0)
                {
                    // WebProxy treats these as regular expressions, not wildcards. Operators write
                    // "*.corp.local" out of habit, so translate that shape rather than silently never matching.
                    result.BypassList = [.. list.Select(ToBypassRegex)];
                }

                return result;
        }
    }

    /// <summary>
    /// Converts a host-shaped bypass entry to the regular expression <see cref="WebProxy.BypassList"/> expects.
    /// <para>
    /// Two traps are handled here. <see cref="WebProxy.BypassList"/> holds <em>regular expressions</em>, not
    /// wildcards, and it matches them against the <strong>whole URI</strong> (<c>https://host:8443/path</c>) —
    /// not the host alone. A naive <c>^*.corp.local$</c> therefore never matches anything. The produced pattern
    /// wraps the host in the URI shape and anchors both ends, so <c>*.corp.local</c> matches
    /// <c>https://jenkins.corp.local:8443/</c> but not <c>https://evil-corp.local.example.com/</c>.
    /// </para>
    /// </summary>
    internal static string ToBypassRegex(string entry)
    {
        // Escape first, then reinstate the wildcard as "any run of non-separator characters", so a '*' is the
        // only metacharacter an operator can use and a literal '.' cannot match an arbitrary character.
        var host = System.Text.RegularExpressions.Regex
            .Escape(entry.Trim())
            .Replace(@"\*", "[^/]*", StringComparison.Ordinal);

        return $"^[^:]+://{host}(:[0-9]+)?(/.*)?$";
    }
}
