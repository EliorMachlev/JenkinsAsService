using System.Net.Sockets;

namespace JenkinsAsService;

public sealed class TcpConnectivityChecker : IConnectivityChecker
{
    private readonly ILogger<TcpConnectivityChecker> _logger;

    public TcpConnectivityChecker(ILogger<TcpConnectivityChecker> logger)
    {
        _logger = logger;
    }

    public async Task TestAsync(string host, int port, int timeoutMs, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        try
        {
            await tcp.ConnectAsync(host, port, cts.Token);
            _logger.LogDebug("Jenkins reachable at {Host}:{Port}", host, port);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Cannot reach Jenkins at '{host}' on port {port} (timed out after {timeoutMs}ms)");
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException(
                $"Cannot reach Jenkins at '{host}' on port {port}: {ex.Message}");
        }
    }
}
