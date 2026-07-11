// Copyright (c) 2024 All rights reserved

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;

namespace JenkinsAsService;

public sealed class JenkinsAgentWorker : BackgroundService
{
    // ─── Watchdog / recovery timing ───────────────────────────────────────────
    // Defaults for the instance timing fields (_backoffBaseSec / _backoffMaxSec / _stabilityMs).
    private const int BackoffBaseSec = 10;
    private const int BackoffMaxSec = 300;
    private const int StabilityMs = 60_000;
    private const double BackoffMultiplier = 2;
    private const int UnknownExitCode = -1;

    // ─── Files / process ──────────────────────────────────────────────────────
    private const string JarFilename = "agent.jar";

    // ─── Java agent CLI argument names ─────────────────────────────────────────
    private const string ArgJar = "-jar";
    private const string ArgUrl = "-url";
    private const string ArgSecret = "-secret";
    private const string ArgName = "-name";
    private const string ArgWorkDir = "-workDir";
    private const string ArgWebSocket = "-webSocket";
    private const string ArgNoReconnect = "-noReconnect";

    // ─── Agent stdout/stderr log-level prefixes ───────────────────────────────
    private const string InfoPrefix = "INFO: ";
    private const string WarningPrefix = "WARNING: ";
    private const string SeverePrefix = "SEVERE: ";

    // ─── Misc ─────────────────────────────────────────────────────────────────
    private const string SecretFileArgPrefix = "@";
    private const string SecretRedaction = "*****";
    private const string UrlPathSeparator = "/";
    private const char TrailingSlash = '/';
    private const int ConnectivityTimeoutMs = 2_000;
    private const int BannerWidth = 80;
    private const char BannerChar = '=';
    private const string OutputMessageTemplate = "{Output}";

    /// <summary>OpenTelemetry Meter name. Referenced by <c>Program.cs</c> when registering the meter,
    /// so both sides stay in sync.</summary>
    public const string MeterName = "JenkinsAsService";
    private const string MeterVersion = "1.0.0";

    private readonly ILogger<JenkinsAgentWorker> _logger;
    private readonly ServiceSettings _settings;
    private readonly IJarDownloader _jarDownloader;
    private readonly IConnectivityChecker _connectivityChecker;
    private readonly ISecretResolver _secretResolver;
    private readonly IAgentProcessLauncher _launcher;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly string _basePath;
    private string _dataDir = "";
    private string _agentDir = "";
    private string _workDir = "";
    private string _javaExe = "";
    private string _agentName = "";
    private string _resolvedSecret = "";
    private string? _secretFilePath;
    private ConnectionMethod _effectiveMethod;
    private bool _currentRunReachedStability;
    private bool _hasStartedOnce;
    private volatile bool _stopping;
    private IAgentProcess? _agent;
    private int _severeCount;

    // Timing is instance state (defaulted from the consts) so tests can shrink the stability window and
    // backoff to milliseconds and drive the full supervision loop deterministically.
    private int _stabilityMs = StabilityMs;
    private int _backoffBaseSec = BackoffBaseSec;
    private int _backoffMaxSec = BackoffMaxSec;

    private readonly Meter _meter;
    private readonly Counter<long> _restartCounter;
    private readonly Counter<long> _severeEventCounter;

    public JenkinsAgentWorker(
        ILogger<JenkinsAgentWorker> logger,
        IOptions<ServiceSettings> settings,
        IJarDownloader jarDownloader,
        IConnectivityChecker connectivityChecker,
        ISecretResolver secretResolver,
        IAgentProcessLauncher launcher,
        IHostApplicationLifetime lifetime)
    {
        _logger = logger;
        _settings = settings.Value;
        _jarDownloader = jarDownloader;
        _connectivityChecker = connectivityChecker;
        _secretResolver = secretResolver;
        _launcher = launcher;
        _lifetime = lifetime;
        _basePath = AppContext.BaseDirectory;

        _meter = new Meter(MeterName, MeterVersion);
        _restartCounter = _meter.CreateCounter<long>(
            "jenkins_agent_restarts_total",
            description: "Number of agent restart attempts since service start");
        _severeEventCounter = _meter.CreateCounter<long>(
            "jenkins_agent_severe_events_total",
            description: "Number of SEVERE log lines emitted by the Jenkins agent");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            LogStartupBanner();

            ValidateSettings();
            ResolveJavaPath();

            // Connect, download and start are all handled inside the supervision loop's bring-up path, so a
            // transient outage at boot (DNS/network not up yet, Jenkins mid-restart) retries with backoff
            // instead of faulting the whole service.
            await RunSupervisionLoop(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            // Non-transient failure (bad config, unresolvable secret, missing Java). Retrying will not help,
            // so shut the host down cleanly rather than faulting — SCM recovery, if configured, restarts us.
            _logger.LogError(ex, "Service failed — shutting down");
            _lifetime.StopApplication();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogWarning("Service stop requested");
        // Signal shutdown intent BEFORE killing the agent so the supervision loop treats the kill-induced
        // exit as an intentional stop, not a crash to be recovered/counted.
        _stopping = true;
        KillAgent();
        AgentSecretFile.Delete(_secretFilePath);
        _logger.LogWarning("Jenkins Agent stopped");
        await base.StopAsync(cancellationToken);
    }

    // Test seam: shrink the stability window and backoff so the supervision loop runs in milliseconds.
    internal void UseFastTimingForTests(int stabilityMs = 50, int backoffBaseSec = 0, int backoffMaxSec = 0)
    {
        _stabilityMs = stabilityMs;
        _backoffBaseSec = backoffBaseSec;
        _backoffMaxSec = backoffMaxSec;
    }

    private void LogStartupBanner()
    {
        var banner = new string(BannerChar, BannerWidth);
        _logger.LogInformation("{Sep}", banner);
        _logger.LogInformation("=== SERVICE STARTING (PID: {Pid}) ===", Environment.ProcessId);
        _logger.LogInformation("Base directory: {BasePath}", _basePath);
        _logger.LogInformation("{Sep}", banner);
    }

    // ─── Validation ─────────────────────────────────────────────────────────

    private void ValidateSettings()
    {
        ServiceSettingsValidator.Validate(_settings);

        var uri = new Uri(_settings.Connection.Url);
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            _logger.LogWarning("Connection:Url uses {Scheme} — agent secret will be sent unencrypted. Consider HTTPS.", uri.Scheme);
        }

        if (string.IsNullOrWhiteSpace(_settings.Connection.AgentName))
        {
            _agentName = Environment.MachineName;
            _logger.LogDebug("AgentName is empty. Using hostname '{Name}'", _agentName);
        }
        else
        {
            _agentName = _settings.Connection.AgentName;
        }

        _resolvedSecret = _secretResolver.Resolve(_settings);
        _logger.LogInformation("Secret resolved via {Mode} mode", _settings.Secret.Mode);

        _dataDir = DataPaths.ResolveDataDirectory(_settings.Agent.DataDirectory);
        _agentDir = DataPaths.ResolveAgentDirectory(_dataDir);
        _workDir = DataPaths.ResolveWorkDirectory(_dataDir);
        _logger.LogInformation("Data directory: {DataDir} (agent cache: {AgentDir}, work dir: {WorkDir})",
            _dataDir, _agentDir, _workDir);

        _effectiveMethod = AgentTransport.InitialMethod(_settings.Connection.Method);
        _logger.LogInformation("Connection method: {Configured} (starting transport: {Effective})",
            _settings.Connection.Method, _effectiveMethod);
    }

    private void ResolveJavaPath()
    {
        _javaExe = JavaPathResolver.Resolve(_settings.Agent.JavaPath, Environment.GetEnvironmentVariable("JAVA_HOME"));
        _logger.LogDebug("Resolved Java at: '{Path}'", Path.GetDirectoryName(_javaExe));
    }

    private async Task TestConnectivity(CancellationToken ct)
    {
        var uri = new Uri(_settings.Connection.Url);
        await _connectivityChecker.Check(uri.Host, uri.Port, ConnectivityTimeoutMs, ct);
    }

    // ─── Agent Process ──────────────────────────────────────────────────────

    private IAgentProcess StartAgentProcess()
    {
        var psi = BuildProcessStartInfo();
        _currentRunReachedStability = false;
        return _launcher.Start(psi, ParseAgentOutput);
    }

    private ProcessStartInfo BuildProcessStartInfo()
    {
        var jarPath = Path.Combine(_agentDir, JarFilename);
        var normalizedUrl = $"{_settings.Connection.Url.TrimEnd(TrailingSlash)}{UrlPathSeparator}";

        var psi = new ProcessStartInfo(_javaExe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _workDir
        };

        AddAgentArguments(psi, jarPath, normalizedUrl);

        // Strip the inherited service environment so build secrets can't leak into untrusted pipeline
        // scripts running inside the agent. UseShellExecute = false pre-populates psi.Environment with
        // the parent block; we deny-by-default and keep only what Java + tooling need.
        if (_settings.Hardening.SanitizeEnvironment)
        {
            EnvironmentSanitizer.Apply(psi.Environment, _settings.Hardening.AllowedEnvironmentVariables);
        }

        LogLaunchCommand(psi);
        return psi;
    }

    private void AddAgentArguments(ProcessStartInfo psi, string jarPath, string normalizedUrl)
    {
        psi.ArgumentList.Add(ArgJar);
        psi.ArgumentList.Add(jarPath);
        psi.ArgumentList.Add(ArgUrl);
        psi.ArgumentList.Add(normalizedUrl);
        psi.ArgumentList.Add(ArgSecret);
        psi.ArgumentList.Add(ResolveSecretArgument());
        psi.ArgumentList.Add(ArgName);
        psi.ArgumentList.Add(_agentName);
        psi.ArgumentList.Add(ArgWorkDir);
        psi.ArgumentList.Add(_workDir);

        if (_effectiveMethod == ConnectionMethod.WebSocket)
        {
            psi.ArgumentList.Add(ArgWebSocket);
        }

        // In Auto mode the watchdog owns reconnection. Without -noReconnect the Java agent retries a failed
        // handshake internally forever, so the process never exits — and ApplyAutoTransportFallback (which
        // fires only on a real exit) can never flip WebSocket ↔ direct-TCP, pinning a broken transport. With
        // it, a fast handshake failure exits the process, is counted as a crash, and triggers the fallback.
        // Fixed transports keep the agent's own faster internal reconnect (no alternate transport to try).
        if (AgentTransport.UsesWatchdogReconnect(_settings.Connection.Method))
        {
            psi.ArgumentList.Add(ArgNoReconnect);
        }

        foreach (var arg in AgentArgumentParser.Parse(_settings.Agent.CustomArguments))
        {
            psi.ArgumentList.Add(arg);
        }
    }

    // Logs the full java command line at Debug level for diagnostics, with the resolved secret redacted.
    private void LogLaunchCommand(ProcessStartInfo psi)
    {
        if (!_logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        var command = $"{psi.FileName} {string.Join(' ', psi.ArgumentList)}";
        if (!string.IsNullOrEmpty(_resolvedSecret))
        {
            command = command.Replace(_resolvedSecret, SecretRedaction, StringComparison.Ordinal);
        }

        _logger.LogDebug("Launching agent ({Transport}): {Command}", _effectiveMethod, command);
    }

    /// <summary>
    /// Returns the value passed after <c>-secret</c>: either <c>@&lt;file&gt;</c> (default, keeps the secret
    /// off the process command line) or the raw secret when <see cref="SecretSettings.ViaFile"/>
    /// is disabled.
    /// </summary>
    private string ResolveSecretArgument()
    {
        if (!_settings.Secret.ViaFile)
        {
            return _resolvedSecret;
        }

        _secretFilePath = AgentSecretFile.Write(
            _dataDir, _resolvedSecret, msg => _logger.LogWarning("{Warning}", msg));
        return SecretFileArgPrefix + _secretFilePath;
    }

    internal void ParseAgentOutput(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        // Redact resolved secret in case Jenkins emits it on handshake failure
        if (!string.IsNullOrEmpty(_resolvedSecret))
        {
            line = line.Replace(_resolvedSecret, SecretRedaction, StringComparison.Ordinal);
        }

        if (line.StartsWith(InfoPrefix, StringComparison.Ordinal))
        {
            _logger.LogInformation(OutputMessageTemplate, line[InfoPrefix.Length..]);
        }
        else if (line.StartsWith(WarningPrefix, StringComparison.Ordinal))
        {
            _logger.LogWarning(OutputMessageTemplate, line[WarningPrefix.Length..]);
        }
        else if (line.StartsWith(SeverePrefix, StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _severeCount);
            _severeEventCounter.Add(1);
            _logger.LogError(OutputMessageTemplate, line[SeverePrefix.Length..]);
        }
        else if (_settings.Logging.DebugMode)
        {
            _logger.LogDebug(OutputMessageTemplate, line);
        }
        else
        {
            _logger.LogInformation(OutputMessageTemplate, line);
        }
    }

    // ─── Watchdog / supervision loop ──────────────────────────────────────────

    /// <summary>
    /// Supervises the agent for the service lifetime. Two independent states:
    /// <list type="bullet">
    /// <item><b>No live agent</b> — connect, download, start, with backoff. A controller that is unreachable
    /// (transient outage / maintenance) is retried indefinitely and never counts toward the give-up budget.</item>
    /// <item><b>Live agent</b> — wait for the stability window or an exit. A real exit before stability is a
    /// crash; consecutive crashes count toward <c>Recovery:MaxRetries</c>, and exceeding it stops the service
    /// (so SCM sees it stop) rather than leaving a dead agent behind a RUNNING service.</item>
    /// </list>
    /// </summary>
    private async Task RunSupervisionLoop(CancellationToken ct)
    {
        _logger.LogInformation("Watchdog started");

        var crashCount = 0;      // consecutive real agent deaths — the only thing that trips give-up
        var bringUpAttempts = 0; // consecutive failed connect/download/start attempts — drive backoff only

        while (true)
        {
            // Single shutdown gate for every path back to the loop top. Killing here (idempotent) closes
            // the stop-during-bring-up race: StopAsync's own KillAgent runs while _agent may still be null,
            // and the launcher can assign a freshly started process AFTER that — without this gate the loop
            // would exit on the cancelled token and orphan the just-started java.exe past service shutdown.
            if (_stopping || ct.IsCancellationRequested)
            {
                KillAgent();
                ct.ThrowIfCancellationRequested();
                return;
            }

            // Snapshot the field: StopAsync (another thread) nulls _agent via KillAgent, so dereferencing
            // the field after a null-check races an NRE during a clean stop.
            var agent = _agent;
            if (agent is null)
            {
                if (bringUpAttempts > 0)
                {
                    var delay = ComputeBackoffDelaySeconds(bringUpAttempts, _backoffBaseSec, _backoffMaxSec, BackoffMultiplier);
                    if (delay > 0)
                    {
                        _logger.LogInformation("Watchdog: waiting {Delay}s before the next connect attempt...", delay);
                        await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                    }
                }

                bringUpAttempts = await TryBringUpAgent(ct) ? 0 : bringUpAttempts + 1;
                continue;
            }

            // Link a per-iteration CTS so the stability timer is cancelled the moment the agent exits,
            // instead of leaving a live 60s timer per cycle to accumulate under frequent restarts.
            using (var stabilityCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                await Task.WhenAny(agent.Exited, Task.Delay(_stabilityMs, stabilityCts.Token));
                await stabilityCts.CancelAsync();
            }

            // Intentional shutdown: StopAsync set _stopping and killed the agent. Do not treat that as a crash.
            if (_stopping)
            {
                return;
            }

            ct.ThrowIfCancellationRequested();

            if (!agent.Exited.IsCompleted && !agent.HasExited)
            {
                // Stability window elapsed, agent still running.
                _currentRunReachedStability = true;
                if (crashCount > 0)
                {
                    crashCount = 0;
                    _logger.LogInformation("Watchdog: Agent stable for {Seconds}s. Crash counter reset.", _stabilityMs / 1000);
                }

                continue;
            }

            // ── Real agent death ──
            var severeCount = Interlocked.Exchange(ref _severeCount, 0);
            if (severeCount > 0)
            {
                _logger.LogWarning("Watchdog: Agent emitted {Count} SEVERE event(s) before exiting.", severeCount);
            }

            var exitCode = agent.HasExited ? agent.ExitCode : UnknownExitCode;
            KillAgent();
            ApplyAutoTransportFallback(agentActuallyExited: true);

            crashCount++;
            _logger.LogWarning("Watchdog: Agent exited (code: {Code}). Crash {Count}.", exitCode, crashCount);

            if (HasExceededMaxRetries(crashCount, _settings.Recovery.MaxRetries))
            {
                _logger.LogError(
                    "Watchdog: Max retries ({Max}) exceeded — the agent keeps crashing. Stopping the service.",
                    _settings.Recovery.MaxRetries);
                _lifetime.StopApplication();
                return;
            }
        }
    }

    /// <summary>
    /// One bring-up attempt: connectivity → jar download → start. Returns false (to be retried with backoff)
    /// both when the controller is unreachable and when download/start fails. Never counts toward give-up.
    /// </summary>
    private async Task<bool> TryBringUpAgent(CancellationToken ct)
    {
        try
        {
            await TestConnectivity(ct);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Watchdog: Jenkins unreachable — will keep retrying. {Reason}", ex.Message);
            return false;
        }

        try
        {
            await _jarDownloader.Download(new Uri(_settings.Connection.Url), _agentDir, ct);
            _agent = StartAgentProcess();

            // The first successful start is the initial launch; subsequent ones are restarts (metric).
            if (_hasStartedOnce)
            {
                _restartCounter.Add(1);
            }

            _hasStartedOnce = true;
            _logger.LogInformation("Jenkins Agent started (Java PID: {Pid}, transport: {Transport})",
                _agent.Id, _effectiveMethod);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Watchdog: Failed to download or start the agent — will retry");
            return false;
        }
    }

    /// <summary>
    /// Auto-mode transport fallback: when a <em>real</em> agent process exits before reaching the
    /// stability window, switch to the other transport for the next attempt (WebSocket ↔ Https).
    /// Delegates the decision to <see cref="AgentTransport.ShouldFallback"/>.
    /// </summary>
    private void ApplyAutoTransportFallback(bool agentActuallyExited)
    {
        if (!AgentTransport.ShouldFallback(_settings.Connection.Method, _currentRunReachedStability, agentActuallyExited))
        {
            return;
        }

        var previous = _effectiveMethod;
        _effectiveMethod = AgentTransport.Toggle(_effectiveMethod);
        _logger.LogWarning("Watchdog: {Previous} transport failed to stabilise — falling back to {Next}.",
            previous, _effectiveMethod);
    }

    internal static bool HasExceededMaxRetries(int crashCount, int maxRetries) =>
        maxRetries > 0 && crashCount > maxRetries;

    // Exponential backoff: base * multiplier^(attempt-1), capped at max. Pure so the curve is unit-testable.
    internal static int ComputeBackoffDelaySeconds(int attempt, int baseSec, int maxSec, double multiplier) =>
        (int)Math.Min(baseSec * Math.Pow(multiplier, attempt - 1), maxSec);

    private void KillAgent()
    {
        if (_agent is null)
        {
            return;
        }

        _logger.LogDebug("Killing agent process tree (PID: {Pid})", _agent.Id);
        _agent.Kill(); // never throws — handled inside the launcher
        _agent.Dispose();
        _agent = null;
    }

    public override void Dispose()
    {
        _meter.Dispose();
        base.Dispose();
    }
}
