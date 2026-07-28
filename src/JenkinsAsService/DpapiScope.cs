// Copyright (c) 2024 All rights reserved

using System.Security.Cryptography;

namespace JenkinsAsService;

/// <summary>
/// DPAPI protection scope for the agent secret.
/// </summary>
public enum DpapiScope
{
    /// <summary>
    /// Machine-scoped (<c>DataProtectionScope.LocalMachine</c>). Any process on this machine can
    /// decrypt the secret. Works regardless of which identity wrote it — the default for
    /// back-compat and for virtual / gMSA service accounts that cannot be interactively logged on.
    /// </summary>
    Machine = 0,

    /// <summary>
    /// User-scoped (<c>DataProtectionScope.CurrentUser</c>). Only the identity that encrypted the
    /// secret can decrypt it. The secret MUST be written by the same account that runs the service
    /// (use <c>update-secret --impersonate</c> as a password-based dedicated account). Not usable
    /// with virtual accounts or gMSA, which cannot be logged on with a password.
    /// </summary>
    User = 1
}

internal static class DpapiScopeExtensions
{
    /// <summary>Maps the domain scope to the Win32 DPAPI flag. Shared by the write and read sides so the
    /// two can never disagree on which key protects the secret.</summary>
    internal static DataProtectionScope ToProtectionScope(this DpapiScope scope) =>
        scope == DpapiScope.User ? DataProtectionScope.CurrentUser : DataProtectionScope.LocalMachine;
}
