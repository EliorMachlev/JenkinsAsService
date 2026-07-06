// Copyright (c) 2024 All rights reserved

using System.Security.AccessControl;
using System.Security.Principal;
using FluentAssertions;

namespace JenkinsAsService.Tests;

public class ConfigAclHardenerTests : IDisposable
{
    private const int TempDirSuffixLength = 8;
    private readonly string _tempDir;
    private readonly string _file;

    public ConfigAclHardenerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "JAS_Acl_" + Guid.NewGuid().ToString("N")[..TempDirSuffixLength]);
        Directory.CreateDirectory(_tempDir);
        _file = Path.Combine(_tempDir, "appsettings.json");
        File.WriteAllText(_file, "{}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void Harden_disables_inheritance_and_grants_system_and_admins()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // ACL hardening is a Windows-only operation
        }

        // Use the current identity as the "service account" so the NTAccount resolves on any host.
        var account = WindowsIdentity.GetCurrent().Name;

        ConfigAclHardener.Harden(_file, account);

        var security = new FileInfo(_file).GetAccessControl();
        security.AreAccessRulesProtected.Should().BeTrue("inheritance must be disabled so Program Files' Users access is dropped");

        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier));
        var sids = rules.Cast<FileSystemAccessRule>()
            .Select(r => ((SecurityIdentifier)r.IdentityReference).Value)
            .ToList();

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value;
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value;

        sids.Should().Contain(system);
        sids.Should().Contain(admins);
        sids.Should().NotContain(users, "the Users group must no longer have access to the secret-bearing file");
    }

    [Fact]
    public void Harden_removes_a_pre_existing_Users_grant()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // ACL hardening is a Windows-only operation
        }

        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

        // Seed an explicit Users:Read ACE (mimicking the inherited Program Files access) so that a later
        // "Users absent" assertion proves hardening actually REMOVED it — %TEMP% grants no Users access, so
        // without seeding the assertion would pass even if Harden silently no-op'd.
        var seeded = new FileInfo(_file).GetAccessControl();
        seeded.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Read, AccessControlType.Allow));
        new FileInfo(_file).SetAccessControl(seeded);

        // Sanity: the Users ACE is present before hardening.
        SidsOf(_file).Should().Contain(users.Value);

        ConfigAclHardener.Harden(_file, WindowsIdentity.GetCurrent().Name);

        new FileInfo(_file).GetAccessControl().AreAccessRulesProtected.Should().BeTrue();
        SidsOf(_file).Should().NotContain(users.Value, "hardening must strip the seeded Users grant");
    }

    private static List<string> SidsOf(string file) =>
        new FileInfo(file).GetAccessControl()
            .GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(r => ((SecurityIdentifier)r.IdentityReference).Value)
            .ToList();
}
