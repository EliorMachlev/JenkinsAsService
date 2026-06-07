using FluentAssertions;

namespace JenkinsAsService.Tests;

public class ParseArgumentsTests
{
    [Fact]
    public void Returns_empty_for_null()
    {
        JenkinsAgentWorker.ParseArguments(null).Should().BeEmpty();
    }

    [Fact]
    public void Returns_empty_for_whitespace()
    {
        JenkinsAgentWorker.ParseArguments("   ").Should().BeEmpty();
    }

    [Fact]
    public void Splits_simple_arguments()
    {
        JenkinsAgentWorker.ParseArguments("-noCertificateCheck -webSocket")
            .Should().Equal("-noCertificateCheck", "-webSocket");
    }

    [Fact]
    public void Handles_quoted_arguments_with_spaces()
    {
        JenkinsAgentWorker.ParseArguments("-Xmx512m \"-Dpath=C:\\Program Files\\Java\"")
            .Should().Equal("-Xmx512m", "-Dpath=C:\\Program Files\\Java");
    }

    [Fact]
    public void Handles_extra_whitespace()
    {
        JenkinsAgentWorker.ParseArguments("  -a   -b   -c  ")
            .Should().Equal("-a", "-b", "-c");
    }

    [Fact]
    public void Handles_single_argument()
    {
        JenkinsAgentWorker.ParseArguments("-noCertificateCheck")
            .Should().ContainSingle()
            .Which.Should().Be("-noCertificateCheck");
    }

    [Fact]
    public void Handles_escaped_quotes_inside_quoted_argument()
    {
        JenkinsAgentWorker.ParseArguments(@"""-Dmsg=hello \""world\""""")
            .Should().ContainSingle()
            .Which.Should().Be(@"-Dmsg=hello ""world""");
    }
}
