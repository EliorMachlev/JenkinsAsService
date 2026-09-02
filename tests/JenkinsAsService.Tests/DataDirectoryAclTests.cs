// Copyright (c) 2024 All rights reserved

using System.Security.AccessControl;
using System.Security.Principal;
using FluentAssertions;

namespace JenkinsAsService.Tests;

public class DataDirectoryAclTests : IDisposable
{
    private const int TempDirSuffixLength = 8;
    private readonly string _tempDir;

    public DataDirectoryAclTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "JAS_DataAcl_" + Guid.NewGuid().ToString("N")[..TempDirSuffixLength]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Grant_gives_the_account_modify_access()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // ACLs are a Windows-only concept
        }

        var account = WindowsIdentity.GetCurrent().Name;

        DataDirectoryAcl.Grant(_tempDir, account).Should().BeTrue();

        var rule = ExplicitRulesFor(_tempDir, WindowsIdentity.GetCurrent().User!.Value).Should().ContainSingle().Subject;
        rule.FileSystemRights.Should().HaveFlag(FileSystemRights.Write);
        rule.FileSystemRights.Should().HaveFlag(FileSystemRights.Delete,
            "the service deletes its own secret file and rotates its logs");
    }

    /// <summary>
    /// The inverse of <see cref="ConfigAclHardenerTests"/>: the config file's DACL is protected, this one
    /// must NOT be, or the folder loses the inherited SYSTEM/Administrators full control it depends on.
    /// </summary>
    [Fact]
    public void Grant_leaves_inheritance_enabled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        DataDirectoryAcl.Grant(_tempDir, WindowsIdentity.GetCurrent().Name);

        new DirectoryInfo(_tempDir).GetAccessControl().AreAccessRulesProtected
            .Should().BeFalse("the data folder must keep inheriting SYSTEM/Administrators full control");
    }

    [Fact]
    public void Grant_preserves_pre_existing_entries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Seed an unrelated explicit ACE. Without this the "preserved" assertion would pass vacuously,
        // since a temp folder carries only inherited entries.
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var seeded = new DirectoryInfo(_tempDir).GetAccessControl();
        seeded.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Read, AccessControlType.Allow));
        new DirectoryInfo(_tempDir).SetAccessControl(seeded);

        DataDirectoryAcl.Grant(_tempDir, WindowsIdentity.GetCurrent().Name);

        ExplicitRulesFor(_tempDir, users.Value).Should().NotBeEmpty("granting one account must not clear the DACL");
    }

    [Fact]
    public void Grant_is_idempotent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var account = WindowsIdentity.GetCurrent().Name;
        var sid = WindowsIdentity.GetCurrent().User!.Value;

        // Runs on install, upgrade AND repair, so a second pass must replace the account's entry rather
        // than stack a duplicate on top of it.
        DataDirectoryAcl.Grant(_tempDir, account);
        DataDirectoryAcl.Grant(_tempDir, account);

        ExplicitRulesFor(_tempDir, sid).Should().ContainSingle();
    }

    [Fact]
    public void Grant_creates_a_missing_directory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var nested = Path.Combine(_tempDir, "not-yet-there");

        DataDirectoryAcl.Grant(nested, WindowsIdentity.GetCurrent().Name).Should().BeTrue();

        Directory.Exists(nested).Should().BeTrue();
    }

    [Fact]
    public void Grant_reports_failure_for_an_unresolvable_account()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var messages = new List<string>();

        // Returns false rather than throwing: the caller (the MSI custom action) decides whether an
        // unresolvable identity fails the install.
        DataDirectoryAcl.Grant(_tempDir, @"NO SUCH DOMAIN\nobody-" + Guid.NewGuid().ToString("N"), messages.Add)
            .Should().BeFalse();

        messages.Should().ContainSingle(m => m.StartsWith("Error:", StringComparison.Ordinal));
    }

    private static List<FileSystemAccessRule> ExplicitRulesFor(string directory, string sid) =>
        new DirectoryInfo(directory).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(r => ((SecurityIdentifier)r.IdentityReference).Value == sid
                && r.AccessControlType == AccessControlType.Allow)
            .ToList();
}
