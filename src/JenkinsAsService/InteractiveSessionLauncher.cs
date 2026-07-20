// Copyright (c) 2024 All rights reserved

using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace JenkinsAsService;

/// <summary>
/// Opt-in launcher that starts the Java agent in the active <em>interactive</em> console session (Session 1)
/// instead of the service's non-interactive Session 0, so GUI processes the agent spawns downstream
/// (e.g. a Selenium/NUnit browser) are visible on the logged-in desktop.
/// <para>
/// <strong>This is a deliberate isolation downgrade.</strong> Crossing the session boundary needs
/// <c>WTSQueryUserToken</c> → <c>SeTcbPrivilege</c>, i.e. the service must run as <c>LocalSystem</c> (or an
/// account granted a LocalSystem-equivalent privilege). Every precondition failure — wrong identity, no
/// logged-in console session, missing privilege — is reported via a warning and returns <c>false</c> so the
/// caller falls back to a normal Session 0 (headless) launch; the service never fails because of this.
/// </para>
/// The interop here cannot be exercised by the unit suite (no interactive session / privilege in CI); it is
/// compile-verified only, in the same class as the TPM code path.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class InteractiveSessionLauncher
{
    private const uint InvalidSessionId = 0xFFFFFFFF;

    // ── Token / process creation constants ────────────────────────────────────
    private const uint MaximumAllowed = 0x02000000;
    private const int SecurityImpersonation = 2; // SECURITY_IMPERSONATION_LEVEL
    private const int TokenPrimary = 1;          // TOKEN_TYPE

    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateNewConsole = 0x00000010;
    private const uint CreateSuspended = 0x00000004;
    private const uint StartfUseStdHandles = 0x00000100;

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    /// <summary>
    /// Attempts an interactive-session launch. Returns <c>true</c> with a live <paramref name="process"/> on
    /// success; <c>false</c> (after logging the specific reason) when the caller should fall back to Session 0.
    /// </summary>
    internal static bool TryStart(
        ProcessStartInfo startInfo,
        Action<string> onOutputLine,
        bool localSystemOnly,
        ILogger logger,
        [NotNullWhen(true)] out IAgentProcess? process)
    {
        process = null;

        if (localSystemOnly && !IsLocalSystem())
        {
            logger.LogWarning(
                "Agent:LaunchInInteractiveSession is enabled but the service is not running as LocalSystem " +
                "(LocalSystemOnly=true) — falling back to a Session 0 (headless) launch. Run the service as " +
                "LocalSystem, or set LocalSystemOnly=false to attempt it under an account granted SeTcbPrivilege.");
            return false;
        }

        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == InvalidSessionId)
        {
            logger.LogWarning(
                "Agent:LaunchInInteractiveSession is enabled but there is no active console session " +
                "(no user is logged in) — falling back to a Session 0 (headless) launch.");
            return false;
        }

        if (!WTSQueryUserToken(sessionId, out var userToken))
        {
            var err = Marshal.GetLastWin32Error();
            logger.LogWarning(
                "Agent:LaunchInInteractiveSession is enabled but acquiring the console session token failed " +
                "(Win32 error {Error} — typically SeTcbPrivilege not held, i.e. the account is not LocalSystem). " +
                "Falling back to a Session 0 (headless) launch.", err);
            return false;
        }

        try
        {
            process = Launch(userToken, startInfo, onOutputLine, sessionId);
            logger.LogWarning(
                "Agent launched on the interactive desktop (session {Session}). Process isolation is REDUCED " +
                "for this run — this mode is unsupported and intended only for debugging visible tests.",
                sessionId);
            return true;
        }
        catch (Win32Exception ex)
        {
            logger.LogWarning(
                "Agent:LaunchInInteractiveSession failed to start the process in session {Session} " +
                "(Win32 error {Error}: {Message}) — falling back to a Session 0 (headless) launch.",
                sessionId, ex.NativeErrorCode, ex.Message);
            return false;
        }
        finally
        {
            CloseHandle(userToken);
        }
    }

    private static bool IsLocalSystem()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.IsSystem; // true only for the LocalSystem account (S-1-5-18)
    }

    private static IAgentProcess Launch(
        IntPtr userToken, ProcessStartInfo startInfo, Action<string> onOutputLine, uint sessionId)
    {
        if (!DuplicateTokenEx(userToken, MaximumAllowed, IntPtr.Zero, SecurityImpersonation, TokenPrimary,
                out var primaryToken))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx failed");
        }

        // Child writes the client ends; we read the (non-inheritable) server ends. StdIn is a NUL device the
        // child inherits — the agent never reads stdin, but STARTF_USESTDHANDLES requires all three set.
        var stdout = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var stdin = OpenInheritableNul();
        var jobHandle = CreateKillOnCloseJob();
        var envBlock = BuildEnvironmentBlock(startInfo.Environment);
        var envPin = GCHandle.Alloc(envBlock, GCHandleType.Pinned);

        var assignedToJob = false;
        var transferred = false;
        try
        {
            var startup = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = @"winsta0\default",
                dwFlags = StartfUseStdHandles,
                hStdInput = stdin.DangerousGetHandle(),
                hStdOutput = stdout.ClientSafePipeHandle.DangerousGetHandle(),
                hStdError = stderr.ClientSafePipeHandle.DangerousGetHandle(),
            };

            // Start suspended so the process can be placed in the kill-on-close job before it forks anything;
            // the job then guarantees the whole build/browser tree dies on Kill()/Dispose().
            var flags = CreateUnicodeEnvironment | CreateSuspended
                        | (startInfo.CreateNoWindow ? CreateNoWindow : CreateNewConsole);

            var commandLine = new StringBuilder(BuildCommandLine(startInfo));
            var workingDir = string.IsNullOrEmpty(startInfo.WorkingDirectory) ? null : startInfo.WorkingDirectory;

            if (!CreateProcessAsUser(primaryToken, null, commandLine, IntPtr.Zero, IntPtr.Zero,
                    bInheritHandles: true, flags, envPin.AddrOfPinnedObject(), workingDir, ref startup,
                    out var pi))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser failed");
            }

            // Our copies of the client (write) pipe ends and stdin must be closed so EOF propagates to the
            // readers once the child exits; the child kept its own inherited copies.
            stdout.DisposeLocalCopyOfClientHandle();
            stderr.DisposeLocalCopyOfClientHandle();

            assignedToJob = jobHandle != IntPtr.Zero && AssignProcessToJobObject(jobHandle, pi.hProcess);
            ResumeThread(pi.hThread);
            CloseHandle(pi.hThread);

            var effectiveJob = assignedToJob ? jobHandle : IntPtr.Zero;
            var process = new InteractiveAgentProcess(pi.hProcess, pi.dwProcessId, effectiveJob, stdout, stderr, onOutputLine);
            transferred = true;
            return process;
        }
        finally
        {
            envPin.Free();
            CloseHandle(primaryToken);
            stdin.Dispose();
            if (!transferred)
            {
                stdout.Dispose();
                stderr.Dispose();
                if (jobHandle != IntPtr.Zero)
                {
                    CloseHandle(jobHandle);
                }
            }
            else if (!assignedToJob && jobHandle != IntPtr.Zero)
            {
                // Job wasn't used (assignment failed) — the wrapper falls back to TerminateProcess.
                CloseHandle(jobHandle);
            }
        }
    }

    private static IntPtr CreateKillOnCloseJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            return IntPtr.Zero; // non-fatal: Kill() will fall back to TerminateProcess
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = { LimitFlags = JobObjectLimitKillOnJobClose },
        };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info,
                (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            CloseHandle(job);
            return IntPtr.Zero;
        }

        return job;
    }

    // Reproduce the exact environment the Session 0 path would have used (already sanitized by the worker when
    // Hardening:SanitizeEnvironment is on), as a double-null-terminated Unicode block for CREATE_UNICODE_ENVIRONMENT.
    private static char[] BuildEnvironmentBlock(IDictionary<string, string?> environment)
    {
        var sb = new StringBuilder();
        foreach (var kv in environment.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        }

        sb.Append('\0'); // final terminator (valid empty block is a single null)
        return sb.ToString().ToCharArray();
    }

    private static string BuildCommandLine(ProcessStartInfo startInfo)
    {
        var sb = new StringBuilder();
        AppendArgument(sb, startInfo.FileName);
        foreach (var arg in startInfo.ArgumentList)
        {
            sb.Append(' ');
            AppendArgument(sb, arg);
        }

        return sb.ToString();
    }

    // Windows CommandLineToArgvW quoting rules — mirrors the escaping System.Diagnostics.Process uses so the
    // reconstructed command line round-trips through ArgumentList identically.
    private static void AppendArgument(StringBuilder sb, string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0)
        {
            sb.Append(arg);
            return;
        }

        sb.Append('"');
        for (var i = 0; i < arg.Length; i++)
        {
            var backslashes = 0;
            while (i < arg.Length && arg[i] == '\\')
            {
                i++;
                backslashes++;
            }

            if (i == arg.Length)
            {
                sb.Append('\\', backslashes * 2);
                break;
            }

            if (arg[i] == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(arg[i]);
            }
        }

        sb.Append('"');
    }

    private static SafeFileHandle OpenInheritableNul()
    {
        var sa = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            bInheritHandle = true,
        };
        // GENERIC_READ | GENERIC_WRITE, share read/write, OPEN_EXISTING.
        var handle = CreateFile("NUL", 0x80000000 | 0x40000000, 0x1 | 0x2, ref sa, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Opening NUL for stdin failed");
        }

        return handle;
    }

    /// <summary>A process launched via <see cref="CreateProcessAsUser"/> in the interactive session.</summary>
    private sealed class InteractiveAgentProcess : IAgentProcess
    {
        private readonly TaskCompletionSource _exitTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly IntPtr _processHandle;
        private readonly IntPtr _jobHandle;
        private readonly AnonymousPipeServerStream _stdout;
        private readonly AnonymousPipeServerStream _stderr;
        private readonly ManualResetEvent _exitEvent;
        private readonly RegisteredWaitHandle _exitWait;
        private int _exitCode = -1;
        private volatile bool _hasExited;

        public InteractiveAgentProcess(
            IntPtr processHandle, int pid, IntPtr jobHandle,
            AnonymousPipeServerStream stdout, AnonymousPipeServerStream stderr, Action<string> onOutputLine)
        {
            _processHandle = processHandle;
            Id = pid;
            _jobHandle = jobHandle;
            _stdout = stdout;
            _stderr = stderr;

            // Wait on the process handle without owning it (Dispose closes _processHandle explicitly).
            _exitEvent = new ManualResetEvent(false)
            {
                SafeWaitHandle = new SafeWaitHandle(processHandle, ownsHandle: false),
            };
            _exitWait = ThreadPool.RegisterWaitForSingleObject(
                _exitEvent, OnProcessSignaled, state: null, Timeout.Infinite, executeOnlyOnce: true);

            PumpAsync(_stdout, onOutputLine);
            PumpAsync(_stderr, onOutputLine);
        }

        public int Id { get; }
        public bool HasExited => _hasExited;
        public int ExitCode => _exitCode;
        public Task Exited => _exitTcs.Task;

        private void OnProcessSignaled(object? state, bool timedOut)
        {
            if (GetExitCodeProcess(_processHandle, out var code))
            {
                _exitCode = unchecked((int)code);
            }

            _hasExited = true;
            _exitTcs.TrySetResult();
        }

        // Forward each line to the same sink the Session 0 path uses (SEVERE counting + secret redaction).
        private static void PumpAsync(Stream stream, Action<string> onOutputLine) => _ = Task.Run(async () =>
        {
            try
            {
                using var reader = new StreamReader(stream, Encoding.Default, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                string? line;
                while ((line = await reader.ReadLineAsync()) is not null)
                {
                    onOutputLine(line);
                }
            }
            catch
            {
                // Pipe closed on shutdown — nothing more to read.
            }
        });

        public void Kill()
        {
            try
            {
                if (_hasExited)
                {
                    return;
                }

                // Closing/terminating the kill-on-close job takes the whole child tree with it; if the job
                // wasn't available, fall back to terminating just the root process.
                if (_jobHandle != IntPtr.Zero)
                {
                    TerminateJobObject(_jobHandle, 1);
                }
                else
                {
                    TerminateProcess(_processHandle, 1);
                }
            }
            catch (Win32Exception)
            {
                // Best-effort kill; abandoning a possibly-orphaned process beats faulting the watchdog.
            }
        }

        public void Dispose()
        {
            _exitWait.Unregister(null);
            _exitEvent.Dispose();
            try { _stdout.Dispose(); } catch { /* already closed */ }
            try { _stderr.Dispose(); } catch { /* already closed */ }
            if (_jobHandle != IntPtr.Zero)
            {
                CloseHandle(_jobHandle); // kill-on-close reaps any survivors
            }

            if (_processHandle != IntPtr.Zero)
            {
                CloseHandle(_processHandle);
            }
        }
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────────

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(
        IntPtr hExistingToken, uint dwDesiredAccess, IntPtr lpTokenAttributes,
        int impersonationLevel, int tokenType, out IntPtr phNewToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken, string? lpApplicationName, StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, ref SECURITY_ATTRIBUTES lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, int jobObjectInfoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public uint dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
