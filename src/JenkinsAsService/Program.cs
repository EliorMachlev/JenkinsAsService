// Copyright (c) 2024 All rights reserved

using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using JenkinsAsService;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

const string UpdateSecretCommandName = "update-secret";
const string PurgeCommandName = PurgeCommand.Name;
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
const string JarFileName = AgentJar.FileName;
const long FileSizeLimitBytes = 10 * 1024 * 1024;
const string TelemetrySectionName = ConfigKeys.TelemetrySection;
const string LogOutputTemplate =
    "{ProcessId} | {Timestamp:yyyy-MM-dd HH:mm:ss} | {Level} | {Message:lj}{NewLine}{Exception}";

// CLI mode — if args[0] is a known command, handle it and exit; otherwise fall through to service mode.
// IMPORTANT: this dispatch must stay before Serilog init — Environment.Exit skips the
// finally { Log.CloseAndFlushAsync() } block below, which is correct (no logger to flush).
var commands = new Dictionary<string, Func<string[], int>>(StringComparer.Ordinal)
{
    [UpdateSecretCommandName] = UpdateSecretCommand.Run,
    [PurgeCommandName] = args => PurgeCommand.Run(args),
};

if (args.Length > 0 && commands.TryGetValue(args[0], out var command))
{
    Environment.Exit(command(args));
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

    ConfigureJarDownloadClient(builder, builder.Configuration.GetSection(ConfigSectionName));
    RegisterCoreServices(builder);
    ConfigureTelemetry(builder);

    return builder.Build();
}

// The agent.jar client is the only outbound HTTP in the service, and the only place transport policy
// (resilience, proxy, certificate pinning) is decided.
static void ConfigureJarDownloadClient(HostApplicationBuilder builder, IConfigurationSection jenkinsConfig)
{
    var jarClient = builder.Services.AddHttpClient(JarDownloaderClientName);
    jarClient.AddStandardResilienceHandler();

    var thumbprint = jenkinsConfig[ControllerCertThumbprintKey];
    var pinning = !string.IsNullOrWhiteSpace(thumbprint);
    var proxyMode = ProxyResolver.ClassifyMode(jenkinsConfig[ProxyKey]);

    if (!pinning && proxyMode == ProxyMode.System)
    {
        return; // Default handler: system proxy, standard chain validation. Nothing to override.
    }

    if (pinning)
    {
        Log.Information("Controller certificate pinning enabled for {Jar} download", JarFileName);
    }

    // Resolved eagerly so a malformed address fails at startup with a clear message, rather than on the
    // first download attempt where it would look like an unreachable controller.
    var proxy = ProxyResolver.Create(jenkinsConfig[ProxyKey], jenkinsConfig[ProxyBypassKey]);
    LogProxySelection(proxyMode, (proxy as WebProxy)?.Address);

    // Pinning and the proxy both replace parts of the primary handler, so they are applied in ONE callback —
    // calling ConfigurePrimaryHttpMessageHandler twice would leave the later handler discarding the earlier.
    jarClient.ConfigurePrimaryHttpMessageHandler(
        () => BuildJarHandler(proxyMode, proxy, pinning ? thumbprint : null));
}

static SocketsHttpHandler BuildJarHandler(ProxyMode proxyMode, IWebProxy? proxy, string? pinnedThumbprint)
{
    var handler = new SocketsHttpHandler();

    switch (proxyMode)
    {
        case ProxyMode.Direct:
            handler.UseProxy = false;
            break;
        case ProxyMode.Explicit when proxy is not null:
            handler.Proxy = proxy;
            handler.UseProxy = true;
            break;
        default:
            break; // ProxyMode.System — leave the handler's inherited default in place.
    }

    if (pinnedThumbprint is not null)
    {
        handler.SslOptions = new SslClientAuthenticationOptions
        {
            // Pin replaces chain trust: accept the connection only if the server certificate's SHA-256
            // thumbprint matches, rejecting any other certificate (incl. a chain-trusted MITM).
            RemoteCertificateValidationCallback = (_, cert, _, _) =>
                CertificateThumbprintValidator.Matches(cert as X509Certificate2, pinnedThumbprint)
        };
    }

    return handler;
}

static void RegisterCoreServices(HostApplicationBuilder builder)
{
    builder.Services.AddSingleton<IJarDownloader, HttpJarDownloader>();
    builder.Services.AddSingleton<IConnectivityChecker, TcpConnectivityChecker>();
    builder.Services.AddSingleton<ISecretResolver, SecretResolver>();
    builder.Services.AddSingleton<IAgentProcessLauncher, AgentProcessLauncher>();
    builder.Services.AddHostedService<LogLevelController>();
    builder.Services.AddHostedService<JenkinsAgentWorker>();

    builder.Logging.ClearProviders();
    builder.Services.AddSerilog();
}

static void ConfigureTelemetry(HostApplicationBuilder builder)
{
    var telemetry = builder.Configuration.GetSection(TelemetrySectionName).Get<TelemetrySettings>()
                    ?? new TelemetrySettings();

    if (!telemetry.Enabled)
    {
        return;
    }

    if (string.IsNullOrWhiteSpace(telemetry.OtlpEndpoint))
    {
        Log.Warning("Telemetry is enabled but OtlpEndpoint is not configured — metrics will not be exported.");
        return;
    }

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService(telemetry.ServiceName))
        .WithMetrics(metrics =>
        {
            metrics.AddMeter(JenkinsAgentWorker.MeterName);
            metrics.AddRuntimeInstrumentation();
            metrics.AddOtlpExporter(o => o.Endpoint = new Uri(telemetry.OtlpEndpoint));
        });
}

// Worth a line at startup: a proxy that is configured but cannot reach the controller is otherwise
// indistinguishable from the controller being down.
static void LogProxySelection(ProxyMode mode, Uri? address)
{
    switch (mode)
    {
        case ProxyMode.Direct:
            Log.Information("Proxy: bypassed for the {Jar} download (Connection:Proxy = direct)", JarFileName);
            break;
        case ProxyMode.Explicit when address is not null:
            Log.Information("Proxy: {Address} for the {Jar} download", address, JarFileName);
            break;
        default:
            break; // ProxyMode.System — the inherited proxy is not ours to describe.
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
