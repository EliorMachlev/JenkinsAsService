// Copyright (c) 2024 All rights reserved

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;

namespace JenkinsAsService.Tests;

public class CertificateThumbprintValidatorTests
{
    [Theory]
    [InlineData("AB:CD:EF", "ABCDEF")]
    [InlineData("ab cd ef", "ABCDEF")]
    [InlineData("ab-cd-ef", "ABCDEF")]
    [InlineData("  ABCDEF  ", "ABCDEF")]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void Normalize_strips_separators_and_uppercases(string? input, string expected)
    {
        CertificateThumbprintValidator.Normalize(input).Should().Be(expected);
    }

    [Fact]
    public void Matches_returns_true_for_correct_sha256_thumbprint()
    {
        using var cert = CreateSelfSignedCert();
        var thumbprint = cert.GetCertHashString(HashAlgorithmName.SHA256);

        CertificateThumbprintValidator.Matches(cert, thumbprint).Should().BeTrue();
    }

    [Fact]
    public void Matches_is_case_and_separator_insensitive()
    {
        using var cert = CreateSelfSignedCert();
        var thumbprint = cert.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant();
        var colonized = string.Join(":", Enumerable.Range(0, thumbprint.Length / 2)
            .Select(i => thumbprint.Substring(i * 2, 2)));

        CertificateThumbprintValidator.Matches(cert, colonized).Should().BeTrue();
    }

    [Fact]
    public void Matches_returns_false_for_wrong_thumbprint()
    {
        using var cert = CreateSelfSignedCert();

        CertificateThumbprintValidator.Matches(cert, "00112233445566778899AABBCCDDEEFF").Should().BeFalse();
    }

    [Fact]
    public void Matches_returns_false_for_null_certificate()
    {
        CertificateThumbprintValidator.Matches(null, "ABCDEF").Should().BeFalse();
    }

    [Fact]
    public void Matches_returns_false_when_expected_is_empty()
    {
        using var cert = CreateSelfSignedCert();

        CertificateThumbprintValidator.Matches(cert, "").Should().BeFalse();
        CertificateThumbprintValidator.Matches(cert, null).Should().BeFalse();
    }

    private static X509Certificate2 CreateSelfSignedCert()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=jenkins-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
