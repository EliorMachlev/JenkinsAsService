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
/// So <c>Jenkins:Hardening:ProcessMitigations</c> selects what the <em>service</em> takes, not what it
/// passes down — three independent digits, see <see cref="MitigationMask"/>. Only
/// <see cref="MitigationMask.NoRemoteImagesFlag"/> has build-visible consequences, but each is its own
/// decision because none of them can be withheld from the build tree while the service holds it.
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

    // The PROCESS_MITIGATION_IMAGE_LOAD_POLICY bits live on MitigationMask, which is what turns the
    // configured digits into them - one definition, so the config format and the P/Invoke cannot disagree.

    // PROCESS_MITIGATION_EXTENSION_POINT_DISABLE_POLICY flag bits.
    // Current Windows enables this before the process runs, so the call below commonly fails with
    // ERROR_ACCESS_DENIED against a policy that is already in force (measured on 11 26200). Kept because
    // the guarantee is not documented for every supported OS, and a failure here is only a warning.
    private const uint DisableExtensionPoints = 1u << 0;   // Block legacy injection (AppInit_DLLs, Winsock LSPs, IMEs).

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessMitigationPolicy(PolicyType policy, ref uint buffer, nuint length);

    /// <summary>
    /// Resolves the configured value to a mask, falling back to <see cref="MitigationMask.All"/> when it is
    /// missing or malformed and reporting that via <paramref name="warn"/>. Pure, so the one part of this
    /// type carrying a decision is testable without touching the real policy — which cannot be undone once
    /// set, and is inherited by everything the test process later spawns.
    /// </summary>
    internal static MitigationMask Resolve(string? configured, Action<string>? warn = null)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return MitigationMask.All;
        }

        if (MitigationMask.TryParse(configured, out var mask))
        {
            return mask;
        }

        // A level name written by the previous build (Full / AllowNetworkImages / Off) keeps the meaning it
        // was written with. Warned, so the operator re-saves it as digits, but honoured: resetting it to all
        // three would break the very build the operator lowered it for, at the first service restart.
        if (MitigationMask.TryFromLegacyName(configured, out var legacy))
        {
            warn?.Invoke(
                $"{ConfigKeys.Hardening.ProcessMitigationsPath} uses a level name from an earlier version; "
                + "applying its three-digit equivalent. Rewrite the setting as three 0/1 digits.");
            return legacy;
        }

        // Fail towards the most protective set, never the least: a value this build cannot read is a
        // mistake, and guessing "they meant to turn things off" would silently weaken the agent. The value
        // itself is deliberately not echoed - messages about invalid input name the setting, never its value.
        warn?.Invoke(
            $"{ConfigKeys.Hardening.ProcessMitigationsPath}: expected three characters, each 0 or 1; "
            + "applying all mitigations.");
        return MitigationMask.All;
    }

    /// <summary>
    /// Applies the image-load mitigations selected by <paramref name="mask"/>, plus extension-point-disable
    /// which is unconditional. No-op on non-Windows. Each policy is best-effort: a failure (older OS,
    /// already-set, or insufficient rights) is reported via <paramref name="warn"/> and skipped.
    /// </summary>
    /// <returns><c>false</c> off-Windows, where nothing was attempted.</returns>
    internal static bool Apply(MitigationMask mask, Action<string>? warn = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        ApplyWindows(mask, warn);
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyWindows(MitigationMask mask, Action<string>? warn)
    {
        TryApply(
            PolicyType.ProcessImageLoadPolicy,
            mask.ImageLoadFlags,
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
