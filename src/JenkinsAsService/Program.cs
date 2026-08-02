// Copyright (c) 2024 All rights reserved

using JenkinsAsService;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

const string UpdateSecretCommandName = "update-secret";
const string ConfigFileName = ConfigKeys.FileName;
const string ConfigSectionName = ConfigKeys.Section;
const string DebugModeKey = ConfigKeys.Logging.DebugModePath;
const string CompactLogKey = ConfigKeys.Logging.CompactLogPath;
const string RetainedLogsKey = ConfigKeys.Logging.RetainedLogsPath;
const string DataDirectoryKey = ConfigKeys.Agent.DataDirectoryPath;
const string ControllerCertThumbprintKey = ConfigKeys.Connection.ControllerCertThumbprintPath;
const string ProxyKey = ConfigKeys.Connection.ProxyPath;
const string ProxyBypassKey = ConfigKeys.Connection.ProxyBypassPath;
const int DefaultRetainedLogs = 3;
const string ServiceName = "Jenkins";
const string JarDownloaderClientName = HttpJarDownloader.ClientName;
const string EventLogSource = EventLogSourceInstaller.DefaultSource;
const string EventLogName = EventLogSourceInstaller.DefaultLogName;
const string TextLogFileName = "agent.log";
const string CompactLogFileName = "agent.clef";
const long FileSizeLimitBytes = 10 * 1024 * 1024;
const string TelemetrySectionName = ConfigKeys.TelemetrySection;
const string LogOutputTemplate =
    "{ProcessId} | {Timestamp:yyyy-MM-dd HH:mm:ss} | {Level} | {Message:lj}{NewLine}{Exception}";

// CLI mode — if args contain a known command, handle it and exit.
// IMPORTANT: this dispatch must stay before Serilog init — Environment.Exit skips the
// finally { Log.CloseAndFlushAsync() } block below, which is correct (no logger to flush).
if (args.Length > 0 && args[0] == UpdateSecretCommandName)
{
    Environment.Exit(UpdateSecretCommand.Run(args));
}

// Service mode
var basePath = AppContext.BaseDirectory;

// Read config early to determine log level
var bootConfig = new ConfigurationBuilder()
    .SetBasePath(basePath)
    .AddJsonFile(ConfigFileName, optional: true)
    .Build();

var jenkinsSection = bootConfig.GetSection(ConfigSectionName);
var debugMode = jenkinsSection.GetValue<bool>(DebugModeKey);
var compactLog = jenkinsSection.GetValue<bool>(CompactLogKey);
var retainedLogs = jenkinsSection.GetValue<int?>(RetainedLogsKey) ?? DefaultRetainedLogs;

// Logs go to the writable data directory (separate from the read-only install folder), not next to
// the binary. Resolved here so the file sink points at the same place the worker writes its artifacts.
var dataDir = DataPaths.ResolveDataDirectory(jenkinsSection[DataDirectoryKey]);

Log.Logger = BuildLogger(debugMode, compactLog, retainedLogs, dataDir);

// Harden the service process: block remote/low-integrity/non-System32 DLL loads and legacy
// extension-point injection. Affects future LoadLibrary calls in this process only (not the Java
// child). Best-effort — never blocks startup. See ProcessMitigations for the rationale on the subset.
ProcessMitigations.Apply(msg => Log.Warning("{Warning}", msg));

try
{
    var host = BuildHost(args, basePath);

    if (!ValidateBoundSettings(host, basePath))
    {
        return;
    }

    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Service terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}

static Serilog.Core.Logger BuildLogger(bool debugMode, bool compactLog, int retainedLogs, string logDirectory)
{
    // Level comes from a switch, not a fixed value, so LogLevelController can re-point it when
    // Logging:DebugMode changes on disk — no service restart, no dropped agent, mid-investigation.
    LogLevelController.Switch.MinimumLevel = LogLevelController.LevelFor(debugMode);

    var logConfig = new LoggerConfiguration()
        .MinimumLevel.ControlledBy(LogLevelController.Switch)
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
        .MinimumLevel.Override("Polly", LogEventLevel.Warning)
        .Enrich.WithProcessId()
        .Enrich.WithMachineName()
        .Enrich.WithEnvironmentName();

    ConfigureFileSink(logConfig, compactLog, retainedLogs, logDirectory);

    // The low-privilege service account cannot create the event source (HKLM write). The installer
    // pre-creates it as SYSTEM; here we only attach the sink if the source is usable, and never let
    // the sink attempt creation (manageEventSource: false) so startup can't fail on a registry write.
    if (EventLogSourceInstaller.Ensure(EventLogSource, EventLogName))
    {
        logConfig.WriteTo.EventLog(
            source: EventLogSource,
            logName: EventLogName,
            restrictedToMinimumLevel: LogEventLevel.Warning,
            manageEventSource: false);
    }

    return logConfig.CreateLogger();
}

static void ConfigureFileSink(LoggerConfiguration logConfig, bool compactLog, int retainedLogs, string logDirectory)
{
    if (compactLog)
    {
        logConfig.WriteTo.File(
            formatter: new CompactJsonFormatter(),
            path: Path.Combine(logDirectory, CompactLogFileName),
            rollingInterval: RollingInterval.Infinite,
            rollOnFileSizeLimit: true,
            fileSizeLimitBytes: FileSizeLimitBytes,
            retainedFileCountLimit: retainedLogs);
    }
    else
    {
        logConfig.WriteTo.File(
            path: Path.Combine(logDirectory, TextLogFileName),
            rollingInterval: RollingInterval.Infinite,
            rollOnFileSizeLimit: true,
            fileSizeLimitBytes: FileSizeLimitBytes,
            retainedFileCountLimit: retainedLogs,
            outputTemplate: LogOutputTemplate);
    }
}

static IHost BuildHost(string[] args, string basePath)
{
    var builder = Host.CreateApplicationBuilder(args);

    // Pin configuration to the INSTALL folder, not the current directory. The host's default content root is
    // the process working directory, which for a service started by SCM is %SystemRoot%\system32 — so without
    // this the bound settings come back empty and the service dies claiming Connection:Url is missing while
    // naming a config file that is fully populated. reloadOnChange also makes Logging:DebugMode live-toggleable
    // (see LogLevelController); it is added last so it wins over the default source.
    builder.Configuration.AddJsonFile(
        Path.Combine(basePath, ConfigFileName), optional: true, reloadOnChange: true);

    builder.Services.AddWindowsService(options => options.ServiceName = ServiceName);
    builder.Services.Configure<ServiceSettings>(builder.Configuration.GetSection(ConfigSectionName));

    var jenkinsConfig = builder.Configuration.GetSection(ConfigSectionName);
    var pinnedThumbprint = jenkinsConfig[ControllerCertThumbprintKey];
    var proxy = jenkinsConfig[ProxyKey];
    var proxyBypass = jenkinsConfig[ProxyBypassKey];
    var jarClient = builder.Services.AddHttpClient(JarDownloaderClientName);
    jarClient.AddStandardResilienceHandler();

    // Pinning and the proxy both replace parts of the primary handler, so they are applied together —
    // configuring it twice would leave whichever ran last silently discarding the other's settings.
    var proxyMode = ProxyResolver.ClassifyMode(proxy);
    var pinning = !string.IsNullOrWhiteSpace(pinnedThumbprint);
    if (pinning || proxyMode != ProxyMode.System)
    {
        if (pinning)
        {
            Log.Information("Controller certificate pinning enabled for {Jar} download", "agent.jar");
        }

        // Parsed eagerly so a malformed address fails at startup with a clear message, rather than on the
        // first download attempt where it would look like an unreachable controller.
        var webProxy = ProxyResolver.Create(proxy, proxyBypass);
        LogProxyMode(proxyMode, webProxy);

        jarClient.ConfigurePrimaryHttpMessageHandler(() =>
        {
            var handler = new SocketsHttpHandler();

            if (proxyMode == ProxyMode.Direct)
            {
                handler.UseProxy = false;
            }
            else if (webProxy is not null)
            {
                handler.Proxy = webProxy;
                handler.UseProxy = true;
            }

            if (pinning)
            {
                handler.SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    // Pin replaces chain trust: accept the connection only if the server certificate's
                    // SHA-256 thumbprint matches, rejecting any other certificate (incl. chain-trusted MITM).
                    RemoteCertificateValidationCallback = (_, cert, _, _) =>
                        CertificateThumbprintValidator.Matches(
                            cert as System.Security.Cryptography.X509Certificates.X509Certificate2, pinnedThumbprint)
                };
            }

            return handler;
        });
    }
    builder.Services.AddSingleton<IJarDownloader, HttpJarDownloader>();
    builder.Services.AddSingleton<IConnectivityChecker, TcpConnectivityChecker>();
    builder.Services.AddSingleton<ISecretResolver, SecretResolver>();
    builder.Services.AddSingleton<IAgentProcessLauncher, AgentProcessLauncher>();
    builder.Services.AddHostedService<LogLevelController>();
    builder.Services.AddHostedService<JenkinsAgentWorker>();

    builder.Logging.ClearProviders();
    builder.Services.AddSerilog();

    var telemetry = builder.Configuration.GetSection(TelemetrySectionName).Get<TelemetrySettings>() ?? new TelemetrySettings();
    if (telemetry.Enabled)
    {
        if (string.IsNullOrWhiteSpace(telemetry.OtlpEndpoint))
        {
            Log.Warning("Telemetry is enabled but OtlpEndpoint is not configured — metrics will not be exported.");
        }
        else
        {
            builder.Services.AddOpenTelemetry()
                .ConfigureResource(r => r.AddService(telemetry.ServiceName))
                .WithMetrics(metrics =>
                {
                    metrics.AddMeter(JenkinsAgentWorker.MeterName);
                    metrics.AddRuntimeInstrumentation();
                    metrics.AddOtlpExporter(o => o.Endpoint = new Uri(telemetry.OtlpEndpoint));
                });
        }
    }

    return builder.Build();
}

// Says which proxy the jar download will use. Worth a line at startup: a proxy that is configured but not
// reaching the controller is otherwise indistinguishable from the controller being down.
static void LogProxyMode(ProxyMode mode, System.Net.IWebProxy? webProxy)
{
    switch (mode)
    {
        case ProxyMode.Direct:
            Log.Information("Proxy: bypassed for the {Jar} download (Connection:Proxy = direct)", "agent.jar");
            break;
        case ProxyMode.Explicit when webProxy is System.Net.WebProxy configured:
            Log.Information("Proxy: {Address} for the {Jar} download", configured.Address, "agent.jar");
            break;
        default:
            break;
    }
}

static bool ValidateBoundSettings(IHost host, string basePath)
{
    var settings = host.Services.GetRequiredService<IOptions<ServiceSettings>>().Value;
    try
    {
        // Single source of truth for mandatory-field/URL rules (empty Url/Secret, explicit port, valid URL),
        // so a bad config fails the clean pre-flight here instead of faulting later inside the worker.
        ServiceSettingsValidator.Validate(settings);
        return true;
    }
    catch (InvalidOperationException ex)
    {
        var configPath = Path.Combine(basePath, ConfigFileName);
        Log.Error("Invalid configuration: {Reason} Fix: {Path}", ex.Message, configPath);
        return false;
    }
}
