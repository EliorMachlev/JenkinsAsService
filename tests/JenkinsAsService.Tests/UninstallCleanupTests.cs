// Copyright (c) 2024 All rights reserved
using System.Text.Json;
using FluentAssertions;

namespace JenkinsAsService.Tests;

public class UninstallCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jas-purge-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    // ---- IsSafeToDelete: the guard on a recursive delete that runs as SYSTEM --------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"C:\")]                    // drive root
    [InlineData(@"C:")]                     // drive, no separator
    [InlineData(@"C:\Data")]                // single segment - too close to the root to be intentional
    [InlineData(@"C:\Data\")]               // ...including with a trailing separator
    [InlineData("relative\\path")]          // resolves against the CWD, which a deferred CA does not control
    [InlineData("C:data")]                  // drive-relative, same problem
    public void Refuses_paths_that_are_not_clearly_a_data_directory(string? path)
    {
        UninstallCleanup.IsSafeToDelete(path).Should().BeFalse();
    }

    [Theory]
    [InlineData(Environment.SpecialFolder.Windows)]
    [InlineData(Environment.SpecialFolder.System)]
    [InlineData(Environment.SpecialFolder.ProgramFiles)]
    [InlineData(Environment.SpecialFolder.CommonApplicationData)]
    [InlineData(Environment.SpecialFolder.UserProfile)]
    public void Refuses_well_known_system_directories(Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder);
        if (string.IsNullOrEmpty(path))
        {
            return; // not present on this machine
        }

        UninstallCleanup.IsSafeToDelete(path).Should().BeFalse();
        UninstallCleanup.IsSafeToDelete(path + Path.DirectorySeparatorChar).Should().BeFalse();
    }

    [Fact]
    public void Accepts_the_default_data_directory()
    {
        // %ProgramData%\JenkinsAsService - the shipped default, and the case that must not be refused.
        UninstallCleanup.IsSafeToDelete(DataPaths.ComputeDataDirectory(null)).Should().BeTrue();
    }

    [Fact]
    public void Accepts_a_relocated_data_directory()
    {
        UninstallCleanup.IsSafeToDelete(@"D:\JenkinsData\agent-node-1").Should().BeTrue();
    }

    // ---- Purge ---------------------------------------------------------------------------------------

    [Fact]
    public void Deletes_the_data_tree_and_the_generated_config()
    {
        var install = CreateDirectory("install");
        var data = CreateDirectory("data", "JenkinsAsServiceData");
        var config = Path.Combine(install, "appsettings.json");
        File.WriteAllText(config, """{"Jenkins":{"Secret":{"Value":"s3cret"}}}""");
        Directory.CreateDirectory(Path.Combine(data, "agent"));
        File.WriteAllText(Path.Combine(data, "agent", "agent.jar"), "jar");
        File.WriteAllText(Path.Combine(data, "agent.log"), "log");

        UninstallCleanup.Purge(install, data, _ => { });

        Directory.Exists(data).Should().BeFalse("the data tree is removed with its contents");
        File.Exists(config).Should().BeFalse("the config holds the secret and must not survive uninstall");
    }

    [Fact]
    public void Leaves_a_system_directory_alone_and_says_so()
    {
        var install = CreateDirectory("install");
        var unsafeTarget = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var messages = new List<string>();

        UninstallCleanup.Purge(install, unsafeTarget, messages.Add);

        Directory.Exists(unsafeTarget).Should().BeTrue();
        messages.Should().ContainSingle(m => m.Contains("Refusing", StringComparison.Ordinal));
    }

    [Fact]
    public void Succeeds_when_there_is_nothing_left_to_remove()
    {
        var install = CreateDirectory("install");
        var missing = Path.Combine(_root, "never-created");

        var act = () => UninstallCleanup.Purge(install, missing, _ => { });

        act.Should().NotThrow("an uninstall must complete even if a previous run already cleaned up");
    }

    [Fact]
    public void Reports_rather_than_throws_when_a_file_is_locked()
    {
        var install = CreateDirectory("install");
        var data = CreateDirectory("data", "JenkinsAsServiceData");
        var locked = Path.Combine(data, "agent.log");
        File.WriteAllText(locked, "log");
        var messages = new List<string>();

        // A running agent holds its log open; uninstall must still complete rather than fault.
        using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var act = () => UninstallCleanup.Purge(install, data, messages.Add);
            act.Should().NotThrow();
        }

        messages.Should().Contain(m => m.Contains("Could not remove", StringComparison.Ordinal));
    }

    // ---- PurgeCommand: the CLI surface the installer invokes ------------------------------------------

    [Fact]
    public void The_purge_command_removes_the_configured_data_directory()
    {
        // Not the default location: an operator who relocated Agent:DataDirectory must have THEIR tree
        // removed, and reading it back out of the config is the only way the command can know that.
        var install = CreateDirectory("install");
        var data = CreateDirectory("relocated", "JenkinsData");
        // Serialized rather than hand-escaped: a Windows path in hand-written JSON needs doubled backslashes,
        // and getting that wrong makes the test fail for a reason unrelated to what it is checking.
        File.WriteAllText(
            Path.Combine(install, "appsettings.json"),
            JsonSerializer.Serialize(new { Jenkins = new { Agent = new { DataDirectory = data } } }));
        File.WriteAllText(Path.Combine(data, "agent.log"), "log");

        var exitCode = PurgeCommand.Run([PurgeCommand.Name], install);

        exitCode.Should().Be(0);
        Directory.Exists(data).Should().BeFalse();
        File.Exists(Path.Combine(install, "appsettings.json")).Should().BeFalse();
    }

    [Fact]
    public void The_purge_command_is_named_as_a_verb_the_installer_invokes_directly()
    {
        // Guards the contract with Package.wxs: the uninstall custom action runs `purge`, so a rename here
        // silently breaks uninstall on a real machine - the CA would fail and Return="ignore" would hide it.
        PurgeCommand.Name.Should().Be("purge");
    }

    [Fact]
    public void The_purge_command_help_does_not_delete_anything()
    {
        var install = CreateDirectory("install");
        var data = CreateDirectory("data", "JenkinsAsServiceData");
        File.WriteAllText(Path.Combine(install, "appsettings.json"), "{}");

        var exitCode = PurgeCommand.Run([PurgeCommand.Name, "--help"], install);

        exitCode.Should().Be(0);
        Directory.Exists(data).Should().BeTrue("--help must never be destructive");
        File.Exists(Path.Combine(install, "appsettings.json")).Should().BeTrue();
    }

    private string CreateDirectory(params string[] segments)
    {
        var path = Path.Combine([_root, .. segments]);
        Directory.CreateDirectory(path);
        return path;
    }
}
