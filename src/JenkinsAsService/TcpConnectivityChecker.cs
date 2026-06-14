using System.Diagnostics;
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
        _logger.LogDebug("Testing TCP connectivity to {Host}:{Port} (timeout {Timeout}ms)", host, port, timeoutMs);

        using var tcp = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await tcp.ConnectAsync(host, port, cts.Token);
            stopwatch.Stop();
            _logger.LogInformation("Jenkins reachable at {Host}:{Port} ({LatencyMs}ms)",
                host, port, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Connectivity test to {Host}:{Port} timed out after {Timeout}ms", host, port, timeoutMs);
            throw new InvalidOperationException(
                $"Cannot reach Jenkins at '{host}' on port {port} (timed out after {timeoutMs}ms)");
        }
        catch (SocketException ex)
        {
            _logger.LogWarning("Connectivity test to {Host}:{Port} failed: {Reason}", host, port, ex.Message);
            throw new InvalidOperationException(
                $"Cannot reach Jenkins at '{host}' on port {port}: {ex.Message}");
        }
    }
}
