// Copyright (c) 2024 All rights reserved

using System.Collections.Concurrent;
using System.Diagnostics;

namespace JenkinsAsService.Tests;

/// <summary>
/// A controllable in-memory <see cref="IAgentProcess"/> so the supervision loop can be driven through
/// crash / stabilise / restart cycles without spawning a real java.exe. Call <see cref="SignalExit"/> to
/// make the "process" die.
/// </summary>
internal sealed class FakeAgentProcess : IAgentProcess
{
    private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Id { get; } = Random.Shared.Next(1000, 9999);

    /// <summary>Settable so a test can pretend the fake was launched into an interactive session.</summary>
    public uint? InteractiveSessionId { get; set; }

    public bool HasExited { get; private set; }
    public int ExitCode { get; private set; }
    public Task Exited => _exit.Task;
    public bool WasKilled => KillCount > 0;
    public bool WasDisposed => DisposeCount > 0;

    /// <summary>
    /// Kill/dispose call counts. StopAsync and the supervision loop both call the worker's KillAgent; if that
    /// does not take the agent atomically, both threads can drive the same instance through Kill/Dispose twice.
    /// </summary>
    public int KillCount;
    public int DisposeCount;

    /// <summary>Simulates the process exiting on its own with the given code.</summary>
    public void SignalExit(int exitCode = 1)
    {
        ExitCode = exitCode;
        HasExited = true;
        _exit.TrySetResult();
    }

    public void Kill()
    {
        Interlocked.Increment(ref KillCount);
        HasExited = true;
        _exit.TrySetResult();
    }

    public void Dispose() => Interlocked.Increment(ref DisposeCount);
}

/// <summary>
/// Records each launched process and hands back caller-supplied <see cref="FakeAgentProcess"/> instances in
/// order, so a test can script "first launch stays up, second crashes fast", etc.
/// </summary>
internal sealed class FakeAgentProcessLauncher : IAgentProcessLauncher
{
    private readonly ConcurrentQueue<FakeAgentProcess> _queued = new();
    public ConcurrentQueue<FakeAgentProcess> Started { get; } = new();
    public int StartCount { get; private set; }

    /// <summary>The <see cref="ProcessStartInfo"/> from the most recent <see cref="Start"/> call, so tests can
    /// assert on the launched command line (e.g. that <c>-noReconnect</c> is or isn't present).</summary>
    public ProcessStartInfo? LastStartInfo { get; private set; }

    /// <summary>Enqueue processes to be returned by successive <see cref="Start"/> calls.</summary>
    public void Enqueue(params FakeAgentProcess[] processes)
    {
        foreach (var p in processes)
        {
            _queued.Enqueue(p);
        }
    }

    public IAgentProcess Start(ProcessStartInfo startInfo, Action<string> onOutputLine)
    {
        StartCount++;
        LastStartInfo = startInfo;
        var process = _queued.TryDequeue(out var next) ? next : new FakeAgentProcess();
        Started.Enqueue(process);
        return process;
    }
}
