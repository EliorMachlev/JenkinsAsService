using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace JenkinsAsService;

public sealed class JenkinsAgentWorker : BackgroundService
{
    // ─── Watchdog / recovery timing ───────────────────────────────────────────
    private const int WatchdogPollMs = 5_000;
    private const int BackoffBaseSec = 10;
    private const int BackoffMaxSec = 300;
    private const int StabilityMs = 60_000;
    private const int StabilitySeconds = StabilityMs / 1_000;
    private const double BackoffMultiplier = 2;
    private const int UnknownExitCode = -1;

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

    // ─── Agent stdout/stderr log-level prefixes ───────────────────────────────
    private const string InfoPrefix = "INFO: ";
    private const string WarningPrefix = "WARNING: ";
    private const string SeverePrefix = "SEVERE: ";

    // ─── Misc ─────────────────────────────────────────────────────────────────
    private const string SecretRedaction = "*****";
    private const string UrlPathSeparator = "/";
    private const char TrailingSlash = '/';
    private const int ConnectivityTimeoutMs = 2_000;
    private const int BannerWidth = 80;
    private const char BannerChar = '=';

    private readonly ILogger<JenkinsAgentWorker> _logger;
    private readonly ServiceSettings _settings;
    private readonly IJarDownloader _jarDownloader;
    private readonly IConnectivityChecker _connectivityChecker;
    private readonly ISecretResolver _secretResolver;
    private readonly string _basePath;
    private string _javaExe = "";
    private string _agentName = "";
    private string _resolvedSecret = "";
    private Process? _agentProcess;

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
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            LogStartupBanner();

            ValidateSettings();
            ResolveJavaPath();
            await TestConnectivityAsync(stoppingToken);
            await _jarDownloader.DownloadAsync(_settings.JenkinsURL, _basePath, stoppingToken);

            _agentProcess = StartAgentProcess();
            await RunWatchdogAsync(stoppingToken);
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
        if (string.IsNullOrWhiteSpace(settings.JenkinsURL))
            throw new InvalidOperationException("'JenkinsURL' is a mandatory field.");

        if (string.IsNullOrWhiteSpace(settings.AgentSecret))
            throw new InvalidOperationException("'AgentSecret' is a mandatory field.");

        var uri = new Uri(settings.JenkinsURL);
        if (uri.IsDefaultPort)
            throw new InvalidOperationException(
                $"JenkinsURL must include an explicit port: '{settings.JenkinsURL}'. " +
                "Example: https://jenkins.example.com:8443");
    }

    private void ValidateSettings()
    {
        ValidateSettings(_settings);

        var uri = new Uri(_settings.JenkinsURL);
        if (uri.Scheme != Uri.UriSchemeHttps)
            _logger.LogWarning("JenkinsURL uses {Scheme} — agent secret will be sent unencrypted. Consider HTTPS.", uri.Scheme);

        if (string.IsNullOrWhiteSpace(_settings.AgentName))
        {
            _agentName = Environment.MachineName;
            _logger.LogDebug("AgentName is empty. Using hostname '{Name}'", _agentName);
        }
        else
        {
            _agentName = _settings.AgentName;
        }

        _resolvedSecret = _secretResolver.Resolve(_settings);
        _logger.LogInformation("Secret resolved via {Mode} mode", _settings.SecretMode);
    }

    internal static string ResolveJavaPath(string? configuredPath, string? javaHome)
    {
        var candidates = new List<string>(3);

        if (!string.IsNullOrWhiteSpace(configuredPath))
            candidates.Add(configuredPath);

        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            candidates.Add(javaHome);
            candidates.Add(Path.Combine(javaHome, JavaBinFolder));
        }

        foreach (var path in candidates)
        {
            var exe = Path.Combine(path, JavaExeFilename);
            if (File.Exists(exe))
                return exe;
        }

        throw new InvalidOperationException(
            "Cannot find java.exe. Set 'JavaPath' in appsettings.json or the JAVA_HOME environment variable.");
    }

    private void ResolveJavaPath()
    {
        _javaExe = ResolveJavaPath(_settings.JavaPath, Environment.GetEnvironmentVariable("JAVA_HOME"));
        _logger.LogDebug("Resolved Java at: '{Path}'", Path.GetDirectoryName(_javaExe));
    }

    private async Task TestConnectivityAsync(CancellationToken ct)
    {
        var uri = new Uri(_settings.JenkinsURL);
        await _connectivityChecker.TestAsync(uri.Host, uri.Port, ConnectivityTimeoutMs, ct);
    }

    // ─── Agent Process ──────────────────────────────────────────────────────

    private Process StartAgentProcess()
    {
        var psi = BuildProcessStartInfo();

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) ParseAgentOutput(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) ParseAgentOutput(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _logger.LogInformation("Jenkins Agent started (Java PID: {JavaPid})", process.Id);
        return process;
    }

    private ProcessStartInfo BuildProcessStartInfo()
    {
        var jarPath = Path.Combine(_basePath, JarFilename);
        var normalizedUrl = $"{_settings.JenkinsURL.TrimEnd(TrailingSlash)}{UrlPathSeparator}";

        var psi = new ProcessStartInfo(_javaExe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _basePath
        };

        psi.ArgumentList.Add(ArgJar);
        psi.ArgumentList.Add(jarPath);
        psi.ArgumentList.Add(ArgUrl);
        psi.ArgumentList.Add(normalizedUrl);
        psi.ArgumentList.Add(ArgSecret);
        psi.ArgumentList.Add(_resolvedSecret);
        psi.ArgumentList.Add(ArgName);
        psi.ArgumentList.Add(_agentName);
        psi.ArgumentList.Add(ArgWorkDir);
        psi.ArgumentList.Add(_basePath);

        foreach (var arg in ParseArguments(_settings.CustomArguments))
            psi.ArgumentList.Add(arg);

        return psi;
    }

    internal static List<string> ParseArguments(string? input)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(input))
            return result;

        int i = 0;

        while (i < input.Length)
        {
            while (i < input.Length && char.IsWhiteSpace(input[i]))
                i++;

            if (i >= input.Length)
                break;

            if (input[i] == '"')
            {
                // Quoted argument — find closing quote, respecting \"
                i++;
                var buf = new System.Text.StringBuilder();
                while (i < input.Length && input[i] != '"')
                {
                    if (input[i] == '\\' && i + 1 < input.Length && input[i + 1] == '"')
                    {
                        buf.Append('"');
                        i += 2;
                    }
                    else
                    {
                        buf.Append(input[i]);
                        i++;
                    }
                }
                result.Add(buf.ToString());
                if (i < input.Length) i++; // skip closing quote
            }
            else
            {
                // Unquoted argument — find next whitespace
                int start = i;
                while (i < input.Length && !char.IsWhiteSpace(input[i]))
                    i++;
                result.Add(input[start..i]);
            }
        }

        return result;
    }

    internal void ParseAgentOutput(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        // Redact resolved secret in case Jenkins emits it on handshake failure
        if (!string.IsNullOrEmpty(_resolvedSecret))
            line = line.Replace(_resolvedSecret, SecretRedaction, StringComparison.Ordinal);

        if (line.StartsWith(InfoPrefix, StringComparison.Ordinal))
            _logger.LogInformation("{Output}", line[InfoPrefix.Length..]);
        else if (line.StartsWith(WarningPrefix, StringComparison.Ordinal))
            _logger.LogWarning("{Output}", line[WarningPrefix.Length..]);
        else if (line.StartsWith(SeverePrefix, StringComparison.Ordinal))
            _logger.LogError("{Output}", line[SeverePrefix.Length..]);
        else if (_settings.DebugMode)
            _logger.LogDebug("{Output}", line);
        else
            _logger.LogInformation("{Output}", line);
    }

    // ─── Watchdog ───────────────────────────────────────────────────────────

    private async Task RunWatchdogAsync(CancellationToken ct)
    {
        var retryCount = 0;
        var lastRestartTime = DateTime.UtcNow;

        _logger.LogInformation("Watchdog started");

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(WatchdogPollMs, ct);

            if (_agentProcess is { HasExited: false })
            {
                if (IsAgentStable(retryCount, lastRestartTime))
                {
                    retryCount = 0;
                    _logger.LogInformation("Watchdog: Agent stable for {Seconds}s. Retry counter reset.", StabilitySeconds);
                }
                continue;
            }

            // ── Agent died — begin recovery ──
            var exitCode = GetAgentExitCode();
            KillAgent();

            retryCount++;
            _logger.LogWarning("Watchdog: Agent exited (code: {Code}). Recovery attempt {Count}.",
                exitCode, retryCount);

            if (HasExceededMaxRetries(retryCount))
            {
                _logger.LogError("Watchdog: Max retries ({Max}) exceeded. Giving up.", _settings.MaxRetries);
                break;
            }

            if (await RecoverAgentAsync(retryCount, ct))
                lastRestartTime = DateTime.UtcNow;
        }
    }

    private bool IsAgentStable(int retryCount, DateTime lastRestartTime) =>
        retryCount > 0 && (DateTime.UtcNow - lastRestartTime).TotalMilliseconds >= StabilityMs;

    private int GetAgentExitCode() =>
        _agentProcess?.HasExited == true ? _agentProcess.ExitCode : UnknownExitCode;

    private bool HasExceededMaxRetries(int retryCount) =>
        _settings.MaxRetries > 0 && retryCount > _settings.MaxRetries;

    private static int ComputeBackoffDelaySeconds(int retryCount) =>
        (int)Math.Min(BackoffBaseSec * Math.Pow(BackoffMultiplier, retryCount - 1), BackoffMaxSec);

    private async Task<bool> RecoverAgentAsync(int retryCount, CancellationToken ct)
    {
        var delay = ComputeBackoffDelaySeconds(retryCount);
        _logger.LogInformation("Watchdog: Waiting {Delay}s before retry...", delay);
        await Task.Delay(TimeSpan.FromSeconds(delay), ct);

        try
        {
            await _jarDownloader.DownloadAsync(_settings.JenkinsURL, _basePath, ct);
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
        if (_agentProcess is null) return;

        try
        {
            if (!_agentProcess.HasExited)
                _agentProcess.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        finally
        {
            _agentProcess.Dispose();
            _agentProcess = null;
        }
    }
}
