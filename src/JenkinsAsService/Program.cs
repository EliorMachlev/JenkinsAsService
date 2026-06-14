using JenkinsAsService;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

const string UpdateSecretCommandName = "update-secret";
const string ConfigFileName = "appsettings.json";
const string ConfigSectionName = "Jenkins";
const string DebugModeKey = "DebugMode";
const string CompactLogKey = "CompactLog";
const string ServiceName = "Jenkins";
const string JarDownloaderClientName = "JarDownloader";
const string EventLogSource = "JenkinsAsService";
const string EventLogName = "Application";
const string TextLogFileName = "agent.log";
const string CompactLogFileName = "agent.clef";
const long FileSizeLimitBytes = 10 * 1024 * 1024;
const int RetainedFileCount = 3;
const string LogOutputTemplate =
    "{Pid} | {Timestamp:yyyy-MM-dd HH:mm:ss} | {Level} | {Message:lj}{NewLine}{Exception}";

// CLI mode — if args contain a known command, handle it and exit.
// IMPORTANT: this dispatch must stay before Serilog init — Environment.Exit skips the
// finally { Log.CloseAndFlushAsync() } block below, which is correct (no logger to flush).
if (args.Length > 0 && args[0] == UpdateSecretCommandName)
{
    Environment.Exit(UpdateSecretCommand.Run(args));
}

// Service mode — existing code below
var basePath = AppContext.BaseDirectory;

// Read config early to determine log level
var bootConfig = new ConfigurationBuilder()
    .SetBasePath(basePath)
    .AddJsonFile(ConfigFileName, optional: true)
    .Build();

var jenkinsSection = bootConfig.GetSection(ConfigSectionName);
var debugMode = jenkinsSection.GetValue<bool>(DebugModeKey);
var compactLog = jenkinsSection.GetValue<bool>(CompactLogKey);

Log.Logger = BuildLogger(debugMode, compactLog, basePath);

try
{
    var host = BuildHost(args);

    if (!ValidateBoundSettings(host, basePath))
        return;

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

static Serilog.Core.Logger BuildLogger(bool debugMode, bool compactLog, string basePath)
{
    var logConfig = new LoggerConfiguration()
        .MinimumLevel.Is(debugMode ? LogEventLevel.Debug : LogEventLevel.Information)
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
        .MinimumLevel.Override("Polly", LogEventLevel.Warning)
        .Enrich.WithProperty("Pid", Environment.ProcessId)
        .Enrich.WithMachineName()
        .Enrich.WithEnvironmentName();

    ConfigureFileSink(logConfig, compactLog, basePath);

    return logConfig
        .WriteTo.EventLog(
            source: EventLogSource,
            logName: EventLogName,
            restrictedToMinimumLevel: LogEventLevel.Warning,
            manageEventSource: true)
        .CreateLogger();
}

static void ConfigureFileSink(LoggerConfiguration logConfig, bool compactLog, string basePath)
{
    if (compactLog)
        logConfig.WriteTo.File(
            formatter: new CompactJsonFormatter(),
            path: Path.Combine(basePath, CompactLogFileName),
            rollingInterval: RollingInterval.Infinite,
            rollOnFileSizeLimit: true,
            fileSizeLimitBytes: FileSizeLimitBytes,
            retainedFileCountLimit: RetainedFileCount);
    else
        logConfig.WriteTo.File(
            path: Path.Combine(basePath, TextLogFileName),
            rollingInterval: RollingInterval.Infinite,
            rollOnFileSizeLimit: true,
            fileSizeLimitBytes: FileSizeLimitBytes,
            retainedFileCountLimit: RetainedFileCount,
            outputTemplate: LogOutputTemplate);
}

static IHost BuildHost(string[] args)
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddWindowsService(options => options.ServiceName = ServiceName);
    builder.Services.Configure<ServiceSettings>(builder.Configuration.GetSection(ConfigSectionName));
    builder.Services.AddHttpClient(JarDownloaderClientName)
        .AddStandardResilienceHandler();
    builder.Services.AddSingleton<IJarDownloader, HttpJarDownloader>();
    builder.Services.AddSingleton<IConnectivityChecker, TcpConnectivityChecker>();
    builder.Services.AddSingleton<ISecretResolver, SecretResolver>();
    builder.Services.AddHostedService<JenkinsAgentWorker>();

    builder.Logging.ClearProviders();
    builder.Services.AddSerilog();

    return builder.Build();
}

static bool ValidateBoundSettings(IHost host, string basePath)
{
    var settings = host.Services.GetRequiredService<IOptions<ServiceSettings>>().Value;
    if (string.IsNullOrWhiteSpace(settings.JenkinsURL) || string.IsNullOrWhiteSpace(settings.AgentSecret))
    {
        var configPath = Path.Combine(basePath, ConfigFileName);
        Log.Error("Mandatory fields (JenkinsURL, AgentSecret) are empty. Fill in: {Path}", configPath);
        return false;
    }

    return true;
}
