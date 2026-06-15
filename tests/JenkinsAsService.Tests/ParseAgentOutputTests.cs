// Copyright (c) 2024 All rights reserved
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace JenkinsAsService.Tests;

public class ParseAgentOutputTests
{
    private const string RawJavaOutput = "some raw java output";

    private readonly ILogger<JenkinsAgentWorker> _logger = Substitute.For<ILogger<JenkinsAgentWorker>>();
    private readonly JenkinsAgentWorker _worker;

    public ParseAgentOutputTests()
    {
        var settings = new ServiceSettings
        {
            JenkinsUrl = "https://jenkins:8443",
            AgentSecret = "secret",
            DebugMode = true
        };

        _worker = new JenkinsAgentWorker(
            _logger,
            Options.Create(settings),
            Substitute.For<IJarDownloader>(),
            Substitute.For<IConnectivityChecker>(),
            Substitute.For<ISecretResolver>());
    }

    [Fact]
    public void Ignores_whitespace_lines()
    {
        _worker.ParseAgentOutput("   ");

        _logger.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public void Parses_INFO_prefix_as_Information()
    {
        _worker.ParseAgentOutput("INFO: Connected to Jenkins");

        _logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Connected to Jenkins")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void Parses_WARNING_prefix_as_Warning()
    {
        _worker.ParseAgentOutput("WARNING: Connection lost");

        _logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Connection lost")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void Parses_SEVERE_prefix_as_Error()
    {
        _worker.ParseAgentOutput("SEVERE: Fatal error");

        _logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Fatal error")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void Unprefixed_line_in_debug_mode_logs_as_Debug()
    {
        _worker.ParseAgentOutput(RawJavaOutput);

        _logger.Received().Log(
            LogLevel.Debug,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains(RawJavaOutput)),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void Unprefixed_line_without_debug_mode_logs_as_Information()
    {
        var logger = Substitute.For<ILogger<JenkinsAgentWorker>>();
        var settings = new ServiceSettings
        {
            JenkinsUrl = "https://jenkins:8443",
            AgentSecret = "secret",
            DebugMode = false
        };
        var worker = new JenkinsAgentWorker(
            logger,
            Options.Create(settings),
            Substitute.For<IJarDownloader>(),
            Substitute.For<IConnectivityChecker>(),
            Substitute.For<ISecretResolver>());

        worker.ParseAgentOutput(RawJavaOutput);

        logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains(RawJavaOutput)),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
}
