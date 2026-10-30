namespace SailTool.Enums;

/// <summary>
/// Conversion between <see cref="AdbMode"/> and the lowercase wire text that
/// <c>fastboot devices</c> and <c>adb devices</c> print.
/// </summary>
internal static class AdbModeExtensions
{
    /// <summary>
    /// Returns the wire text for a mode, which is also what the UI has always
    /// shown inside the square brackets of a device entry.
    /// </summary>
    internal static string ToDisplayText(this AdbMode mode) => mode switch
    {
        AdbMode.Device       => "device",
        AdbMode.Recovery     => "recovery",
        AdbMode.Sideload     => "sideload",
        AdbMode.Unauthorized => "unauthorized",
        AdbMode.Offline      => "offline",
        AdbMode.Bootloader   => "bootloader",
        AdbMode.Fastboot     => "fastboot",
        AdbMode.FastbootUsb  => "fastboot_usb",
        _                    => mode.ToString().ToLowerInvariant()
    };

    /// <summary>
    /// Parses a wire text into a mode. Comparison is case-insensitive because
    /// adb prints lowercase while some bootloaders vary.
    /// </summary>
    /// <returns>The matching member, or null for anything the parsers do not accept.</returns>
    internal static AdbMode? FromWireText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        return raw.Trim().ToLowerInvariant() switch
        {
            "device"       => AdbMode.Device,
            "recovery"     => AdbMode.Recovery,
            "sideload"     => AdbMode.Sideload,
            "unauthorized" => AdbMode.Unauthorized,
            "offline"      => AdbMode.Offline,
            "bootloader"   => AdbMode.Bootloader,
            "fastboot"     => AdbMode.Fastboot,
            "fastboot_usb" => AdbMode.FastbootUsb,
            _              => null
        };
    }
}
