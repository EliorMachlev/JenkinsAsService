// Copyright (c) 2024 All rights reserved

using System.Security.AccessControl;
using System.Security.Principal;
using FluentAssertions;

namespace JenkinsAsService.Tests;

public class AgentSecretFileTests : IDisposable
{
    private const int TempDirSuffixLength = 8;
    private readonly string _tempDir;

    public AgentSecretFileTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "JAS_Secret_" + Guid.NewGuid().ToString("N")[..TempDirSuffixLength]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void Write_persists_secret_verbatim_without_bom_or_newline()
    {
        const string secret = "s3cr3t-jnlp-token";

        var path = AgentSecretFile.Write(_tempDir, secret);

        Path.GetFileName(path).Should().Be(AgentSecretFile.FileName);
        // Read raw bytes: must equal the UTF-8 secret exactly — no BOM prefix, no trailing newline.
        File.ReadAllBytes(path).Should().Equal(System.Text.Encoding.UTF8.GetBytes(secret));
    }

    [Fact]
    public void Write_overwrites_existing_file()
    {
        AgentSecretFile.Write(_tempDir, "first");
        var path = AgentSecretFile.Write(_tempDir, "second");

        File.ReadAllText(path).Should().Be("second");
    }

    [Fact]
    public void Delete_removes_the_file_and_is_safe_on_missing_path()
    {
        var path = AgentSecretFile.Write(_tempDir, "x");

        AgentSecretFile.Delete(path);
        File.Exists(path).Should().BeFalse();

        // Second delete (already gone) and a null path must not throw.
        AgentSecretFile.Delete(path);
        AgentSecretFile.Delete(null);
    }

    [Fact]
    public void Write_restricts_acl_to_system_and_admins()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // ACL hardening is a Windows-only operation
        }

        var path = AgentSecretFile.Write(_tempDir, "secret");

        var security = new FileInfo(path).GetAccessControl();
        security.AreAccessRulesProtected.Should().BeTrue("inheritance must be broken on the secret file");

        var sids = security.GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(r => ((SecurityIdentifier)r.IdentityReference).Value)
            .ToList();

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value;
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value;

        sids.Should().Contain(system);
        sids.Should().Contain(admins);
        sids.Should().NotContain(users, "the Users group must not be able to read the JNLP secret");
    }
}
