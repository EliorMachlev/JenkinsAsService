using JenkinsAsService;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

var basePath = AppContext.BaseDirectory;

// Read config early to determine log level
var bootConfig = new ConfigurationBuilder()
    .SetBasePath(basePath)
    .AddJsonFile("appsettings.json", optional: true)
    .Build();

var jenkinsSection = bootConfig.GetSection("Jenkins");
var debugMode = jenkinsSection.GetValue<bool>("DebugMode");
var compactLog = jenkinsSection.GetValue<bool>("CompactLog");

var logConfig = new LoggerConfiguration()
    .MinimumLevel.Is(debugMode ? LogEventLevel.Debug : LogEventLevel.Information)
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
    .MinimumLevel.Override("Polly", LogEventLevel.Warning)
    .Enrich.WithProperty("Pid", Environment.ProcessId)
    .Enrich.WithMachineName()
    .Enrich.WithEnvironmentName();

const long fileSizeLimit = 10 * 1024 * 1024;
const int retainedFiles = 3;

if (compactLog)
    logConfig.WriteTo.File(
        formatter: new CompactJsonFormatter(),
        path: Path.Combine(basePath, "agent.clef"),
        rollingInterval: RollingInterval.Infinite,
        rollOnFileSizeLimit: true,
        fileSizeLimitBytes: fileSizeLimit,
        retainedFileCountLimit: retainedFiles);
else
    logConfig.WriteTo.File(
        path: Path.Combine(basePath, "agent.log"),
        rollingInterval: RollingInterval.Infinite,
        rollOnFileSizeLimit: true,
        fileSizeLimitBytes: fileSizeLimit,
        retainedFileCountLimit: retainedFiles,
        outputTemplate: "{Pid} | {Timestamp:yyyy-MM-dd HH:mm:ss} | {Level} | {Message:lj}{NewLine}{Exception}");

Log.Logger = logConfig
    .WriteTo.EventLog(
        source: "JenkinsAsService",
        logName: "Application",
        restrictedToMinimumLevel: LogEventLevel.Warning,
        manageEventSource: true)
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddWindowsService(options => options.ServiceName = "Jenkins");
    builder.Services.Configure<ServiceSettings>(builder.Configuration.GetSection("Jenkins"));
    builder.Services.AddHttpClient("JarDownloader")
        .AddStandardResilienceHandler();
    builder.Services.AddSingleton<IJarDownloader, HttpJarDownloader>();
    builder.Services.AddSingleton<IConnectivityChecker, TcpConnectivityChecker>();
    builder.Services.AddHostedService<JenkinsAgentWorker>();

    builder.Logging.ClearProviders();
    builder.Services.AddSerilog();

    var host = builder.Build();

    // Validate that settings bind correctly before starting
    var settings = host.Services.GetRequiredService<IOptions<ServiceSettings>>().Value;
    if (string.IsNullOrWhiteSpace(settings.JenkinsURL) || string.IsNullOrWhiteSpace(settings.AgentSecret))
    {
        var configPath = Path.Combine(basePath, "appsettings.json");
        Log.Error("Mandatory fields (JenkinsURL, AgentSecret) are empty. Fill in: {Path}", configPath);
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
