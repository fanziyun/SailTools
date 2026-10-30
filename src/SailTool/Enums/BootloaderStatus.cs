namespace SailTool.Enums;

/// <summary>
/// Bootloader lock state, normalised from the two different ways it is reported:
/// <c>ro.boot.verifiedbootstate</c> over adb, and <c>fastboot getvar unlocked</c>
/// over fastboot.
/// </summary>
/// <remarks>
/// Member names carry no display text. The Chinese labels the UI shows come from
/// <see cref="BootloaderStatusExtensions.ToDisplayText"/>, which is the single
/// place they are defined.
/// </remarks>
public enum BootloaderStatus
{
    /// <summary>Unlocked: <c>orange</c>, or <c>unlocked: yes</c>.</summary>
    Unlocked,

    /// <summary>Stock locked: <c>green</c>, or <c>unlocked: no</c>.</summary>
    Locked,

    /// <summary>Locked with a custom key: <c>yellow</c>.</summary>
    CustomKey,

    /// <summary>Locked, self-signed / verified: <c>red</c>.</summary>
    Verified,

    /// <summary>The device did not report a recognisable state.</summary>
    Unknown
}
