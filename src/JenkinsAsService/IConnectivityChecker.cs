namespace JenkinsAsService;

public interface IConnectivityChecker
{
    Task TestAsync(string host, int port, int timeoutMs, CancellationToken ct);
}
