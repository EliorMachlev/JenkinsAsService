namespace JenkinsAsService;

public interface IJarDownloader
{
    Task DownloadAsync(string jenkinsUrl, string destinationPath, CancellationToken ct);
}
