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
}
