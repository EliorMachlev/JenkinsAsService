namespace JenkinsAsService;

public sealed class HttpJarDownloader : IJarDownloader
{
    private const string JarFilename = "agent.jar"; // intentional: decoupled from JenkinsAgentWorker
    private const string JnlpJarsPath = "jnlpJars";
    private const string UrlSeparator = "/";
    private const char TrailingSlash = '/';
    private const string HttpClientName = "JarDownloader";

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
        var jarUri = BuildJarUri(jenkinsUrl);

        _logger.LogInformation("Downloading {Jar} from {Uri}", JarFilename, jarUri);

        var bytesWritten = await DownloadToFileAsync(jarUri, jarPath, ct);

        _logger.LogInformation("{Jar} downloaded successfully ({Bytes} bytes)", JarFilename, bytesWritten);
    }

    private static string BuildJarUri(string jenkinsUrl) =>
        $"{jenkinsUrl.TrimEnd(TrailingSlash)}{UrlSeparator}{JnlpJarsPath}{UrlSeparator}{JarFilename}";

    private async Task<long> DownloadToFileAsync(string jarUri, string jarPath, CancellationToken ct)
    {
        using var client = _httpFactory.CreateClient(HttpClientName);
        using var response = await client.GetAsync(jarUri, ct);
        response.EnsureSuccessStatusCode();

        await using var fs = new FileStream(jarPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await response.Content.CopyToAsync(fs, ct);
        return fs.Length;
    }
}
