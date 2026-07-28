// Copyright (c) 2024 All rights reserved

using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace JenkinsAsService;

/// <summary>
/// Starts the Java agent process. Abstracted behind an interface so the watchdog's supervision loop can be
/// driven by a fake process in tests (crash/stabilise/restart cycles) without spawning a real <c>java.exe</c>.
/// </summary>
public interface IAgentProcessLauncher
{
    /// <summary>Starts the process described by <paramref name="startInfo"/>, forwarding every stdout/stderr
    /// line to <paramref name="onOutputLine"/>. The returned handle signals exit via <see cref="IAgentProcess.Exited"/>.</summary>
    IAgentProcess Start(ProcessStartInfo startInfo, Action<string> onOutputLine);
}

/// <summary>A running agent process the watchdog can observe and kill.</summary>
public interface IAgentProcess : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    int ExitCode { get; }

    /// <summary>Completes when the process exits (from natural death or <see cref="Kill"/>).</summary>
    Task Exited { get; }

    /// <summary>Kills the whole process tree. Never throws — an already-exited or unkillable process is logged upstream.</summary>
    void Kill();
}

/// <summary>Production launcher backed by <see cref="System.Diagnostics.Process"/>.</summary>
internal sealed class AgentProcessLauncher : IAgentProcessLauncher
{
    private readonly ILogger<AgentProcessLauncher> _logger;
    private readonly ServiceSettings _settings;

    public AgentProcessLauncher(ILogger<AgentProcessLauncher> logger, IOptions<ServiceSettings> settings)
    {
        _logger = logger;
        _settings = settings.Value;
    }

    public IAgentProcess Start(ProcessStartInfo startInfo, Action<string> onOutputLine)
    {
        // Opt-in interactive-desktop launch. Only reachable when explicitly enabled; on any precondition
        // failure (not LocalSystem, no console session, privilege missing) InteractiveSessionLauncher logs a
        // prominent warning and returns false, and we fall through to the normal Session 0 launch below so
        // the agent stays up (headless) instead of failing.
        var interactive = _settings.Agent.LaunchInInteractiveSession;
        if (interactive.Enabled && OperatingSystem.IsWindows()
            && InteractiveSessionLauncher.TryStart(
                startInfo, onOutputLine, interactive.LocalSystemOnly, _logger, out var interactiveProcess))
        {
            return interactiveProcess;
        }

        return StartInSession0(startInfo, onOutputLine);
    }

    private static IAgentProcess StartInSession0(ProcessStartInfo startInfo, Action<string> onOutputLine)
    {
        var handle = new SystemAgentProcess(startInfo, onOutputLine);
        try
        {
            handle.Start();
        }
        catch
        {
            // Start failed (bad path, access denied) — don't leak the undisposed Process handle.
            handle.Dispose();
            throw;
        }

        return handle;
    }

    private sealed class SystemAgentProcess : IAgentProcess
    {
        // Captured in a local (not read from a field) so a late-delivered Exited from THIS process can never
        // complete a different generation's signal — the source of a spurious-restart race.
        private readonly TaskCompletionSource _exitTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Process _process;

        public SystemAgentProcess(ProcessStartInfo startInfo, Action<string> onOutputLine)
        {
            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            void OnDataReceived(object _, DataReceivedEventArgs e)
            {
                if (e.Data is not null)
                {
                    onOutputLine(e.Data);
                }
            }

            _process.OutputDataReceived += OnDataReceived;
            _process.ErrorDataReceived += OnDataReceived;
            // Subscribe before Start so a fast-exiting process doesn't miss the event.
            _process.Exited += (_, _) => _exitTcs.TrySetResult();
        }

        public void Start()
        {
            _process.Start();
            // Defensive: if the process already exited between Start() and the subscription above
            // (extremely rare), fire the signal now so the watchdog doesn't wait.
            if (_process.HasExited)
            {
                _exitTcs.TrySetResult();
            }

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        public int Id => _process.Id;
        public bool HasExited => _process.HasExited;
        public int ExitCode => _process.ExitCode;
        public Task Exited => _exitTcs.Task;

        public void Kill()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Process already exited — nothing to kill.
            }
            catch (Win32Exception)
            {
                // Kill can fail (access denied, or the OS races the process's own exit). Abandoning a
                // possibly-orphaned process is better than faulting the watchdog; the handle is discarded.
            }
        }

        public void Dispose() => _process.Dispose();
    }
}
