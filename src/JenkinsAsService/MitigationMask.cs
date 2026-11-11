// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

/// <summary>
/// Which of the three image-load process mitigations the service applies to itself, written as three
/// digits — <c>NoRemoteImages</c>, <c>NoLowLabelImages</c>, <c>PreferSystem32Images</c>, in that order, each
/// <c>1</c> (applied) or <c>0</c> (not applied).
/// <list type="bullet">
/// <item><c>"111"</c> — everything (the default).</item>
/// <item><c>"011"</c> — drops only the network-DLL block (first digit), keeping the low-integrity block and
/// the System32 preference. This is the build-relevant case: a build step that runs tooling or loads a
/// native DLL from a UNC path or mapped drive. It is what the old <c>AllowNetworkImages</c> level meant.</item>
/// <item><c>"101"</c> — drops only the low-integrity block (second digit).</item>
/// <item><c>"000"</c> — applies none of them.</item>
/// </list>
/// <para>
/// Three independent bits rather than an ordered dial, because these policies are <strong>inherited by
/// child processes</strong> and Windows offers no way to drop one for a child that the parent holds — see
/// <see cref="ProcessMitigations"/>. Sparing the build tree a mitigation therefore means not taking it in
/// the service either, so each one is a separate decision with its own cost, and no single ordering of
/// them is right for every pipeline.
/// </para>
/// <para>
/// This governs the image-load policy only. Extension-point-disable is applied unconditionally and is not
/// represented here.
/// </para>
/// <para>
/// Stored internally as the <em>disabled</em> bits, so <c>default(MitigationMask)</c> — an uninitialised
/// field, a <c>default</c> argument — is <see cref="All"/>, never "nothing applied". A forgotten
/// initialisation must fail closed.
/// </para>
/// </summary>
public readonly struct MitigationMask : IEquatable<MitigationMask>
{
    /// <summary>Number of digits in the textual form. The parse is exact-length, not "at least".</summary>
    internal const int DigitCount = 3;

    // PROCESS_MITIGATION_IMAGE_LOAD_POLICY flag bits. Declared here rather than duplicated from
    // ProcessMitigations because this type is what turns a config string into them.
    internal const uint NoRemoteImagesFlag = 1u << 0;
    internal const uint NoLowLabelImagesFlag = 1u << 1;
    internal const uint PreferSystem32ImagesFlag = 1u << 2;

    private const uint AllFlags = NoRemoteImagesFlag | NoLowLabelImagesFlag | PreferSystem32ImagesFlag;

    // The image-load bits that are NOT applied. Inverted on purpose: zero, which is what default() and
    // every uninitialised field hold, then means "everything applied".
    private readonly uint _disabled;

    public MitigationMask(bool noRemoteImages, bool noLowLabelImages, bool preferSystem32Images)
    {
        _disabled =
            (noRemoteImages ? 0 : NoRemoteImagesFlag)
            | (noLowLabelImages ? 0 : NoLowLabelImagesFlag)
            | (preferSystem32Images ? 0 : PreferSystem32ImagesFlag);
    }

    /// <summary>Block DLL loads from UNC paths and mapped network drives. The one with build-visible
    /// consequences: a build step that runs tooling from a share fails under the service.</summary>
    public bool NoRemoteImages => (_disabled & NoRemoteImagesFlag) == 0;

    /// <summary>Block DLLs written by low-integrity processes.</summary>
    public bool NoLowLabelImages => (_disabled & NoLowLabelImagesFlag) == 0;

    /// <summary>Resolve known system DLLs from <c>%WinDir%\System32</c> first.</summary>
    public bool PreferSystem32Images => (_disabled & PreferSystem32ImagesFlag) == 0;

    /// <summary>All three applied — the default, what the installer sends for "Full", and
    /// <c>default(MitigationMask)</c>.</summary>
    public static MitigationMask All => default;

    /// <summary>The flag word passed to <c>SetProcessMitigationPolicy</c>.</summary>
    public uint ImageLoadFlags => AllFlags & ~_disabled;

    /// <summary>
    /// Parses the three-digit form. Exact length, digits <c>0</c> and <c>1</c> only, so a value that has
    /// drifted to something else — a stale enum name from an older build, a truncated string, an operator's
    /// "true" — is rejected rather than silently read as a weaker mask. Legacy names are a separate,
    /// deliberate step: <see cref="TryFromLegacyName"/>.
    /// </summary>
    public static bool TryParse(string? value, out MitigationMask mask)
    {
        mask = All;
        var trimmed = value?.Trim();
        if (trimmed is null || trimmed.Length != DigitCount)
        {
            return false;
        }

        foreach (var c in trimmed)
        {
            if (c is not ('0' or '1'))
            {
                return false;
            }
        }

        mask = new MitigationMask(trimmed[0] == '1', trimmed[1] == '1', trimmed[2] == '1');
        return true;
    }

    /// <summary>
    /// Maps the level names an earlier build (commit 418d8e4) wrote to <c>Hardening:ProcessMitigations</c> —
    /// <c>Full</c>, <c>AllowNetworkImages</c>, <c>Off</c> — to the mask with the same image-load effect, so a
    /// config written by that build keeps its meaning instead of being reset to <see cref="All"/>.
    /// <c>AllowNetworkImages</c> applied the low-integrity block and the System32 preference but not the
    /// network-DLL block, which is <c>"011"</c>. Case-insensitive, matching how the configuration binder
    /// read the enum.
    /// <para>
    /// For <em>stored</em> values only. The CLI and the MSI property accept the three digits and nothing
    /// else, so a name typed there is still a hard failure.
    /// </para>
    /// </summary>
    internal static bool TryFromLegacyName(string? value, out MitigationMask mask)
    {
        switch (value?.Trim().ToUpperInvariant())
        {
            case "FULL":
                mask = All;
                return true;
            case "ALLOWNETWORKIMAGES":
                mask = new MitigationMask(noRemoteImages: false, noLowLabelImages: true, preferSystem32Images: true);
                return true;
            case "OFF":
                mask = new MitigationMask(false, false, false);
                return true;
            default:
                mask = All;
                return false;
        }
    }

    /// <summary>The three-digit form, which is exactly what is stored in appsettings.json.</summary>
    public override string ToString() =>
        $"{Digit(NoRemoteImages)}{Digit(NoLowLabelImages)}{Digit(PreferSystem32Images)}";

    private static char Digit(bool set) => set ? '1' : '0';

    public bool Equals(MitigationMask other) => _disabled == other._disabled;

    public override bool Equals(object? obj) => obj is MitigationMask other && Equals(other);

    public override int GetHashCode() => (int)ImageLoadFlags;

    public static bool operator ==(MitigationMask left, MitigationMask right) => left.Equals(right);

    public static bool operator !=(MitigationMask left, MitigationMask right) => !left.Equals(right);
}
