// Copyright (c) 2024 All rights reserved
using FluentAssertions;

namespace JenkinsAsService.Tests;

public class DataPathsTests
{
    [Fact]
    public void Empty_resolves_to_programdata_appfolder()
    {
        var resolved = DataPaths.ResolveDataDirectory("");

        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            DataPaths.AppFolderName);
        resolved.Should().Be(expected);
    }

    [Fact]
    public void Null_resolves_to_programdata_appfolder()
    {
        var resolved = DataPaths.ResolveDataDirectory(null);

        resolved.Should().EndWith(DataPaths.AppFolderName);
    }

    [Fact]
    public void Explicit_path_is_used_and_created()
    {
        var target = Path.Combine(Path.GetTempPath(), "JAS_Data_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var resolved = DataPaths.ResolveDataDirectory(target);

            resolved.Should().Be(target);
            Directory.Exists(resolved).Should().BeTrue("ResolveDataDirectory creates the directory best-effort");
        }
        finally
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
        }
    }

    [Fact]
    public void Environment_variables_are_expanded()
    {
        var resolved = DataPaths.ResolveDataDirectory(@"%ProgramData%\JAS_EnvTest_Marker");

        resolved.Should().NotContain("%");
        resolved.Should().EndWith(@"JAS_EnvTest_Marker");
    }

    [Fact]
    public void Agent_and_work_dirs_are_created_as_siblings_under_the_data_dir()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "JAS_Sub_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(dataDir);

            var agentDir = DataPaths.ResolveAgentDirectory(dataDir);
            var workDir = DataPaths.ResolveWorkDirectory(dataDir);

            agentDir.Should().Be(Path.Combine(dataDir, DataPaths.AgentFolderName));
            workDir.Should().Be(Path.Combine(dataDir, DataPaths.WorkFolderName));
            Directory.Exists(agentDir).Should().BeTrue("the agent cache subfolder is created best-effort");
            Directory.Exists(workDir).Should().BeTrue("the work subfolder is created best-effort");
            agentDir.Should().NotBe(workDir, "the jar cache must be isolated from build/workspace churn");
        }
        finally
        {
            if (Directory.Exists(dataDir))
            {
                Directory.Delete(dataDir, recursive: true);
            }
        }
    }
}
