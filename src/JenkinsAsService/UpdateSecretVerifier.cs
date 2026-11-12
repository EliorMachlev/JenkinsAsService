// Copyright (c) 2024 All rights reserved

using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace JenkinsAsService;

/// <summary>Which written value a verification step is about, so a caller can re-ask for exactly that one.</summary>
internal enum VerifiedValue
{
    Url,
    JavaPath
}

/// <summary>A failed check: the value it was about and the complete message for the operator.</summary>
internal sealed record VerificationFailure(VerifiedValue Value, string Message);

/// <summary>
/// Checks the values <c>update-secret</c> is about to write, before it writes them, so a mistyped URL or Java
/// path fails the command (and, as a deferred custom action, the install) instead of the service at its next
/// start. Three checks, all on by default and all skipped together by <c>--skip-verification-steps</c>:
/// <list type="number">
/// <item>the URL obeys <see cref="ControllerUrl"/> - the very rule <see cref="ServiceSettingsValidator"/>
/// applies at startup;</item>
/// <item>the controller answers a TCP connect at the URL's host and port, through the same
/// <see cref="IConnectivityChecker"/> the service's bring-up uses;</item>
/// <item>an explicitly given Java path contains <c>java.exe</c>, through <see cref="JavaPathResolver"/> with
/// no <c>JAVA_HOME</c> fallback - a path that only "works" because JAVA_HOME happens to be set is not the path
/// the operator meant. An unset Java path is not checked: it is resolved from JAVA_HOME at runtime.</item>
/// </list>
/// <para>
/// Messages name the option, the reason and the value. A URL or a path is not a secret; the URL's userinfo
/// (<c>user:pass@</c>) is, and is stripped with <see cref="ControllerUrl.WithoutUserInfo"/>. Nothing here ever
/// sees the agent secret.
/// </para>
/// </summary>
internal sealed class UpdateSecretVerifier
{
    /// <summary>How long the reachability check waits for the TCP connect.</summary>
    internal const int DefaultReachabilityTimeoutMs = 5_000;

    /// <summary>The option that turns every check off, in the form the messages tell the operator to use.</summary>
    internal const string SkipOptionHint = "--svs (--skip-verification-steps)";

    private const string JavaExe = "java.exe";
    private const string JavaBinFolder = "bin";

    private readonly IConnectivityChecker _checker;
    private readonly int _timeoutMs;
    private readonly Action<string> _recordFailure;

    /// <param name="checker">The TCP check. Tests pass a loopback-only one; production the service's own.</param>
    /// <param name="timeoutMs">The reachability timeout.</param>
    /// <param name="recordFailure">
    /// Where a failure is recorded besides stderr. An EXE custom action's console output never reaches the MSI
    /// log, so for a silent install this is the only place the reason survives; production writes it to the
    /// Application event log. Null records nothing.
    /// </param>
    internal UpdateSecretVerifier(IConnectivityChecker checker, int timeoutMs, Action<string>? recordFailure = null)
    {
        _checker = checker;
        _timeoutMs = timeoutMs;
        _recordFailure = recordFailure ?? (_ => { });
    }

    /// <summary>The production verifier: the service's TCP checker, a 5 s timeout, failures to the event log.</summary>
    internal static UpdateSecretVerifier CreateDefault() =>
        new(new TcpConnectivityChecker(NullLogger<TcpConnectivityChecker>.Instance),
            DefaultReachabilityTimeoutMs, WriteToEventLog);

    /// <summary>Records a failure through the sink given at construction (in addition to the caller's stderr).</summary>
    internal void Record(VerificationFailure failure) => _recordFailure(failure.Message);

    /// <summary>
    /// Runs every applicable check and returns the failures, in option order: the URL when
    /// <paramref name="url"/> is non-blank, the Java path when <paramref name="javaPath"/> is. Blank means
    /// "not being written", which is not this class's concern - the required-field checks own that.
    /// </summary>
    internal IReadOnlyList<VerificationFailure> Verify(string? url, string? javaPath)
    {
        var failures = new List<VerificationFailure>(2);
        if (!string.IsNullOrWhiteSpace(url) && VerifyUrl(url) is { } urlFailure)
        {
            failures.Add(urlFailure);
        }

        if (!string.IsNullOrWhiteSpace(javaPath) && VerifyJavaPath(javaPath) is { } javaFailure)
        {
            failures.Add(javaFailure);
        }

        return failures;
    }

    /// <summary>Checks 1 and 2: the URL obeys the startup rule, then answers a TCP connect. Null when both pass.</summary>
    internal VerificationFailure? VerifyUrl(string url)
    {
        var shown = ControllerUrl.WithoutUserInfo(url);
        var problem = ControllerUrl.Check(url);
        if (problem != ControllerUrl.Problem.None)
        {
            return new VerificationFailure(VerifiedValue.Url,
                $"--url: '{shown}' {ControllerUrl.Describe(problem)}. Example: {ControllerUrl.Example}");
        }

        var uri = new Uri(url);
        if (string.IsNullOrEmpty(uri.Host))
        {
            return new VerificationFailure(VerifiedValue.Url,
                $"--url: '{shown}' names no host to connect to. Example: {ControllerUrl.Example}");
        }

        try
        {
            // Synchronous on purpose: this is a one-shot CLI with no synchronization context to deadlock.
            _checker.Check(uri.Host, uri.Port, _timeoutMs, CancellationToken.None).GetAwaiter().GetResult();
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Net.Sockets.SocketException
                                       or ArgumentException or OperationCanceledException)
        {
            // TcpConnectivityChecker's own message names the host and port (never the userinfo) and the reason.
            return new VerificationFailure(VerifiedValue.Url,
                $"--url: the controller at '{shown}' is not reachable ({ex.Message}). This is a TCP connect with a "
                + $"{_timeoutMs} ms timeout; at install time it runs as SYSTEM and ignores "
                + "Connection:Proxy, and the controller may simply be down for maintenance. If the URL is right, "
                + $"use {SkipOptionHint} to write it without this check.");
        }
    }

    /// <summary>Check 3: <c>java.exe</c> is in the given folder. Null when it is.</summary>
    internal static VerificationFailure? VerifyJavaPath(string javaPath)
    {
        try
        {
            // javaHome: null - the explicitly given path is what is being written, so it alone must hold java.exe.
            JavaPathResolver.Resolve(javaPath, javaHome: null);
            return null;
        }
        catch (InvalidOperationException)
        {
            // Agent:JavaPath is the folder that CONTAINS java.exe. A JDK root (what JAVA_HOME holds) is the
            // common mistake, so say so when its bin folder would have been right.
            var bin = Path.Join(javaPath, JavaBinFolder);
            var hint = File.Exists(Path.Join(bin, JavaExe))
                ? $" It is in '{bin}': give that folder."
                : " Give the folder that contains java.exe (a JDK's bin folder).";
            return new VerificationFailure(VerifiedValue.JavaPath,
                $"--java-path: java.exe was not found in '{javaPath}'.{hint}");
        }
    }

    // Best effort: the source is created by EventLogSourceInstaller.Ensure, which the silent path calls first
    // while it still runs elevated. Not being able to log must never change the command's outcome.
    private static void WriteToEventLog(string message)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            if (EventLog.SourceExists(EventLogSourceInstaller.DefaultSource))
            {
                EventLog.WriteEntry(EventLogSourceInstaller.DefaultSource,
                    $"update-secret verification failed; nothing was written. {message}", EventLogEntryType.Error);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception or UnauthorizedAccessException
                                       or ArgumentException)
        {
            // Not elevated, or the log is unavailable: stderr already has it.
        }
    }
}
