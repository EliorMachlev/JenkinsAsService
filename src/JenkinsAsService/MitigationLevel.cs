// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

/// <summary>
/// How much of the Win32 process-mitigation subset the service applies to itself
/// (<c>Jenkins:Hardening:ProcessMitigations</c>).
/// <para>
/// The levels are a single ordered dial rather than independent switches because mitigation policies are
/// <strong>inherited by child processes</strong> and Windows offers no way to drop one for a child that
/// the parent holds — see <see cref="ProcessMitigations"/>. Sparing the build tree therefore means not
/// taking the mitigation in the service either, so every level below <see cref="Full"/> is a real
/// reduction in the service's own hardening, not a redistribution of it.
/// </para>
/// </summary>
public enum MitigationLevel
{
    /// <summary>
    /// Default. Applies the whole safe subset: block DLL loads from UNC/network paths, block DLLs written
    /// by low-integrity processes, prefer <c>%WinDir%\System32</c> for known system DLLs, and disable
    /// legacy extension-point injection.
    /// </summary>
    Full,

    /// <summary>
    /// Everything in <see cref="Full"/> except the network-image block. Use when a build step runs tooling,
    /// or loads a native DLL, from a UNC path or mapped drive — under <see cref="Full"/> that fails inside
    /// the build with a loader error naming nothing to do with this service.
    /// </summary>
    AllowNetworkImages,

    /// <summary>
    /// Applies nothing. The escape hatch for a pipeline broken by a mitigation not covered by
    /// <see cref="AllowNetworkImages"/>; prefer that level, which keeps two of the three image-load
    /// protections.
    /// </summary>
    Off
}
