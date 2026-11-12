// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService.Tests;

/// <summary>
/// The test classes that redirect <see cref="Console.Out"/>/<see cref="Console.Error"/> to capture what a
/// command prints. The console is process-wide and xUnit runs classes in parallel, so two of them capturing at
/// once would read each other's output and restore each other's writers. One collection runs them in turn.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ConsoleCollection
{
    public const string Name = "Console capture";
}
