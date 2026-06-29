// Copyright (c) 2024 All rights reserved

using System.Diagnostics;
using System.Runtime.Versioning;

namespace JenkinsAsService;

/// <summary>
/// Best-effort management of the Windows Event Log source. Creating a source writes to HKLM and
/// requires elevation — something the low-privilege service account cannot do at runtime. The
/// installer (running as SYSTEM) pre-creates it; the service only verifies it exists.
/// </summary>
public static class EventLogSourceInstaller
{
    /// <summary>
    /// Returns <c>true</c> if the event source exists or was successfully created. Creation is only
    /// attempted when the caller is elevated; any failure is swallowed and returns <c>false</c>
    /// so callers can fall back to file logging. No-op (returns false) on non-Windows.
    /// </summary>
    public static bool Ensure(string source, string logName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return EnsureWindows(source, logName);
    }

    [SupportedOSPlatform("windows")]
    private static bool EnsureWindows(string source, string logName)
    {
        try
        {
            if (EventLog.SourceExists(source))
            {
                return true;
            }

            EventLog.CreateEventSource(new EventSourceCreationData(source, logName));
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or InvalidOperationException
            or System.ComponentModel.Win32Exception or UnauthorizedAccessException or ArgumentException)
        {
            // Not elevated (typical for the low-privilege service account) or the log is unavailable.
            return false;
        }
    }
}
