// Copyright (c) 2024 All rights reserved

using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace JenkinsAsService.Tests;

/// <summary>
/// Drives <see cref="JenkinsAgentWorker"/>'s supervision loop end-to-end with a fake process launcher and
/// millisecond timing, covering the crash/restart/give-up/unreachable behavior that used to be untestable.
/// </summary>
public sealed class SupervisionLoopTests : IDisposable
{
    private const int WaitTimeoutMs = 5000;

    private readonly string _dataDir;
    private readonly string _javaDir;
    private readonly FakeAgentProcessLauncher _launcher = new();
    private readonly IJarDownloader _downloader = Substitute.For<IJarDownloader>();
    private readonly IConnectivityChecker _connectivity = Substitute.For<IConnectivityChecker>();
    private readonly ISecretResolver _secretResolver = Substitute.For<ISecretResolver>();
    private readonly IHostApplicationLifetime _lifetime = Substitute.For<IHostApplicationLifetime>();

    public SupervisionLoopTests()
    {
        _dataDir = NewTempDir("JAS_Sup_Data_");
        _javaDir = NewTempDir("JAS_Sup_Java_");
        File.WriteAllText(Path.Combine(_javaDir, "java.exe"), "stub"); // JavaPathResolver only checks existence
        _secretResolver.Resolve(Arg.Any<ServiceSettings>()).Returns("resolved-secret");
    }

    public void Dispose()
    {
        TryDeleteDir(_dataDir);
        TryDeleteDir(_javaDir);
    }

    [Fact]
    public async Task Agent_that_crashes_is_restarted()
    {
        var first = new FakeAgentProcess();
        var second = new FakeAgentProcess();
        _launcher.Enqueue(first, second);

        var worker = CreateWorker(maxRetries: 0);
        worker.UseFastTimingForTests(stabilityMs: 10_000, backoffBaseSec: 0, backoffMaxSec: 0);

        await worker.StartAsync(CancellationToken.None);
        await WaitUntil(() => _launcher.StartCount == 1);

        first.SignalExit(exitCode: 1);

        await WaitUntil(() => _launcher.StartCount == 2);
        first.WasKilled.Should().BeTrue("the crashed process must be reaped");
        first.WasDisposed.Should().BeTrue();
        _lifetime.DidNotReceive().StopApplication();

        await StopQuietly(worker);
    }

    [Fact]
    public async Task Service_stops_after_consecutive_crashes_exceed_max_retries()
    {
        // Pre-exited processes: each "start" immediately observes a dead agent → a counted crash.
        _launcher.Enqueue(ExitedProcess(), ExitedProcess(), ExitedProcess());

        var worker = CreateWorker(maxRetries: 2);
        worker.UseFastTimingForTests(stabilityMs: 10_000, backoffBaseSec: 0, backoffMaxSec: 0);

        await worker.StartAsync(CancellationToken.None);

        // crash 1, 2, then crash 3 > MaxRetries(2) → give up and stop the host.
        await WaitUntil(() => _lifetime.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IHostApplicationLifetime.StopApplication)));
        _launcher.StartCount.Should().Be(3, "three crashes are needed to exceed a MaxRetries of 2");

        await StopQuietly(worker);
    }

    [Fact]
    public async Task Unreachable_controller_is_retried_forever_and_never_trips_give_up()
    {
        var attempts = 0;
        _connectivity
            .When(c => c.Check(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()))
            .Do(_ =>
            {
                Interlocked.Increment(ref attempts);
                throw new InvalidOperationException("controller unreachable");
            });

        var worker = CreateWorker(maxRetries: 1);
        worker.UseFastTimingForTests(stabilityMs: 50, backoffBaseSec: 0, backoffMaxSec: 0);

        await worker.StartAsync(CancellationToken.None);

        // Many connectivity attempts prove it keeps retrying...
        await WaitUntil(() => Volatile.Read(ref attempts) >= 3);
        // ...yet it never started an agent and never gave up despite MaxRetries=1.
        _launcher.StartCount.Should().Be(0);
        _lifetime.DidNotReceive().StopApplication();

        await StopQuietly(worker);
    }

    [Fact]
    public async Task Stop_during_bring_up_kills_an_agent_started_after_the_stop_request()
    {
        // Connectivity blocks (ignoring the token) so we can stop the service mid-bring-up.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _connectivity.Check(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => gate.Task);

        var started = new FakeAgentProcess();
        _launcher.Enqueue(started);

        var worker = CreateWorker(maxRetries: 0);
        worker.UseFastTimingForTests(stabilityMs: 10_000, backoffBaseSec: 0, backoffMaxSec: 0);

        await worker.StartAsync(CancellationToken.None);
        await WaitUntil(() => _connectivity.ReceivedCalls().Any());

        // StopAsync sets _stopping and cancels the token, then awaits the (still blocked) loop.
        var stopTask = worker.StopAsync(CancellationToken.None);
        // Connectivity now "succeeds" — bring-up proceeds and starts an agent AFTER the stop request.
        gate.TrySetResult();
        await stopTask;

        // The late-started agent must be reaped on the way out, not orphaned past service shutdown.
        started.WasKilled.Should().BeTrue("an agent started after the stop request must not outlive the service");
        started.WasDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task Auto_mode_launches_the_agent_with_noReconnect_so_the_watchdog_owns_reconnection()
    {
        var worker = CreateWorker(maxRetries: 0, method: ConnectionMethod.Auto);
        worker.UseFastTimingForTests(stabilityMs: 10_000, backoffBaseSec: 0, backoffMaxSec: 0);

        await worker.StartAsync(CancellationToken.None);
        await WaitUntil(() => _launcher.StartCount == 1);

        _launcher.LastStartInfo!.ArgumentList.Should().Contain("-noReconnect",
            "Auto must let the agent exit on a failed handshake so the transport fallback can fire");

        await StopQuietly(worker);
    }

    [Fact]
    public async Task Fixed_transport_keeps_the_agents_own_internal_reconnect()
    {
        var worker = CreateWorker(maxRetries: 0, method: ConnectionMethod.WebSocket);
        worker.UseFastTimingForTests(stabilityMs: 10_000, backoffBaseSec: 0, backoffMaxSec: 0);

        await worker.StartAsync(CancellationToken.None);
        await WaitUntil(() => _launcher.StartCount == 1);

        _launcher.LastStartInfo!.ArgumentList.Should().NotContain("-noReconnect",
            "a pinned transport has nothing to fall back to, so the agent's faster internal reconnect stays");

        await StopQuietly(worker);
    }

    private JenkinsAgentWorker CreateWorker(int maxRetries, ConnectionMethod method = ConnectionMethod.Auto)
    {
        var settings = new ServiceSettings
        {
            Connection = new() { Url = "https://jenkins:8443", Method = method },
            Secret = new() { Value = "cipher", Mode = SecretMode.Unprotected, ViaFile = false },
            Agent = new() { JavaPath = _javaDir, DataDirectory = _dataDir },
            Hardening = new() { SanitizeEnvironment = false },
            Recovery = new() { MaxRetries = maxRetries }
        };

        return new JenkinsAgentWorker(
            Substitute.For<ILogger<JenkinsAgentWorker>>(),
            Options.Create(settings),
            _downloader,
            _connectivity,
            _secretResolver,
            _launcher,
            _lifetime);
    }

    private static FakeAgentProcess ExitedProcess()
    {
        var p = new FakeAgentProcess();
        p.SignalExit(exitCode: 1);
        return p;
    }

    private static async Task StopQuietly(JenkinsAgentWorker worker)
    {
        try
        {
            await worker.StopAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Expected when the loop is torn down mid-wait.
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > WaitTimeoutMs)
            {
                throw new TimeoutException("Condition not met within the timeout.");
            }

            await Task.Delay(10);
        }
    }

    private static string NewTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}
