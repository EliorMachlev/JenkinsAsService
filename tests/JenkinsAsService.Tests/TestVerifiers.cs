// Copyright (c) 2024 All rights reserved
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace JenkinsAsService.Tests;

/// <summary>
/// <see cref="UpdateSecretVerifier"/>s for tests. Nothing here resolves a name or leaves the machine: the real
/// checker is only ever pointed at 127.0.0.1, and the stand-in never touches a socket.
/// </summary>
internal static class TestVerifiers
{
    /// <summary>A checker that always connects (NSubstitute returns a completed Task), so only the URL rule applies.</summary>
    internal static UpdateSecretVerifier AlwaysReachable(List<string>? recorded = null) =>
        new(Substitute.For<IConnectivityChecker>(), 100, Recorder(recorded));

    /// <summary>The production TCP checker with a short timeout, for loopback-only URLs.</summary>
    internal static UpdateSecretVerifier RealTcp(List<string>? recorded = null, int timeoutMs = 500) =>
        new(new TcpConnectivityChecker(NullLogger<TcpConnectivityChecker>.Instance), timeoutMs, Recorder(recorded));

    /// <summary>
    /// A loopback port with nothing listening: bound, read and released. Windows answers a connect there with a
    /// reset (retried for about two seconds) and the short timeout ends it sooner; either way it is unreachable.
    /// </summary>
    internal static int ClosedLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static Action<string>? Recorder(List<string>? recorded) =>
        recorded is null ? null : recorded.Add;
}
