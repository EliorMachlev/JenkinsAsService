// Copyright (c) 2024 All rights reserved

using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace JenkinsAsService;

// Runs an action under the identity of a local/domain account via LogonUser + RunImpersonated.
// Used for Credential Manager / User-scope DPAPI writes so the secret lands in the service
// account's own store rather than the installing admin's.
internal static class ImpersonationRunner
{
    private const char DomainSeparator = '\\';
    private const string LocalDomain = ".";

    // LogonUser logon-type / provider constants (advapi32)
    private const int Logon32LogonInteractive = 2;
    private const int Logon32ProviderDefault = 0;

    // Runs action under the identity of the given local/domain account.
    // The account must have "Log on locally" rights on this machine.
    public static void Run(string username, string password, Action action)
    {
        var domain = LocalDomain;
        var user = username;

        if (username.Contains(DomainSeparator))
        {
            var parts = username.Split(DomainSeparator, 2);
            domain = parts[0];
            user = parts[1];
        }

        if (!NativeMethods.LogonUser(user, domain, password,
                Logon32LogonInteractive, Logon32ProviderDefault, out var token))
        {
            var err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"LogonUser failed for '{username}' (Win32 error {err}). " +
                "Verify the credentials and that the account has 'Log on locally' rights.");
        }

        using (token)
        {
            WindowsIdentity.RunImpersonated(token, action);
        }
    }

    private static class NativeMethods
    {
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool LogonUser(
            string lpszUsername,
            string lpszDomain,
            string lpszPassword,
            int dwLogonType,
            int dwLogonProvider,
            out SafeAccessTokenHandle phToken);
    }
}
