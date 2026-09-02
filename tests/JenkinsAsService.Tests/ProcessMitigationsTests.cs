// Copyright (c) 2024 All rights reserved
using FluentAssertions;

namespace JenkinsAsService.Tests;

public class ProcessMitigationsTests
{
    [Fact]
    public void Apply_does_not_throw_and_applies_image_load_policy_on_windows()
    {
        var warnings = new List<string>();

        var act = () => ProcessMitigations.Apply(MitigationLevel.Full, warnings.Add);

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

    // The levels are asserted through ImageLoadFlags rather than by applying them and reading the policy
    // back. A mitigation policy cannot be lowered once set, and it is inherited by every process the test
    // host spawns afterwards - so an "AllowNetworkImages really drops NoRemoteImages" test that touched the
    // real policy would both depend on which test ran first and permanently harden the runner.
    [Theory]
    [InlineData(MitigationLevel.Full, NoRemote | NoLowLabel | PreferSystem32)]
    [InlineData(MitigationLevel.AllowNetworkImages, NoLowLabel | PreferSystem32)]
    [InlineData(MitigationLevel.Off, 0u)]
    public void ImageLoadFlags_match_the_level(MitigationLevel level, uint expected) =>
        ProcessMitigations.ImageLoadFlags(level).Should().Be(expected);

    [Fact]
    public void AllowNetworkImages_drops_only_the_network_flag()
    {
        var full = ProcessMitigations.ImageLoadFlags(MitigationLevel.Full);
        var relaxed = ProcessMitigations.ImageLoadFlags(MitigationLevel.AllowNetworkImages);

        // The point of the middle level: exactly one bit less than Full, and it is the UNC one. Asserted as
        // a difference so that adding a fourth image-load flag later cannot silently land in only one level.
        (full & ~relaxed).Should().Be(NoRemote);
        (relaxed & NoRemote).Should().Be(0u);
        (relaxed & (NoLowLabel | PreferSystem32)).Should().Be(NoLowLabel | PreferSystem32);
    }

    [Fact]
    public void Apply_reports_that_it_did_nothing_when_off()
    {
        var warnings = new List<string>();

        // False is what makes Program.cs log the unhardened-service warning; without a return value the
        // operator's only evidence of a reduced posture would be the config file itself.
        ProcessMitigations.Apply(MitigationLevel.Off, warnings.Add).Should().BeFalse();
        warnings.Should().BeEmpty();
    }

    // PROCESS_MITIGATION_IMAGE_LOAD_POLICY bits, restated here so a silent edit to the production
    // constants fails a test instead of quietly redefining what each level means.
    private const uint NoRemote = 1u << 0;
    private const uint NoLowLabel = 1u << 1;
    private const uint PreferSystem32 = 1u << 2;
}
