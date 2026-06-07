namespace JenkinsAsService;

public sealed class HttpJarDownloader : IJarDownloader
{
    private const string JarFilename = "agent.jar"; // intentional: decoupled from JenkinsAgentWorker
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<HttpJarDownloader> _logger;

    public HttpJarDownloader(IHttpClientFactory httpFactory, ILogger<HttpJarDownloader> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task DownloadAsync(string jenkinsUrl, string destinationPath, CancellationToken ct)
    {
        var jarPath = Path.Combine(destinationPath, JarFilename);
        var jarUri = $"{jenkinsUrl.TrimEnd('/')}/jnlpJars/{JarFilename}";

        _logger.LogInformation("Downloading {Jar} from {Uri}", JarFilename, jarUri);

        using var client = _httpFactory.CreateClient("JarDownloader");
        using var response = await client.GetAsync(jarUri, ct);
        response.EnsureSuccessStatusCode();

        await using var fs = new FileStream(jarPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await response.Content.CopyToAsync(fs, ct);

        _logger.LogInformation("{Jar} downloaded successfully", JarFilename);
    }
}
