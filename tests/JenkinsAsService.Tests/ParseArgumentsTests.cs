// Copyright (c) 2024 All rights reserved
using FluentAssertions;

namespace JenkinsAsService.Tests;

public class ParseArgumentsTests
{
    [Fact]
    public void Returns_empty_for_null()
    {
        AgentArgumentParser.Parse(null).Should().BeEmpty();
    }

    [Fact]
    public void Returns_empty_for_whitespace()
    {
        AgentArgumentParser.Parse("   ").Should().BeEmpty();
    }

    [Fact]
    public void Splits_simple_arguments()
    {
        AgentArgumentParser.Parse("-noCertificateCheck -webSocket")
            .Should().Equal("-noCertificateCheck", "-webSocket");
    }

    [Fact]
    public void Handles_quoted_arguments_with_spaces()
    {
        AgentArgumentParser.Parse("-Xmx512m \"-Dpath=C:\\Program Files\\Java\"")
            .Should().Equal("-Xmx512m", "-Dpath=C:\\Program Files\\Java");
    }

    [Fact]
    public void Handles_extra_whitespace()
    {
        AgentArgumentParser.Parse("  -a   -b   -c  ")
            .Should().Equal("-a", "-b", "-c");
    }

    [Fact]
    public void Handles_single_argument()
    {
        AgentArgumentParser.Parse("-noCertificateCheck")
            .Should().ContainSingle()
            .Which.Should().Be("-noCertificateCheck");
    }

    [Fact]
    public void Handles_escaped_quotes_inside_quoted_argument()
    {
        AgentArgumentParser.Parse(@"""-Dmsg=hello \""world\""""")
            .Should().ContainSingle()
            .Which.Should().Be(@"-Dmsg=hello ""world""");
    }
}
