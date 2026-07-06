// Copyright (c) 2024 All rights reserved
using FluentAssertions;

namespace JenkinsAsService.Tests;

public class ProcessMitigationsTests
{
    [Fact]
    public void Apply_does_not_throw_and_applies_image_load_policy_on_windows()
    {
        var warnings = new List<string>();

        var act = () => ProcessMitigations.Apply(warnings.Add);

        act.Should().NotThrow();

        if (OperatingSystem.IsWindows())
        {
            // The image-load policy is settable at runtime on every supported Windows build, so a clean
            // P/Invoke must apply it without warning. (Extension-point-disable can legitimately return
            // ACCESS_DENIED in some hosts — that is best-effort and intentionally tolerated.)
            warnings.Should().NotContain(w => w.Contains("image-load", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Apply_is_idempotent()
    {
        var act = () =>
        {
            ProcessMitigations.Apply();
            ProcessMitigations.Apply();
        };

        act.Should().NotThrow();
    }
}
