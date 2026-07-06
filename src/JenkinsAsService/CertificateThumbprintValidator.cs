// Copyright (c) 2024 All rights reserved

using System.Security.Cryptography.X509Certificates;

namespace JenkinsAsService;

/// <summary>
/// Validates a server certificate against an administrator-supplied SHA-256 thumbprint (certificate
/// pinning). Used by the agent.jar HttpClient so a MITM presenting a chain-trusted-but-different
/// certificate (e.g. a rogue internal CA) is still rejected.
/// </summary>
/// <remarks>
/// This only pins the .NET agent.jar download. The Java agent's own control-channel TLS is handled
/// by the JVM — to pin that connection, pass the controller certificate to the agent via
/// <c>CustomArguments</c> (<c>-cert &lt;PEM&gt;</c>).
/// </remarks>
public static class CertificateThumbprintValidator
{
    /// <summary>
    /// Normalizes a thumbprint to uppercase hex with all separators (colons, spaces, dashes) removed.
    /// Returns an empty string for null/whitespace input.
    /// </summary>
    public static string Normalize(string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            return string.Empty;
        }

        Span<char> buffer = thumbprint.Length <= 128 ? stackalloc char[thumbprint.Length] : new char[thumbprint.Length];
        var length = 0;
        foreach (var c in thumbprint)
        {
            if (Uri.IsHexDigit(c))
            {
                buffer[length++] = char.ToUpperInvariant(c);
            }
        }

        return new string(buffer[..length]);
    }

    /// <summary>
    /// Returns <c>true</c> if <paramref name="certificate"/>'s SHA-256 thumbprint equals the
    /// normalized <paramref name="expectedThumbprint"/>. A null certificate never matches.
    /// </summary>
    public static bool Matches(X509Certificate2? certificate, string? expectedThumbprint)
    {
        if (certificate is null)
        {
            return false;
        }

        var expected = Normalize(expectedThumbprint);
        if (expected.Length == 0)
        {
            return false;
        }

        var actual = Normalize(certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256));
        return string.Equals(expected, actual, StringComparison.Ordinal);
    }
}
