// Copyright (c) 2024 All rights reserved

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using Microsoft.Extensions.Options;

namespace JenkinsAsService;

public sealed class JenkinsAgentWorker : BackgroundService
{
    // ─── Watchdog / recovery timing ───────────────────────────────────────────
    private const int BackoffBaseSec = 10;
    private const int BackoffMaxSec = 300;
    private const int StabilityMs = 60_000;
    private const int StabilitySeconds = StabilityMs / 1_000;
    private const double BackoffMultiplier = 2;
    private const int UnknownExitCode = -1;
    private const int EscapedQuoteWidth = 2;

    // ─── Files / process ──────────────────────────────────────────────────────
    private const string JarFilename = "agent.jar";
    private const string JavaExeFilename = "java.exe";
    private const string JavaBinFolder = "bin";

    // ─── Java agent CLI argument names ─────────────────────────────────────────
    private const string ArgJar = "-jar";
    private const string ArgUrl = "-url";
    private const string ArgSecret = "-secret";
    private const string ArgName = "-name";
    private const string ArgWorkDir = "-workDir";
    private const string ArgWebSocket = "-webSocket";

    // ─── Agent stdout/stderr log-level prefixes ───────────────────────────────
    private const string InfoPrefix = "INFO: ";
    private const string WarningPrefix = "WARNING: ";
    private const string SeverePrefix = "SEVERE: ";

    // ─── Secret file / environment hardening ──────────────────────────────────
    private const string SecretFileArgPrefix = "@";
    private static readonly char[] AllowListSeparators = [';', ','];

    // Deny-by-default allow-list for the Java agent's environment block. Covers what the JVM and common
    // Windows build tooling need; everything else (incl. service-injected secrets) is stripped. Extend
    // per-deployment via ServiceSettings.AllowedEnvironmentVariables rather than editing this list.
    private static readonly string[] DefaultEnvAllowlist =
    [
        "SystemRoot", "windir", "SystemDrive", "ComSpec",
        "PATH", "PATHEXT",
        "TEMP", "TMP",
        "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER", "OS", "COMPUTERNAME",
        "USERNAME", "USERPROFILE", "USERDOMAIN", "HOMEDRIVE", "HOMEPATH",
        "APPDATA", "LOCALAPPDATA", "ProgramData",
        "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "CommonProgramFiles", "CommonProgramFiles(x86)",
        "JAVA_HOME",
    ];

    // ─── Misc ─────────────────────────────────────────────────────────────────
    private const string SecretRedaction = "*****";
    private const string UrlPathSeparator = "/";
    private const char TrailingSlash = '/';
    private const int MaxJavaCandidates = 3;
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
    private readonly string _basePath;
    private string _dataDir = "";
    private string _javaExe = "";
    private string _agentName = "";
    private string _resolvedSecret = "";
    private string? _secretFilePath;
    private ConnectionMethod _effectiveMethod;
    private bool _currentRunReachedStability;
    private Process? _agentProcess;
    private TaskCompletionSource? _agentExitTcs;
    private int _severeCount;

    private readonly Meter _meter;
    private readonly Counter<long> _restartCounter;
    private readonly Counter<long> _severeEventCounter;

    public JenkinsAgentWorker(
        ILogger<JenkinsAgentWorker> logger,
        IOptions<ServiceSettings> settings,
        IJarDownloader jarDownloader,
        IConnectivityChecker connectivityChecker,
        ISecretResolver secretResolver)
    {
        _logger = logger;
        _settings = settings.Value;
        _jarDownloader = jarDownloader;
        _connectivityChecker = connectivityChecker;
        _secretResolver = secretResolver;
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
            await TestConnectivity(stoppingToken);
            await _jarDownloader.Download(new Uri(_settings.Connection.Url), _dataDir, stoppingToken);

            _agentProcess = StartAgentProcess();
            await RunWatchdog(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Service failed");
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogWarning("Service stop requested");
        KillAgent();
        AgentSecretFile.Delete(_secretFilePath);
        _logger.LogWarning("Jenkins Agent stopped");
        await base.StopAsync(cancellationToken);
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

    internal static void ValidateSettings(ServiceSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Connection.Url))
        {
            throw new InvalidOperationException("'Connection:Url' is a mandatory field.");
        }

        if (string.IsNullOrWhiteSpace(settings.Secret.Value))
        {
            throw new InvalidOperationException("'Secret:Value' is a mandatory field.");
        }

        var uri = new Uri(settings.Connection.Url);
        if (uri.IsDefaultPort)
        {
            throw new InvalidOperationException(
                $"Connection:Url must include an explicit port: '{settings.Connection.Url}'. " +
                "Example: https://jenkins.example.com:8443");
        }
    }

    private void ValidateSettings()
    {
        ValidateSettings(_settings);

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
        _logger.LogInformation("Data directory: {DataDir}", _dataDir);

        _effectiveMethod = InitialEffectiveMethod(_settings.Connection.Method);
        _logger.LogInformation("Connection method: {Configured} (starting transport: {Effective})",
            _settings.Connection.Method, _effectiveMethod);
    }

    /// <summary>The transport to attempt first: <c>Https</c> stays direct TCP inbound; <c>Auto</c> and
    /// <c>WebSocket</c> both start on WebSocket (only <c>Auto</c> later falls back).</summary>
    internal static ConnectionMethod InitialEffectiveMethod(ConnectionMethod configured) =>
        configured == ConnectionMethod.Https ? ConnectionMethod.Https : ConnectionMethod.WebSocket;

    /// <summary>Flips between the two transports for <c>Auto</c> fallback.</summary>
    internal static ConnectionMethod ToggleMethod(ConnectionMethod current) =>
        current == ConnectionMethod.WebSocket ? ConnectionMethod.Https : ConnectionMethod.WebSocket;

    internal static string ResolveJavaPath(string? configuredPath, string? javaHome)
    {
        var candidates = new List<string>(MaxJavaCandidates);

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            candidates.Add(configuredPath);
        }

        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            candidates.Add(javaHome);
            candidates.Add(Path.Combine(javaHome, JavaBinFolder));
        }

        foreach (var path in candidates)
        {
            var exe = Path.Combine(path, JavaExeFilename);
            if (File.Exists(exe))
            {
                return exe;
            }
        }

        throw new InvalidOperationException(
            "Cannot find java.exe. Set 'JavaPath' in appsettings.json or the JAVA_HOME environment variable.");
    }

    private void ResolveJavaPath()
    {
        _javaExe = ResolveJavaPath(_settings.Agent.JavaPath, Environment.GetEnvironmentVariable("JAVA_HOME"));
        _logger.LogDebug("Resolved Java at: '{Path}'", Path.GetDirectoryName(_javaExe));
    }

    private async Task TestConnectivity(CancellationToken ct)
    {
        var uri = new Uri(_settings.Connection.Url);
        await _connectivityChecker.Check(uri.Host, uri.Port, ConnectivityTimeoutMs, ct);
    }

    // ─── Agent Process ──────────────────────────────────────────────────────

    private Process StartAgentProcess()
    {
        var psi = BuildProcessStartInfo();
        _currentRunReachedStability = false;

        _agentExitTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        void OnDataReceived(object _, DataReceivedEventArgs e)
        {
            if (e.Data is not null)
            {
                ParseAgentOutput(e.Data);
            }
        }

        process.OutputDataReceived += OnDataReceived;
        process.ErrorDataReceived += OnDataReceived;
        // Subscribe before Start so a fast-exiting process doesn't miss the event.
        process.Exited += (_, _) => _agentExitTcs.TrySetResult();

        process.Start();
        // Defensive: if the process already exited in the window between Start() and the
        // subscription above (extremely rare), fire the signal now so the watchdog doesn't wait.
        if (process.HasExited)
        {
            _agentExitTcs.TrySetResult();
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _logger.LogInformation("Jenkins Agent started (Java PID: {JavaPid})", process.Id);
        return process;
    }

    private ProcessStartInfo BuildProcessStartInfo()
    {
        var jarPath = Path.Combine(_dataDir, JarFilename);
        var normalizedUrl = $"{_settings.Connection.Url.TrimEnd(TrailingSlash)}{UrlPathSeparator}";

        var psi = new ProcessStartInfo(_javaExe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _dataDir
        };

        AddAgentArguments(psi, jarPath, normalizedUrl);

        // Strip the inherited service environment so build secrets can't leak into untrusted pipeline
        // scripts running inside the agent. UseShellExecute = false pre-populates psi.Environment with
        // the parent block; we deny-by-default and keep only what Java + tooling need.
        if (_settings.Hardening.SanitizeEnvironment)
        {
            SanitizeEnvironment(psi.Environment, _settings.Hardening.AllowedEnvironmentVariables);
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
        psi.ArgumentList.Add(_dataDir);

        if (_effectiveMethod == ConnectionMethod.WebSocket)
        {
            psi.ArgumentList.Add(ArgWebSocket);
        }

        foreach (var arg in ParseArguments(_settings.Agent.CustomArguments))
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

    /// <summary>
    /// Deny-by-default environment scrub: removes every variable from <paramref name="environment"/> that
    /// is not in the curated <see cref="DefaultEnvAllowlist"/> or the caller-supplied
    /// <paramref name="extraAllowed"/> (semicolon/comma-separated, case-insensitive).
    /// </summary>
    internal static void SanitizeEnvironment(IDictionary<string, string?> environment, string? extraAllowed)
    {
        var allow = new HashSet<string>(DefaultEnvAllowlist, StringComparer.OrdinalIgnoreCase);
        foreach (var name in SplitAllowList(extraAllowed))
        {
            allow.Add(name);
        }

        foreach (var key in environment.Keys.ToList())
        {
            if (!allow.Contains(key))
            {
                environment.Remove(key);
            }
        }
    }

    private static IEnumerable<string> SplitAllowList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(AllowListSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal static List<string> ParseArguments(string? input)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(input))
        {
            return result;
        }

        var i = 0;

        while (i < input.Length)
        {
            while (i < input.Length && char.IsWhiteSpace(input[i]))
            {
                i++;
            }

            if (i < input.Length)
            {
                if (input[i] == '"')
                {
                    i++; // skip opening quote
                    result.Add(ParseQuotedArg(input, ref i));
                }
                else
                {
                    result.Add(ParseUnquotedArg(input, ref i));
                }
            }
        }

        return result;
    }

    private static string ParseQuotedArg(string input, ref int i)
    {
        var buf = new StringBuilder();
        while (i < input.Length && input[i] != '"')
        {
            if (input[i] == '\\' && i + 1 < input.Length && input[i + 1] == '"')
            {
                buf.Append('"');
                i += EscapedQuoteWidth;
            }
            else
            {
                buf.Append(input[i]);
                i++;
            }
        }

        if (i < input.Length)
        {
            i++; // skip closing quote
        }

        return buf.ToString();
    }

    private static string ParseUnquotedArg(string input, ref int i)
    {
        var end = i;
        while (end < input.Length && !char.IsWhiteSpace(input[end]))
        {
            end++;
        }

        var token = input[i..end];
        i = end;
        return token;
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

    // ─── Watchdog ───────────────────────────────────────────────────────────

    private async Task RunWatchdog(CancellationToken ct)
    {
        var retryCount = 0;

        _logger.LogInformation("Watchdog started");

        while (!ct.IsCancellationRequested)
        {
            var exitTask = _agentExitTcs!.Task;
            await Task.WhenAny(exitTask, Task.Delay(StabilityMs, ct));

            ct.ThrowIfCancellationRequested();

            if (!exitTask.IsCompleted && _agentProcess is { HasExited: false })
            {
                // Stability window elapsed, agent still running
                _currentRunReachedStability = true;
                if (retryCount > 0)
                {
                    retryCount = 0;
                    _logger.LogInformation("Watchdog: Agent stable for {Seconds}s. Retry counter reset.", StabilitySeconds);
                }

                continue;
            }

            // ── Agent died — begin recovery ──
            var severeCount = Interlocked.Exchange(ref _severeCount, 0);
            if (severeCount > 0)
            {
                _logger.LogWarning("Watchdog: Agent emitted {Count} SEVERE event(s) before exiting.", severeCount);
            }

            var exitCode = GetAgentExitCode();
            KillAgent();

            // Pass whether a real agent process exited this cycle (vs. the pseudo-exit after a recovery
            // that was skipped because the controller was unreachable) so fallback isn't misattributed.
            ApplyAutoTransportFallback(agentActuallyExited: exitTask.IsCompleted);

            retryCount++;
            _logger.LogWarning("Watchdog: Agent exited (code: {Code}). Recovery attempt {Count}.",
                exitCode, retryCount);

            if (HasExceededMaxRetries(retryCount))
            {
                _logger.LogError("Watchdog: Max retries ({Max}) exceeded. Giving up.", _settings.Recovery.MaxRetries);
                return;
            }

            if (await RecoverAgent(retryCount, ct))
            {
                _restartCounter.Add(1);
            }
            else
            {
                // No new process was started. Reset the exit signal so the next iteration
                // waits on the stability timer instead of re-entering recovery immediately.
                _agentExitTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    /// <summary>
    /// Auto-mode transport fallback: when a <em>real</em> agent process exits before reaching the
    /// stability window, switch to the other transport for the next attempt (WebSocket ↔ Https).
    /// No-op unless the configured method is <c>Auto</c>, the run reached stability, or the "exit" was
    /// the pseudo-exit raised after a recovery skipped due to an unreachable controller.
    /// </summary>
    private void ApplyAutoTransportFallback(bool agentActuallyExited)
    {
        if (!ShouldFallbackTransport(_settings.Connection.Method, _currentRunReachedStability, agentActuallyExited))
        {
            return;
        }

        var previous = _effectiveMethod;
        _effectiveMethod = ToggleMethod(_effectiveMethod);
        _logger.LogWarning("Watchdog: {Previous} transport failed to stabilise — falling back to {Next}.",
            previous, _effectiveMethod);
    }

    /// <summary>
    /// True only for <c>Auto</c> when a <em>real</em> agent process exited before reaching the stability
    /// window. A run that stabilised, or a pseudo-exit from a recovery skipped due to an unreachable
    /// controller (<paramref name="agentActuallyExited"/> = false), must not trigger a transport switch.
    /// </summary>
    internal static bool ShouldFallbackTransport(
        ConnectionMethod configured, bool reachedStability, bool agentActuallyExited) =>
        configured == ConnectionMethod.Auto && agentActuallyExited && !reachedStability;

    private int GetAgentExitCode() =>
        _agentProcess is { HasExited: true } ? _agentProcess.ExitCode : UnknownExitCode;

    private bool HasExceededMaxRetries(int retryCount) =>
        _settings.Recovery.MaxRetries > 0 && retryCount > _settings.Recovery.MaxRetries;

    private static int ComputeBackoffDelaySeconds(int retryCount) =>
        (int)Math.Min(BackoffBaseSec * Math.Pow(BackoffMultiplier, retryCount - 1), BackoffMaxSec);

    private async Task<bool> RecoverAgent(int retryCount, CancellationToken ct)
    {
        var delay = ComputeBackoffDelaySeconds(retryCount);
        _logger.LogInformation("Watchdog: Waiting {Delay}s before retry...", delay);
        await Task.Delay(TimeSpan.FromSeconds(delay), ct);

        try
        {
            await TestConnectivity(ct);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Watchdog: Jenkins unreachable — skipping restart attempt. {Reason}", ex.Message);
            return false;
        }

        try
        {
            await _jarDownloader.Download(new Uri(_settings.Connection.Url), _dataDir, ct);
            _agentProcess = StartAgentProcess();
            _logger.LogInformation("Watchdog: Agent restarted (attempt {Count})", retryCount);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Watchdog: Recovery failed");
            return false;
        }
    }

    private void KillAgent()
    {
        if (_agentProcess is null)
        {
            return;
        }

        try
        {
            if (!_agentProcess.HasExited)
            {
                _logger.LogDebug("Killing agent process tree (PID: {Pid})", _agentProcess.Id);
                _agentProcess.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited — nothing to kill
        }
        finally
        {
            _agentProcess.Dispose();
            _agentProcess = null;
        }
    }

    public override void Dispose()
    {
        _meter.Dispose();
        base.Dispose();
    }
}
