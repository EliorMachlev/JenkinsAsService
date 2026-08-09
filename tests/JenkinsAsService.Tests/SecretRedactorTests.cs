// Copyright (c) 2024 All rights reserved
using System.Text;
using FluentAssertions;

namespace JenkinsAsService.Tests;

/// <summary>
/// The secret reaches the agent through a URL and comes back through the controller's error messages, so it
/// can return re-encoded. A percent-encoded secret in a log file is just as disclosed as a plain one.
/// </summary>
public class SecretRedactorTests
{
    private const string Secret = "s3cr3t/value+with=specials";

    [Fact]
    public void No_patterns_for_an_absent_secret()
    {
        // Callers pass the resolved secret straight in; an empty one must not produce a pattern that
        // matches everything.
        SecretRedactor.BuildPatterns(null).Should().BeEmpty();
        SecretRedactor.BuildPatterns("").Should().BeEmpty();
    }

    [Fact]
    public void Redact_is_a_no_op_without_patterns()
    {
        SecretRedactor.Redact("nothing to hide", []).Should().Be("nothing to hide");
    }

    [Fact]
    public void The_plain_secret_is_redacted()
    {
        var line = SecretRedactor.Redact($"SEVERE: bad secret {Secret} rejected", SecretRedactor.BuildPatterns(Secret));

        line.Should().NotContain(Secret).And.Contain(SecretRedactor.Replacement);
    }

    [Fact]
    public void The_url_encoded_secret_is_redacted()
    {
        // The secret travels in a URL/form body, so the controller can echo it percent-encoded.
        var encoded = Uri.EscapeDataString(Secret);
        encoded.Should().NotBe(Secret, "this test is meaningless if the value encodes to itself");

        var line = SecretRedactor.Redact($"POST /tcpSlaveAgentListener?secret={encoded}", SecretRedactor.BuildPatterns(Secret));

        line.Should().NotContain(encoded).And.NotContain(Secret);
    }

    [Fact]
    public void The_xml_escaped_secret_is_redacted()
    {
        const string xmlish = "a&b<c>d";
        var escaped = "a&amp;b&lt;c&gt;d";

        var line = SecretRedactor.Redact($"<error>{escaped}</error>", SecretRedactor.BuildPatterns(xmlish));

        line.Should().NotContain(escaped).And.NotContain(xmlish);
    }

    [Fact]
    public void The_base64_secret_is_redacted()
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(Secret));

        var line = SecretRedactor.Redact($"Authorization: Basic {b64}", SecretRedactor.BuildPatterns(Secret));

        line.Should().NotContain(b64);
    }

    [Fact]
    public void Every_occurrence_on_a_line_is_replaced()
    {
        var line = SecretRedactor.Redact($"{Secret} then {Secret}", SecretRedactor.BuildPatterns(Secret));

        line.Should().NotContain(Secret);
        line.Should().Be($"{SecretRedactor.Replacement} then {SecretRedactor.Replacement}");
    }

    [Fact]
    public void Longest_patterns_are_applied_first()
    {
        // A secret with no special characters URL-encodes to itself, so the pattern set collapses to the
        // distinct forms only — and the longer Base64 form must not be left half-scrubbed by the shorter one.
        var patterns = SecretRedactor.BuildPatterns("plainsecret");

        patterns.Should().BeInDescendingOrder(p => p.Length);
        patterns.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Redaction_is_case_sensitive()
    {
        // A secret is case-sensitive; scrubbing case-insensitively would mangle unrelated text.
        var line = SecretRedactor.Redact("PLAINSECRET is a different value", SecretRedactor.BuildPatterns("plainsecret"));

        line.Should().Be("PLAINSECRET is a different value");
    }
}
