// Copyright (c) 2024 All rights reserved

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace JenkinsAsService;

/// <summary>
/// Sets the SCM failure actions for the installed service: restart on each of the first three consecutive
/// failures, resetting the count after a day without one.
/// <para>
/// The outer half of the recovery story — the in-process watchdog handles a crashed <em>agent</em>, these
/// handle a crashed or given-up <em>service</em>. That is why <c>JenkinsAgentWorker</c> calls
/// <c>StopApplication()</c> on give-up rather than just returning: SCM must see a stopped service to act.
/// </para>
/// <para>Set through the SCM API rather than by WiX; see the installer .wixproj for why neither WiX route works.</para>
/// </summary>
public static class ServiceRecoveryConfigurator
{
    /// <summary>Restart the service on each of the first three consecutive failures.</summary>
    internal const int RestartAttempts = 3;

    /// <summary>Delay before each restart, in milliseconds.</summary>
    internal const int RestartDelayMs = 10_000;

    /// <summary>Seconds without a failure after which the failure count resets (one day).</summary>
    internal const int ResetPeriodSeconds = 86_400;

    /// <summary>
    /// Configures the failure actions for <paramref name="serviceName"/>. No-op on non-Windows platforms.
    /// </summary>
    /// <returns><see langword="true"/> when applied (or not applicable to this platform).</returns>
    public static bool Configure(string serviceName, Action<string>? log = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            ConfigureWindows(serviceName);
            log?.Invoke(
                $"Configured '{serviceName}' to restart on each of the first {RestartAttempts} failures " +
                $"({RestartDelayMs / 1000}s apart).");
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            log?.Invoke($"Error: could not configure recovery actions for '{serviceName}': {ex.Message}");
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ConfigureWindows(string serviceName)
    {
        using var manager = NativeMethods.OpenSCManager(null, null, NativeMethods.ScManagerConnect);
        if (manager.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenSCManager failed.");
        }

        // SERVICE_CHANGE_CONFIG is not sufficient on its own: ChangeServiceConfig2 additionally requires
        // SERVICE_START whenever the actions include SC_ACTION_RESTART, which all of ours do.
        using var service = NativeMethods.OpenService(
            manager, serviceName, NativeMethods.ServiceChangeConfig | NativeMethods.ServiceStart);
        if (service.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenService failed for '{serviceName}'.");
        }

        // SERVICE_FAILURE_ACTIONS holds a raw pointer to the SC_ACTION array, so it cannot be a managed
        // array that the GC is free to move.
        var actionSize = Marshal.SizeOf<NativeMethods.ScAction>();
        var actions = Marshal.AllocHGlobal(actionSize * RestartAttempts);

        try
        {
            for (var i = 0; i < RestartAttempts; i++)
            {
                Marshal.StructureToPtr(
                    new NativeMethods.ScAction { Type = NativeMethods.ScActionRestart, Delay = RestartDelayMs },
                    actions + (i * actionSize),
                    fDeleteOld: false);
            }

            var failureActions = new NativeMethods.ServiceFailureActions
            {
                ResetPeriod = ResetPeriodSeconds,
                // Null leaves the current value alone, which is what we want for both.
                RebootMessage = null,
                Command = null,
                ActionCount = RestartAttempts,
                Actions = actions,
            };

            if (!NativeMethods.ChangeServiceConfig2(
                    service, NativeMethods.ServiceConfigFailureActions, ref failureActions))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "ChangeServiceConfig2 failed.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(actions);
        }
    }

    private static class NativeMethods
    {
        internal const int ScManagerConnect = 0x0001;
        internal const int ServiceChangeConfig = 0x0002;
        internal const int ServiceStart = 0x0010;
        internal const int ServiceConfigFailureActions = 2;
        internal const int ScActionRestart = 1;

        [StructLayout(LayoutKind.Sequential)]
        internal struct ScAction
        {
            internal int Type;
            internal int Delay;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct ServiceFailureActions
        {
            internal int ResetPeriod;
            [MarshalAs(UnmanagedType.LPWStr)] internal string? RebootMessage;
            [MarshalAs(UnmanagedType.LPWStr)] internal string? Command;
            internal int ActionCount;
            internal IntPtr Actions;
        }

        [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern ServiceHandle OpenSCManager(string? machineName, string? databaseName, int access);

        [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern ServiceHandle OpenService(ServiceHandle manager, string serviceName, int access);

        [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ChangeServiceConfig2(
            ServiceHandle service, int infoLevel, ref ServiceFailureActions info);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseServiceHandle(IntPtr handle);

        /// <summary>SCM handles are closed with CloseServiceHandle, not CloseHandle.</summary>
        internal sealed class ServiceHandle() : SafeHandle(IntPtr.Zero, ownsHandle: true)
        {
            public override bool IsInvalid => handle == IntPtr.Zero;

            protected override bool ReleaseHandle() => CloseServiceHandle(handle);
        }
    }
}
