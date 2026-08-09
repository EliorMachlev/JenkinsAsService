// Copyright (c) 2024 All rights reserved
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace JenkinsAsService.Tests;

public class ParseAgentOutputTests
{
    private const string RawJavaOutput = "some raw java output";

    private readonly ILogger<JenkinsAgentWorker> _logger = Substitute.For<ILogger<JenkinsAgentWorker>>();
    private readonly JenkinsAgentWorker _worker;

    public ParseAgentOutputTests() => _worker = CreateWorker(_logger, debugMode: true);

    /// <summary>
    /// A worker wired entirely to substitutes: these tests only drive <c>ParseAgentOutput</c>, so nothing
    /// beyond the logger and the settings participates.
    /// </summary>
    private static JenkinsAgentWorker CreateWorker(ILogger<JenkinsAgentWorker> logger, bool debugMode) =>
        new(logger,
            Options.Create(new ServiceSettings
            {
                Connection = new() { Url = "https://jenkins:8443" },
                Secret = new() { Value = "secret" },
                Logging = new() { DebugMode = debugMode }
            }),
            Substitute.For<IJarDownloader>(),
            Substitute.For<IConnectivityChecker>(),
            Substitute.For<ISecretResolver>(),
            Substitute.For<IAgentProcessLauncher>(),
            Substitute.For<IHostApplicationLifetime>());

    /// <summary>
    /// Asserts one log call at <paramref name="level"/> whose rendered state contains <paramref name="expected"/>.
    /// <para>
    /// ILogger&lt;T&gt; has a single generic <c>Log</c> method, so every assertion needs the same five-argument
    /// matcher; spelling it out per test buried the one line that differs. The null-guard on the state object
    /// is what the matcher needs to be honest — a null state should fail to match, not throw.
    /// </para>
    /// </summary>
    private static void AssertLogged(ILogger<JenkinsAgentWorker> logger, LogLevel level, string expected) =>
        logger.Received().Log(
            level,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state != null && state.ToString()!.Contains(expected, StringComparison.Ordinal)),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

    [Fact]
    public void Ignores_whitespace_lines()
    {
        _worker.ParseAgentOutput("   ");

        _logger.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData("INFO: Connected to Jenkins", LogLevel.Information, "Connected to Jenkins")]
    [InlineData("WARNING: Connection lost", LogLevel.Warning, "Connection lost")]
    [InlineData("SEVERE: Fatal error", LogLevel.Error, "Fatal error")]
    public void The_java_log_prefix_selects_the_level(string line, LogLevel expectedLevel, string expectedText)
    {
        _worker.ParseAgentOutput(line);

        AssertLogged(_logger, expectedLevel, expectedText);
    }

    [Fact]
    public void Unprefixed_line_in_debug_mode_logs_as_Debug()
    {
        _worker.ParseAgentOutput(RawJavaOutput);

        AssertLogged(_logger, LogLevel.Debug, RawJavaOutput);
    }

    [Fact]
    public void Unprefixed_line_without_debug_mode_logs_as_Information()
    {
        var logger = Substitute.For<ILogger<JenkinsAgentWorker>>();
        var worker = CreateWorker(logger, debugMode: false);

        worker.ParseAgentOutput(RawJavaOutput);

        AssertLogged(logger, LogLevel.Information, RawJavaOutput);
    }
}
