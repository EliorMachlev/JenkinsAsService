using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace JenkinsAsService;

public sealed class JenkinsAgentWorker : BackgroundService
{
    private const int WatchdogPollMs = 5_000;
    private const int BackoffBaseSec = 10;
    private const int BackoffMaxSec = 300;
    private const int StabilityMs = 60_000;
    private const string JarFilename = "agent.jar";

    private readonly ILogger<JenkinsAgentWorker> _logger;
    private readonly ServiceSettings _settings;
    private readonly IJarDownloader _jarDownloader;
    private readonly IConnectivityChecker _connectivityChecker;
    private readonly string _basePath;
    private string _javaExe = "";
    private string _agentName = "";
    private Process? _agentProcess;

    public JenkinsAgentWorker(
        ILogger<JenkinsAgentWorker> logger,
        IOptions<ServiceSettings> settings,
        IJarDownloader jarDownloader,
        IConnectivityChecker connectivityChecker)
    {
        _logger = logger;
        _settings = settings.Value;
        _jarDownloader = jarDownloader;
        _connectivityChecker = connectivityChecker;
        _basePath = AppContext.BaseDirectory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("{Sep}", new string('=', 80));
            _logger.LogInformation("=== SERVICE STARTING (PID: {Pid}) ===", Environment.ProcessId);
            _logger.LogInformation("{Sep}", new string('=', 80));

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
    }

    internal static string ResolveJavaPath(string? configuredPath, string? javaHome)
    {
        var candidates = new List<string>(3);

        if (!string.IsNullOrWhiteSpace(configuredPath))
            candidates.Add(configuredPath);

        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            candidates.Add(javaHome);
            candidates.Add(Path.Combine(javaHome, "bin"));
        }

        foreach (var path in candidates)
        {
            var exe = Path.Combine(path, "java.exe");
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
        await _connectivityChecker.TestAsync(uri.Host, uri.Port, 2_000, ct);
    }

    // ─── Agent Process ──────────────────────────────────────────────────────

    private Process StartAgentProcess()
    {
        var jarPath = Path.Combine(_basePath, JarFilename);

        var psi = new ProcessStartInfo(_javaExe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _basePath
        };

        psi.ArgumentList.Add("-jar");
        psi.ArgumentList.Add(jarPath);
        psi.ArgumentList.Add("-url");
        psi.ArgumentList.Add($"{_settings.JenkinsURL.TrimEnd('/')}/");
        psi.ArgumentList.Add("-secret");
        psi.ArgumentList.Add(_settings.AgentSecret);
        psi.ArgumentList.Add("-name");
        psi.ArgumentList.Add(_agentName);
        psi.ArgumentList.Add("-workDir");
        psi.ArgumentList.Add(_basePath);

        foreach (var arg in ParseArguments(_settings.CustomArguments))
            psi.ArgumentList.Add(arg);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) ParseAgentOutput(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) ParseAgentOutput(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _logger.LogInformation("Jenkins Agent started (Java PID: {JavaPid})", process.Id);
        return process;
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

        if (line.StartsWith("INFO: ", StringComparison.Ordinal))
            _logger.LogInformation("{Output}", line[6..]);
        else if (line.StartsWith("WARNING: ", StringComparison.Ordinal))
            _logger.LogWarning("{Output}", line[9..]);
        else if (line.StartsWith("SEVERE: ", StringComparison.Ordinal))
            _logger.LogError("{Output}", line[8..]);
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
                if (retryCount > 0 && (DateTime.UtcNow - lastRestartTime).TotalMilliseconds >= StabilityMs)
                {
                    retryCount = 0;
                    _logger.LogInformation("Watchdog: Agent stable for 60s. Retry counter reset.");
                }
                continue;
            }

            // ── Agent died — begin recovery ──
            var exitCode = _agentProcess?.HasExited == true ? _agentProcess.ExitCode : -1;
            KillAgent();

            retryCount++;
            _logger.LogWarning("Watchdog: Agent exited (code: {Code}). Recovery attempt {Count}.",
                exitCode, retryCount);

            if (_settings.MaxRetries > 0 && retryCount > _settings.MaxRetries)
            {
                _logger.LogError("Watchdog: Max retries ({Max}) exceeded. Giving up.", _settings.MaxRetries);
                break;
            }

            var delay = (int)Math.Min(BackoffBaseSec * Math.Pow(2, retryCount - 1), BackoffMaxSec);
            _logger.LogInformation("Watchdog: Waiting {Delay}s before retry...", delay);
            await Task.Delay(TimeSpan.FromSeconds(delay), ct);

            try
            {
                await _jarDownloader.DownloadAsync(_settings.JenkinsURL, _basePath, ct);
                _agentProcess = StartAgentProcess();
                lastRestartTime = DateTime.UtcNow;
                _logger.LogInformation("Watchdog: Agent restarted (attempt {Count})", retryCount);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Watchdog: Recovery failed");
            }
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
