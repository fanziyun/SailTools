using SailTool.Enums;

namespace SailTool.Models;

/// <summary>
/// Represents a device connected via adb or fastboot.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Mode"/> and <see cref="Connection"/> are enums rather than the
/// strings the tools print, so callers compare against named members instead of
/// literals like <c>"unauthorized"</c>. The set is closed — the parsers in
/// <see cref="SailTool.Services.Parsing.FastbootOutputParser"/> only accept the
/// values listed there, so an unparseable line never produces a DeviceInfo.
/// </para>
/// <para>
/// Every display path reproduces the old strings exactly:
/// <see cref="ConnectionDisplay"/> yields "Fastboot" / "ADB", and
/// <see cref="ModeDisplay"/> yields the lowercase wire text such as "device".
/// </para>
/// </remarks>
/// <param name="Serial">Device serial as reported by the tool.</param>
/// <param name="Mode">Reported mode.</param>
/// <param name="Connection">Which host tool reported it.</param>
public sealed record DeviceInfo(string Serial, AdbMode Mode, ConnectionType Connection)
{
    /// <summary>Mode as the wire text, e.g. <c>device</c>, <c>fastboot_usb</c>.</summary>
    public string ModeDisplay => Mode.ToDisplayText();

    /// <summary>Connection as displayed, i.e. <c>Fastboot</c> or <c>ADB</c>.</summary>
    public string ConnectionDisplay => Connection.ToString();

    public string DisplayName => $"{Serial}  [{ModeDisplay}] ({ConnectionDisplay})";

    /// <summary>
    /// Whether the device is reachable through fastboot. Covers both the
    /// <c>fastboot</c> and <c>fastboot_usb</c> bootloader reports.
    /// </summary>
    public bool IsFastboot =>
        Connection == ConnectionType.Fastboot ||
        Mode is AdbMode.Fastboot or AdbMode.FastbootUsb;

    /// <summary>
    /// Whether the device is booted into Android and authorised, i.e. usable
    /// through adb. A device in recovery or sideload is adb-visible but not here,
    /// which matters for routing reboot commands.
    /// </summary>
    public bool IsAdb =>
        Connection == ConnectionType.ADB && Mode == AdbMode.Device;

    public override string ToString() => DisplayName;
}
