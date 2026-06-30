// Copyright (c) 2024 All rights reserved
using FluentAssertions;

namespace JenkinsAsService.Tests;

public class ConnectionMethodTests
{
    [Theory]
    [InlineData(ConnectionMethod.Auto, ConnectionMethod.WebSocket)]      // Auto starts on WebSocket
    [InlineData(ConnectionMethod.WebSocket, ConnectionMethod.WebSocket)]
    [InlineData(ConnectionMethod.Https, ConnectionMethod.Https)]
    public void InitialEffectiveMethod_picks_the_starting_transport(ConnectionMethod configured, ConnectionMethod expected)
    {
        JenkinsAgentWorker.InitialEffectiveMethod(configured).Should().Be(expected);
    }

    [Theory]
    [InlineData(ConnectionMethod.WebSocket, ConnectionMethod.Https)]
    [InlineData(ConnectionMethod.Https, ConnectionMethod.WebSocket)]
    public void ToggleMethod_flips_between_the_two_transports(ConnectionMethod current, ConnectionMethod expected)
    {
        JenkinsAgentWorker.ToggleMethod(current).Should().Be(expected);
    }

    [Fact]
    public void Toggling_twice_returns_to_the_original()
    {
        var once = JenkinsAgentWorker.ToggleMethod(ConnectionMethod.WebSocket);
        JenkinsAgentWorker.ToggleMethod(once).Should().Be(ConnectionMethod.WebSocket);
    }

    [Fact]
    public void Method_defaults_to_Auto()
    {
        new ServiceSettings().Connection.Method.Should().Be(ConnectionMethod.Auto);
    }
}
