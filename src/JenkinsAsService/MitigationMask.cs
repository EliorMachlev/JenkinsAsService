// Copyright (c) 2024 All rights reserved

namespace JenkinsAsService;

/// <summary>
/// Which of the three image-load process mitigations the service applies to itself, written as three
/// digits — <c>NoRemoteImages</c>, <c>NoLowLabelImages</c>, <c>PreferSystem32Images</c>, in that order.
/// <c>"111"</c> is everything (the default), <c>"101"</c> keeps the network-DLL block and System32
/// preference but drops the low-integrity block, <c>"000"</c> applies none of them.
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

    public MitigationMask(bool noRemoteImages, bool noLowLabelImages, bool preferSystem32Images)
    {
        NoRemoteImages = noRemoteImages;
        NoLowLabelImages = noLowLabelImages;
        PreferSystem32Images = preferSystem32Images;
    }

    /// <summary>Block DLL loads from UNC paths and mapped network drives. The one with build-visible
    /// consequences: a build step that runs tooling from a share fails under the service.</summary>
    public bool NoRemoteImages { get; }

    /// <summary>Block DLLs written by low-integrity processes.</summary>
    public bool NoLowLabelImages { get; }

    /// <summary>Resolve known system DLLs from <c>%WinDir%\System32</c> first.</summary>
    public bool PreferSystem32Images { get; }

    /// <summary>All three applied — the default, and what the installer sends for "Full".</summary>
    public static MitigationMask All => new(true, true, true);

    /// <summary>The flag word passed to <c>SetProcessMitigationPolicy</c>.</summary>
    public uint ImageLoadFlags =>
        (NoRemoteImages ? NoRemoteImagesFlag : 0)
        | (NoLowLabelImages ? NoLowLabelImagesFlag : 0)
        | (PreferSystem32Images ? PreferSystem32ImagesFlag : 0);

    /// <summary>
    /// Parses the three-digit form. Exact length, digits <c>0</c> and <c>1</c> only, so a value that has
    /// drifted to something else — a stale enum name from an older build, a truncated string, an operator's
    /// "true" — is rejected rather than silently read as a weaker mask.
    /// </summary>
    public static bool TryParse(string? value, out MitigationMask mask)
    {
        mask = default;
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

    /// <summary>The three-digit form, which is exactly what is stored in appsettings.json.</summary>
    public override string ToString() =>
        $"{Digit(NoRemoteImages)}{Digit(NoLowLabelImages)}{Digit(PreferSystem32Images)}";

    private static char Digit(bool set) => set ? '1' : '0';

    public bool Equals(MitigationMask other) =>
        NoRemoteImages == other.NoRemoteImages
        && NoLowLabelImages == other.NoLowLabelImages
        && PreferSystem32Images == other.PreferSystem32Images;

    public override bool Equals(object? obj) => obj is MitigationMask other && Equals(other);

    public override int GetHashCode() => (int)ImageLoadFlags;

    public static bool operator ==(MitigationMask left, MitigationMask right) => left.Equals(right);

    public static bool operator !=(MitigationMask left, MitigationMask right) => !left.Equals(right);
}
