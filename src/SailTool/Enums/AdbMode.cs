namespace SailTool.Enums;

/// <summary>
/// The mode a device reports, as printed by <c>fastboot devices</c> and
/// <c>adb devices</c>.
/// </summary>
/// <remarks>
/// The set is closed: <see cref="Services.Parsing.FastbootOutputParser"/> only
/// accepts <c>fastboot</c>, <c>fastboot_usb</c>, <c>device</c>, <c>recovery</c>,
/// <c>bootloader</c>, <c>sideload</c>, <c>unauthorized</c> and <c>offline</c>, so
/// every parseable line maps onto a member here. Use
/// <see cref="AdbModeExtensions.ToDisplayText"/> to get the lowercase wire form
/// back — that is what the UI shows, not the member name.
/// </remarks>
public enum AdbMode
{
    /// <summary>adb: booted into Android with debugging authorised.</summary>
    Device,

    /// <summary>adb: in recovery.</summary>
    Recovery,

    /// <summary>adb: sideloading an update package.</summary>
    Sideload,

    /// <summary>adb: connected but the RSA prompt was never accepted.</summary>
    Unauthorized,

    /// <summary>adb: the device stopped answering.</summary>
    Offline,

    /// <summary>adb: bootloader, reached without fastboot.exe noticing.</summary>
    Bootloader,

    /// <summary>fastboot: bootloader mode.</summary>
    Fastboot,

    /// <summary>fastboot: bootloader mode over a USB3 port.</summary>
    FastbootUsb
}
