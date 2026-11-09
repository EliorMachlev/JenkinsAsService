// Copyright (c) 2024 All rights reserved
using FluentAssertions;

namespace JenkinsAsService.Tests;

public class ProcessMitigationsTests
{
    [Fact]
    public void Apply_does_not_throw_and_applies_image_load_policy_on_windows()
    {
        var warnings = new List<string>();

        var act = () => ProcessMitigations.Apply(MitigationMask.All, warnings.Add);

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
            ProcessMitigations.Apply(MitigationMask.All);
            ProcessMitigations.Apply(MitigationMask.All);
        };

        act.Should().NotThrow();
    }

    // Masks are asserted through ImageLoadFlags rather than by applying them and reading the policy back.
    // A mitigation policy cannot be lowered once set, and it is inherited by every process the test host
    // spawns afterwards - so a test that touched the real policy would depend on which test ran first and
    // permanently harden the runner.
    [Theory]
    [InlineData("111", NoRemote | NoLowLabel | PreferSystem32)]
    [InlineData("101", NoRemote | PreferSystem32)]
    [InlineData("011", NoLowLabel | PreferSystem32)]
    [InlineData("110", NoRemote | NoLowLabel)]
    [InlineData("100", NoRemote)]
    [InlineData("010", NoLowLabel)]
    [InlineData("001", PreferSystem32)]
    [InlineData("000", 0u)]
    public void ImageLoadFlags_match_the_digits(string digits, uint expected)
    {
        MitigationMask.TryParse(digits, out var mask).Should().BeTrue();
        mask.ImageLoadFlags.Should().Be(expected);
        mask.ToString().Should().Be(digits);
    }

    [Theory]
    [InlineData("")]
    [InlineData("11")]
    [InlineData("1111")]
    [InlineData("1a1")]
    [InlineData("true")]
    [InlineData("Full")]
    public void TryParse_rejects_anything_that_is_not_three_binary_digits(string value) =>
        MitigationMask.TryParse(value, out _).Should().BeFalse();

    [Fact]
    public void TryParse_trims_surrounding_whitespace()
    {
        MitigationMask.TryParse(" 101 ", out var mask).Should().BeTrue();
        mask.ToString().Should().Be("101");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Resolve_defaults_to_all_when_unset_without_warning(string? configured)
    {
        var warnings = new List<string>();
        ProcessMitigations.Resolve(configured, warnings.Add).Should().Be(MitigationMask.All);
        warnings.Should().BeEmpty();
    }

    [Theory]
    [InlineData("000", "000")]
    [InlineData("101", "101")]
    public void Resolve_honours_a_valid_value(string configured, string expected) =>
        ProcessMitigations.Resolve(configured).ToString().Should().Be(expected);

    [Theory]
    [InlineData("Off")]
    [InlineData("AllowNetworkImages")]
    [InlineData("10")]
    public void Resolve_fails_towards_all_and_warns_on_a_malformed_value(string configured)
    {
        var warnings = new List<string>();

        // A stale enum name from an older build must never be read as "turn it off".
        ProcessMitigations.Resolve(configured, warnings.Add).Should().Be(MitigationMask.All);
        warnings.Should().ContainSingle().Which.Should().Contain(configured);
    }

    [Fact]
    public void Apply_with_no_digits_set_still_does_not_throw()
    {
        var act = () => ProcessMitigations.Apply(new MitigationMask(false, false, false));
        act.Should().NotThrow();
    }

    // PROCESS_MITIGATION_IMAGE_LOAD_POLICY bits, restated here so a silent edit to the production
    // constants fails a test instead of quietly redefining what each level means.
    private const uint NoRemote = 1u << 0;
    private const uint NoLowLabel = 1u << 1;
    private const uint PreferSystem32 = 1u << 2;
}
