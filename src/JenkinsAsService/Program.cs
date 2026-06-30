// Copyright (c) 2024 All rights reserved

using JenkinsAsService;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

const string UpdateSecretCommandName = "update-secret";
const string ConfigFileName = "appsettings.json";
const string ConfigSectionName = "Jenkins";
const string DebugModeKey = "DebugMode";
const string CompactLogKey = "CompactLog";
const string RetainedLogsKey = "RetainedLogs";
const int DefaultRetainedLogs = 3;
const string ServiceName = "Jenkins";
const string JarDownloaderClientName = "JarDownloader";
const string EventLogSource = "JenkinsAsService";
const string EventLogName = "Application";
const string TextLogFileName = "agent.log";
const string CompactLogFileName = "agent.clef";
const long FileSizeLimitBytes = 10 * 1024 * 1024;
const string TelemetrySectionName = "Telemetry";
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

Log.Logger = BuildLogger(debugMode, compactLog, retainedLogs, basePath);

// Harden the service process: block remote/low-integrity/non-System32 DLL loads and legacy
// extension-point injection. Affects future LoadLibrary calls in this process only (not the Java
// child). Best-effort — never blocks startup. See ProcessMitigations for the rationale on the subset.
ProcessMitigations.Apply(msg => Log.Warning("{Warning}", msg));

try
{
    var host = BuildHost(args);

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

static Serilog.Core.Logger BuildLogger(bool debugMode, bool compactLog, int retainedLogs, string basePath)
{
    var logConfig = new LoggerConfiguration()
        .MinimumLevel.Is(debugMode ? LogEventLevel.Debug : LogEventLevel.Information)
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
        .MinimumLevel.Override("Polly", LogEventLevel.Warning)
        .Enrich.WithProcessId()
        .Enrich.WithMachineName()
        .Enrich.WithEnvironmentName();

    ConfigureFileSink(logConfig, compactLog, retainedLogs, basePath);

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

static void ConfigureFileSink(LoggerConfiguration logConfig, bool compactLog, int retainedLogs, string basePath)
{
    if (compactLog)
    {
        logConfig.WriteTo.File(
            formatter: new CompactJsonFormatter(),
            path: Path.Combine(basePath, CompactLogFileName),
            rollingInterval: RollingInterval.Infinite,
            rollOnFileSizeLimit: true,
            fileSizeLimitBytes: FileSizeLimitBytes,
            retainedFileCountLimit: retainedLogs);
    }
    else
    {
        logConfig.WriteTo.File(
            path: Path.Combine(basePath, TextLogFileName),
            rollingInterval: RollingInterval.Infinite,
            rollOnFileSizeLimit: true,
            fileSizeLimitBytes: FileSizeLimitBytes,
            retainedFileCountLimit: retainedLogs,
            outputTemplate: LogOutputTemplate);
    }
}

static IHost BuildHost(string[] args)
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddWindowsService(options => options.ServiceName = ServiceName);
    builder.Services.Configure<ServiceSettings>(builder.Configuration.GetSection(ConfigSectionName));

    var pinnedThumbprint = builder.Configuration.GetSection(ConfigSectionName)["ControllerCertThumbprint"];
    var jarClient = builder.Services.AddHttpClient(JarDownloaderClientName);
    jarClient.AddStandardResilienceHandler();

    if (!string.IsNullOrWhiteSpace(pinnedThumbprint))
    {
        Log.Information("Controller certificate pinning enabled for {Jar} download", "agent.jar");
        jarClient.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                // Pin replaces chain trust: accept the connection only if the server certificate's
                // SHA-256 thumbprint matches, rejecting any other certificate (incl. chain-trusted MITM).
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    CertificateThumbprintValidator.Matches(
                        cert as System.Security.Cryptography.X509Certificates.X509Certificate2, pinnedThumbprint)
            }
        });
    }
    builder.Services.AddSingleton<IJarDownloader, HttpJarDownloader>();
    builder.Services.AddSingleton<IConnectivityChecker, TcpConnectivityChecker>();
    builder.Services.AddSingleton<ISecretResolver, SecretResolver>();
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
                    metrics.AddMeter("JenkinsAsService");
                    metrics.AddRuntimeInstrumentation();
                    metrics.AddOtlpExporter(o => o.Endpoint = new Uri(telemetry.OtlpEndpoint));
                });
        }
    }

    return builder.Build();
}

static bool ValidateBoundSettings(IHost host, string basePath)
{
    var settings = host.Services.GetRequiredService<IOptions<ServiceSettings>>().Value;
    if (string.IsNullOrWhiteSpace(settings.JenkinsUrl) || string.IsNullOrWhiteSpace(settings.AgentSecret))
    {
        var configPath = Path.Combine(basePath, ConfigFileName);
        Log.Error("Mandatory fields (JenkinsUrl, AgentSecret) are empty. Fill in: {Path}", configPath);
        return false;
    }

    return true;
}
