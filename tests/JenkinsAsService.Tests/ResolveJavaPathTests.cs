using FluentAssertions;

namespace JenkinsAsService.Tests;

public class ResolveJavaPathTests
{
    [Fact]
    public void Throws_when_no_java_found()
    {
        var act = () => JenkinsAgentWorker.ResolveJavaPath(null, null);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*java.exe*");
    }

    [Fact]
    public void Throws_when_paths_dont_contain_java()
    {
        var act = () => JenkinsAgentWorker.ResolveJavaPath(@"C:\nonexistent", @"C:\also-nonexistent");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*java.exe*");
    }

    [Fact]
    public void Resolves_from_configured_path()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "JAS_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllBytes(Path.Combine(tempDir, "java.exe"), []);

            JenkinsAgentWorker.ResolveJavaPath(tempDir, null)
                .Should().Be(Path.Combine(tempDir, "java.exe"));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Resolves_from_JAVA_HOME_bin()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "JAS_Test_" + Guid.NewGuid().ToString("N"));
        var binDir = Path.Combine(tempDir, "bin");
        Directory.CreateDirectory(binDir);

        try
        {
            File.WriteAllBytes(Path.Combine(binDir, "java.exe"), []);

            JenkinsAgentWorker.ResolveJavaPath(null, tempDir)
                .Should().Be(Path.Combine(binDir, "java.exe"));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Configured_path_takes_priority_over_JAVA_HOME()
    {
        var configDir = Path.Combine(Path.GetTempPath(), "JAS_Config_" + Guid.NewGuid().ToString("N"));
        var homeDir = Path.Combine(Path.GetTempPath(), "JAS_Home_" + Guid.NewGuid().ToString("N"));
        var homeBin = Path.Combine(homeDir, "bin");
        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(homeBin);

        try
        {
            File.WriteAllBytes(Path.Combine(configDir, "java.exe"), []);
            File.WriteAllBytes(Path.Combine(homeBin, "java.exe"), []);

            JenkinsAgentWorker.ResolveJavaPath(configDir, homeDir)
                .Should().Be(Path.Combine(configDir, "java.exe"));
        }
        finally
        {
            Directory.Delete(configDir, recursive: true);
            Directory.Delete(homeDir, recursive: true);
        }
    }
}
