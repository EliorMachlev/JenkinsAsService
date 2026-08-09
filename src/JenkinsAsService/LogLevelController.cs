// Copyright (c) 2024 All rights reserved

using Microsoft.Extensions.Options;
using Serilog.Core;
using Serilog.Events;

namespace JenkinsAsService;

/// <summary>
/// Keeps Serilog's minimum level in step with <c>Logging:DebugMode</c> while the service is running.
/// <para>
/// Diagnosing a crash-loop used to require editing the config and <em>restarting the service</em> — which
/// destroys the very state you were trying to observe, and on a build node means dropping the agent. The
/// level now lives in a <see cref="LoggingLevelSwitch"/> that this hosted service re-points whenever
/// appsettings.json changes on disk.
/// </para>
/// </summary>
internal sealed class LogLevelController : IHostedService, IDisposable
{
    /// <summary>
    /// The switch the Serilog pipeline is built around. Static because the logger is created in
    /// <c>Program.cs</c> before the host (and therefore before DI) exists, and both sides must share
    /// the one instance for a change to reach the running pipeline.
    /// </summary>
    internal static readonly LoggingLevelSwitch Switch = new(LogEventLevel.Information);

    private readonly IOptionsMonitor<ServiceSettings> _monitor;
    private readonly ILogger<LogLevelController> _logger;
    private IDisposable? _subscription;

    public LogLevelController(IOptionsMonitor<ServiceSettings> monitor, ILogger<LogLevelController> logger)
    {
        _monitor = monitor;
        _logger = logger;
    }

    /// <summary>The level <paramref name="debugMode"/> selects. The only place the mapping is defined.</summary>
    internal static LogEventLevel LevelFor(bool debugMode) =>
        debugMode ? LogEventLevel.Debug : LogEventLevel.Information;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Apply(_monitor.CurrentValue.Logging.DebugMode, initial: true);
        _subscription = _monitor.OnChange(settings => Apply(settings.Logging.DebugMode, initial: false));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    private void Apply(bool debugMode, bool initial)
    {
        var level = LevelFor(debugMode);
        if (Switch.MinimumLevel == level)
        {
            return; // OnChange can fire more than once for a single save — don't log a change that isn't one.
        }

        Switch.MinimumLevel = level;
        if (!initial)
        {
            // Logged at Warning so it is visible in the Event Log too: a level change explains a sudden
            // change in log volume, and an operator reading the file later needs to know when it happened.
            _logger.LogWarning(
                "Logging:DebugMode changed to {DebugMode} — log level is now {Level}. No restart required.",
                debugMode, level);
        }
    }

    public void Dispose() => _subscription?.Dispose();
}
