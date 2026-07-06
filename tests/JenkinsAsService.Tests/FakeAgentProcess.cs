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
    public bool HasExited { get; private set; }
    public int ExitCode { get; private set; }
    public Task Exited => _exit.Task;
    public bool WasKilled { get; private set; }
    public bool WasDisposed { get; private set; }

    /// <summary>Simulates the process exiting on its own with the given code.</summary>
    public void SignalExit(int exitCode = 1)
    {
        ExitCode = exitCode;
        HasExited = true;
        _exit.TrySetResult();
    }

    public void Kill()
    {
        WasKilled = true;
        HasExited = true;
        _exit.TrySetResult();
    }

    public void Dispose() => WasDisposed = true;
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
        var process = _queued.TryDequeue(out var next) ? next : new FakeAgentProcess();
        Started.Enqueue(process);
        return process;
    }
}
