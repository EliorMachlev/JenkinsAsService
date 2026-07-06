// Copyright (c) 2024 All rights reserved

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace JenkinsAsService;

/// <summary>
/// Applies a deliberately narrow, build-agent-safe subset of Win32 process mitigation policies to the
/// <em>service</em> process to reduce DLL-planting and injection surface. Policies that would break a
/// process whose job is to launch an arbitrary JVM and build toolchain — notably binary-signature /
/// Code Integrity Guard (which blocks unsigned native DLLs) and dynamic-code prohibition (which breaks
/// the .NET JIT) — are intentionally <strong>not</strong> applied. Mitigations affect only this process,
/// never the spawned <c>java.exe</c> child, so builds are unaffected. All failures are non-fatal.
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
    private const uint NoRemoteImages = 1u << 0;          // Block loading DLLs from UNC / network paths.
    private const uint NoLowMandatoryLabelImages = 1u << 1; // Block DLLs written by low-integrity processes.
    private const uint PreferSystem32Images = 1u << 2;     // Resolve known system DLLs from %WinDir%\System32 first.

    // PROCESS_MITIGATION_EXTENSION_POINT_DISABLE_POLICY flag bits.
    private const uint DisableExtensionPoints = 1u << 0;   // Block legacy injection (AppInit_DLLs, Winsock LSPs, IMEs).

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessMitigationPolicy(PolicyType policy, ref uint buffer, nuint length);

    /// <summary>
    /// Applies the safe mitigation subset. No-op on non-Windows. Each policy is best-effort: a failure
    /// (older OS, already-set, or insufficient rights) is reported via <paramref name="warn"/> and skipped.
    /// </summary>
    internal static void Apply(Action<string>? warn = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ApplyWindows(warn);
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyWindows(Action<string>? warn)
    {
        TryApply(
            PolicyType.ProcessImageLoadPolicy,
            NoRemoteImages | NoLowMandatoryLabelImages | PreferSystem32Images,
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
