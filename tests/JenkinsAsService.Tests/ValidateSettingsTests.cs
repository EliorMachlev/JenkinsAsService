// Copyright (c) 2024 All rights reserved
using FluentAssertions;

namespace JenkinsAsService.Tests;

public class ValidateSettingsTests
{
    [Fact]
    public void Throws_when_url_is_empty()
    {
        var settings = new ServiceSettings { Secret = new() { Value = "secret" } };

        var act = () => JenkinsAgentWorker.ValidateSettings(settings);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Connection:Url*");
    }

    [Fact]
    public void Throws_when_secret_is_empty()
    {
        var settings = new ServiceSettings { Connection = new() { Url = "https://jenkins:8443" } };

        var act = () => JenkinsAgentWorker.ValidateSettings(settings);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Secret:Value*");
    }

    [Fact]
    public void Throws_when_URL_uses_default_port()
    {
        var settings = new ServiceSettings
        {
            Connection = new() { Url = "https://jenkins.example.com" },
            Secret = new() { Value = "secret" }
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
            Connection = new() { Url = "not-a-url" },
            Secret = new() { Value = "secret" }
        };

        var act = () => JenkinsAgentWorker.ValidateSettings(settings);

        act.Should().Throw<UriFormatException>();
    }

    [Fact]
    public void Passes_with_valid_settings()
    {
        var settings = new ServiceSettings
        {
            Connection = new() { Url = "https://jenkins.example.com:8443" },
            Secret = new() { Value = "secret123" }
        };

        var act = () => JenkinsAgentWorker.ValidateSettings(settings);

        act.Should().NotThrow();
    }

    [Fact]
    public void SecretMode_defaults_to_Unprotected_when_not_in_config()
    {
        // A config with no Secret:Mode should fall back to the enum default.
        var settings = new ServiceSettings
        {
            Connection = new() { Url = "https://jenkins.example.com:8443" },
            Secret = new() { Value = "plaintext-secret" }
            // Mode intentionally not set
        };

        settings.Secret.Mode.Should().Be(SecretMode.Unprotected);
        var act = () => JenkinsAgentWorker.ValidateSettings(settings);
        act.Should().NotThrow();
    }
}
