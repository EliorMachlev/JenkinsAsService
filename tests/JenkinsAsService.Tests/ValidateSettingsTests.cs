using FluentAssertions;

namespace JenkinsAsService.Tests;

public class ValidateSettingsTests
{
    [Fact]
    public void Throws_when_JenkinsURL_is_empty()
    {
        var settings = new ServiceSettings { AgentSecret = "secret" };

        var act = () => JenkinsAgentWorker.ValidateSettings(settings);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*JenkinsURL*");
    }

    [Fact]
    public void Throws_when_AgentSecret_is_empty()
    {
        var settings = new ServiceSettings { JenkinsURL = "https://jenkins:8443" };

        var act = () => JenkinsAgentWorker.ValidateSettings(settings);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AgentSecret*");
    }

    [Fact]
    public void Throws_when_URL_uses_default_port()
    {
        var settings = new ServiceSettings
        {
            JenkinsURL = "https://jenkins.example.com",
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
            JenkinsURL = "not-a-url",
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
            JenkinsURL = "https://jenkins.example.com:8443",
            AgentSecret = "secret123"
        };

        var act = () => JenkinsAgentWorker.ValidateSettings(settings);

        act.Should().NotThrow();
    }
}
