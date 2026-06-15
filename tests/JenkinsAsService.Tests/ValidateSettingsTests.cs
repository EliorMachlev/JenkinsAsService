// Copyright (c) 2024 All rights reserved
using FluentAssertions;

namespace JenkinsAsService.Tests;

public class ValidateSettingsTests
{
    [Fact]
    public void Throws_when_JenkinsUrl_is_empty()
    {
        var settings = new ServiceSettings { AgentSecret = "secret" };

        var act = () => JenkinsAgentWorker.ValidateSettings(settings);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*JenkinsUrl*");
    }

    [Fact]
    public void Throws_when_AgentSecret_is_empty()
    {
        var settings = new ServiceSettings { JenkinsUrl = "https://jenkins:8443" };

        var act = () => JenkinsAgentWorker.ValidateSettings(settings);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AgentSecret*");
    }

    [Fact]
    public void Throws_when_URL_uses_default_port()
    {
        var settings = new ServiceSettings
        {
            JenkinsUrl = "https://jenkins.example.com",
            AgentSecret = "secret"
        };

        var act = () => JenkinsAgentWorker.ValidateSettings(settings);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*explicit port*");
    }

    [Fact]
    public void Throws_when_URL_is_malformed()
    {
        var settings = new ServiceSettings
        {
            JenkinsUrl = "not-a-url",
            AgentSecret = "secret"
        };

        var act = () => JenkinsAgentWorker.ValidateSettings(settings);

        act.Should().Throw<UriFormatException>();
    }

    [Fact]
    public void Passes_with_valid_settings()
    {
        var settings = new ServiceSettings
        {
            JenkinsUrl = "https://jenkins.example.com:8443",
            AgentSecret = "secret123"
        };

        var act = () => JenkinsAgentWorker.ValidateSettings(settings);

        act.Should().NotThrow();
    }

    [Fact]
    public void SecretMode_defaults_to_Unprotected_when_not_in_config()
    {
        // Existing installs have no SecretMode field — binding should fall back to enum default
        var settings = new ServiceSettings
        {
            JenkinsUrl = "https://jenkins.example.com:8443",
            AgentSecret = "plaintext-secret"
            // SecretMode intentionally not set (simulates old appsettings.json)
        };

        settings.SecretMode.Should().Be(SecretMode.Unprotected);
        var act = () => JenkinsAgentWorker.ValidateSettings(settings);
        act.Should().NotThrow();
    }
}
