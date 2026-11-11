// Copyright (c) 2024 All rights reserved

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace JenkinsAsService;

/// <summary>
/// Applies a deliberately narrow, build-agent-safe subset of Win32 process mitigation policies to the
/// service process to reduce DLL-planting and injection surface. Policies that would break a process
/// whose job is to launch an arbitrary JVM and build toolchain — notably binary-signature / Code
/// Integrity Guard (which blocks unsigned native DLLs) and dynamic-code prohibition (which breaks the
/// .NET JIT) — are intentionally <strong>not</strong> applied. All failures are non-fatal.
/// <para>
/// <strong>These policies are inherited by child processes.</strong> They reach <c>java.exe</c> and
/// every compiler, test runner and script the agent spawns beneath it — this type does not stop at the
/// service boundary, and an earlier version of this comment claiming it did was wrong. Windows provides
/// no way to undo the inheritance: a child can be given <em>additional</em> mitigations at creation time
/// through <c>PROC_THREAD_ATTRIBUTE_MITIGATION_POLICY</c>, but the <c>ALWAYS_OFF</c> bits override only
/// the system default, not a policy inherited from the parent. Measured on Windows 11 26200 (x64): with
/// the parent at <c>image-load = 7</c>, a child created with every <c>ALWAYS_OFF</c> bit still reads
/// back 7, while the same attribute list with <c>ALWAYS_ON</c> against a clean parent correctly yields 7
/// — so the attribute is honoured, and inheritance simply wins.
/// </para>
/// <para>
/// So <c>Jenkins:Hardening:ProcessMitigations</c> is a dial on what the <em>service</em> takes, not on
/// what it passes down — see <see cref="MitigationLevel"/>. Of the three image-load flags only
/// <see cref="NoRemoteImages"/> has build-visible consequences, which is why declining it has a level of
/// its own instead of forcing the all-or-nothing choice.
/// </para>
/// </summary>
internal static class ProcessMitigations
{
    private enum PolicyType
    {
        // Values from PROCESS_MITIGATION_POLICY (winnt.h).
        ProcessExtensionPointDisablePolicy = 7,
        ProcessImageLoadPolicy = 10,
    }

    // PROCESS_MITIGATION_IMAGE_LOAD_POLICY flag bits.
    // The one flag with build-visible consequences, and so the only one MitigationLevel.AllowNetworkImages
    // drops. Inherited by the build tree like the rest, which is exactly why it gets its own level.
    private const uint NoRemoteImages = 1u << 0;          // Block loading DLLs from UNC / network paths.
    private const uint NoLowMandatoryLabelImages = 1u << 1; // Block DLLs written by low-integrity processes.
    private const uint PreferSystem32Images = 1u << 2;     // Resolve known system DLLs from %WinDir%\System32 first.

    // PROCESS_MITIGATION_EXTENSION_POINT_DISABLE_POLICY flag bits.
    // Current Windows enables this before the process runs, so the call below commonly fails with
    // ERROR_ACCESS_DENIED against a policy that is already in force (measured on 11 26200). Kept because
    // the guarantee is not documented for every supported OS, and a failure here is only a warning.
    private const uint DisableExtensionPoints = 1u << 0;   // Block legacy injection (AppInit_DLLs, Winsock LSPs, IMEs).

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessMitigationPolicy(PolicyType policy, ref uint buffer, nuint length);

    /// <summary>
    /// The image-load flag set for a given level. Pure, so the one part of this type carrying a decision is
    /// testable without touching the real policy — which cannot be undone once set, and is inherited by
    /// everything the test process later spawns.
    /// </summary>
    internal static uint ImageLoadFlags(MitigationLevel level) => level switch
    {
        MitigationLevel.Off => 0,
        MitigationLevel.AllowNetworkImages => NoLowMandatoryLabelImages | PreferSystem32Images,
        // Full, and deliberately the DEFAULT arm rather than a named one: an unrecognised level must fail
        // towards the most protective set, not the least. A config carrying a level this build does not
        // know about is the case that matters, and it binds to 0 (Full) anyway.
        _ => NoRemoteImages | NoLowMandatoryLabelImages | PreferSystem32Images
    };

    /// <summary>
    /// Applies the safe mitigation subset at <paramref name="level"/>. No-op on non-Windows, and no-op
    /// entirely at <see cref="MitigationLevel.Off"/>. Each policy is best-effort: a failure (older OS,
    /// already-set, or insufficient rights) is reported via <paramref name="warn"/> and skipped.
    /// </summary>
    /// <returns><c>true</c> if any policy was attempted; <c>false</c> at <see cref="MitigationLevel.Off"/>
    /// or off-Windows, so the caller can log that the service is running unhardened.</returns>
    internal static bool Apply(MitigationLevel level = MitigationLevel.Full, Action<string>? warn = null)
    {
        if (!OperatingSystem.IsWindows() || level == MitigationLevel.Off)
        {
            return false;
        }

        ApplyWindows(level, warn);
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyWindows(MitigationLevel level, Action<string>? warn)
    {
        TryApply(
            PolicyType.ProcessImageLoadPolicy,
            ImageLoadFlags(level),
            "image-load",
            warn);

        TryApply(
            PolicyType.ProcessExtensionPointDisablePolicy,
            DisableExtensionPoints,
            "extension-point-disable",
            warn);
    }

    [SupportedOSPlatform("windows")]
    private static void TryApply(PolicyType policy, uint flags, string name, Action<string>? warn)
    {
        try
        {
            var buffer = flags;
            if (!SetProcessMitigationPolicy(policy, ref buffer, (nuint)sizeof(uint)))
            {
                warn?.Invoke($"Process mitigation '{name}' not applied (Win32 error {Marshal.GetLastWin32Error()}).");
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            warn?.Invoke($"Process mitigation '{name}' unavailable on this OS: {ex.Message}");
        }
    }
}
