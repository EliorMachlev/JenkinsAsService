// Copyright (c) 2024 All rights reserved
using FluentAssertions;

namespace JenkinsAsService.Tests;

public class ValidateSettingsTests
{
    [Fact]
    public void Throws_when_url_is_empty()
    {
        var settings = new ServiceSettings { Secret = new() { Value = "secret" } };

        var act = () => ServiceSettingsValidator.Validate(settings);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Connection:Url*");
    }

    [Fact]
    public void Throws_when_secret_is_empty()
    {
        var settings = new ServiceSettings { Connection = new() { Url = "https://jenkins:8443" } };

        var act = () => ServiceSettingsValidator.Validate(settings);

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

        var act = () => ServiceSettingsValidator.Validate(settings);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*explicit port*");
    }

    [Theory]
    [InlineData("https://jenkins.example.com:443")]   // explicit standard HTTPS port (reverse proxy)
    [InlineData("http://jenkins.example.com:80")]      // explicit standard HTTP port
    [InlineData("https://[2001:db8::1]:443")]          // explicit port on an IPv6 literal
    public void Passes_when_URL_specifies_an_explicit_port_even_if_it_is_the_scheme_default(string url)
    {
        var settings = new ServiceSettings
        {
            Connection = new() { Url = url },
            Secret = new() { Value = "secret" }
        };

        var act = () => ServiceSettingsValidator.Validate(settings);

        act.Should().NotThrow();
    }

    [Fact]
    public void Throws_a_friendly_error_when_URL_is_malformed()
    {
        var settings = new ServiceSettings
        {
            Connection = new() { Url = "not-a-url" },
            Secret = new() { Value = "secret" }
        };

        var act = () => ServiceSettingsValidator.Validate(settings);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not a valid absolute URL*");
    }

    [Fact]
    public void Passes_with_valid_settings()
    {
        var settings = new ServiceSettings
        {
            Connection = new() { Url = "https://jenkins.example.com:8443" },
            Secret = new() { Value = "secret123" }
        };

        var act = () => ServiceSettingsValidator.Validate(settings);

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
        var act = () => ServiceSettingsValidator.Validate(settings);
        act.Should().NotThrow();
    }
}
