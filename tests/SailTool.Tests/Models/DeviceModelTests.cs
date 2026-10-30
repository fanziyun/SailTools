using SailTool.Enums;
using SailTool.Models;
using Xunit;

namespace SailTool.Tests.Models;

/// <summary>
/// Covers <see cref="DeviceInfo"/> and <see cref="DetailedDeviceInfo"/>: the two
/// records whose display members the UI binds.
/// </summary>
public class DeviceModelTests
{
    // ══════════════════════════════════════════════════════════════
    //  DeviceInfo
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void DisplayName_reproduces_the_old_string_with_the_lowercase_mode()
    {
        // Was $"{Serial}  [{Mode}] ({ConnectionType})" with Mode a wire string;
        // the enum must render identically or the device list text changes.
        var device = new DeviceInfo("1a2b3c4d", AdbMode.Device, ConnectionType.ADB);

        Assert.Equal("1a2b3c4d  [device] (ADB)", device.DisplayName);
    }

    [Fact]
    public void DisplayName_for_a_usb3_bootloader_keeps_the_wire_spelling()
    {
        var device = new DeviceInfo("abc123", AdbMode.FastbootUsb, ConnectionType.Fastboot);

        Assert.Equal("abc123  [fastboot_usb] (Fastboot)", device.DisplayName);
    }

    [Fact]
    public void ConnectionDisplay_is_what_the_enum_prints()
    {
        Assert.Equal("Fastboot", new DeviceInfo("s", AdbMode.Fastboot, ConnectionType.Fastboot).ConnectionDisplay);
        Assert.Equal("ADB", new DeviceInfo("s", AdbMode.Device, ConnectionType.ADB).ConnectionDisplay);
    }

    [Theory]
    [InlineData(ConnectionType.Fastboot, AdbMode.Fastboot, true, false)]
    [InlineData(ConnectionType.Fastboot, AdbMode.FastbootUsb, true, false)]
    [InlineData(ConnectionType.ADB, AdbMode.Device, false, true)]
    [InlineData(ConnectionType.ADB, AdbMode.Recovery, false, false)]
    [InlineData(ConnectionType.ADB, AdbMode.Sideload, false, false)]
    [InlineData(ConnectionType.ADB, AdbMode.Unauthorized, false, false)]
    [InlineData(ConnectionType.ADB, AdbMode.Offline, false, false)]
    [InlineData(ConnectionType.ADB, AdbMode.Bootloader, false, false)]
    public void IsFastboot_and_IsAdb_route_commands_correctly(
        ConnectionType connection, AdbMode mode, bool expectFastboot, bool expectAdb)
    {
        // These two predicates decide whether a reboot is sent to fastboot.exe or
        // adb.exe, so every mode has to land on exactly one side, or neither.
        var device = new DeviceInfo("1a2b3c4d", mode, connection);

        Assert.Equal(expectFastboot, device.IsFastboot);
        Assert.Equal(expectAdb, device.IsAdb);
    }

    [Fact]
    public void IsFastboot_and_IsAdb_are_never_both_true()
    {
        foreach (var connection in Enum.GetValues<ConnectionType>())
        foreach (var mode in Enum.GetValues<AdbMode>())
        {
            var device = new DeviceInfo("s", mode, connection);
            Assert.False(device.IsFastboot && device.IsAdb,
                $"{connection}/{mode} claimed both connections");
        }
    }

    [Fact]
    public void Records_with_the_same_values_are_equal()
    {
        var a = new DeviceInfo("1a2b3c4d", AdbMode.Device, ConnectionType.ADB);
        var b = new DeviceInfo("1a2b3c4d", AdbMode.Device, ConnectionType.ADB);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Records_differing_in_mode_alone_are_not_equal()
    {
        // HomePageViewModel relies on this to decide whether to reassign the
        // selection when a device moves from device to recovery.
        var a = new DeviceInfo("1a2b3c4d", AdbMode.Device, ConnectionType.ADB);
        var b = new DeviceInfo("1a2b3c4d", AdbMode.Recovery, ConnectionType.ADB);

        Assert.NotEqual(a, b);
    }

    // ══════════════════════════════════════════════════════════════
    //  DetailedDeviceInfo
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Empty_fills_every_display_field_with_the_placeholder()
    {
        var info = DetailedDeviceInfo.Empty("1a2b3c4d", "Fastboot");

        Assert.Equal("1a2b3c4d", info.Serial);
        Assert.Equal("Fastboot", info.DetectionType);
        Assert.Equal("--", info.DeviceCodename);
        Assert.Equal("--", info.Brand);
        Assert.Equal("--", info.Model);
        Assert.Equal("--", info.Slot);
        Assert.Equal("--", info.AndroidVersion);
        Assert.Equal("--", info.SystemVersion);
        Assert.Equal("--", info.KernelVersion);
    }

    [Fact]
    public void Empty_reports_an_unknown_bootloader_rather_than_a_fabricated_state()
    {
        var info = DetailedDeviceInfo.Empty("1a2b3c4d", "Fastboot");

        Assert.Equal(BootloaderStatus.Unknown, info.Bootloader);
        Assert.Equal("--", info.BootloaderDisplay);
    }

    [Theory]
    [InlineData(BootloaderStatus.Unlocked, "已解锁")]
    [InlineData(BootloaderStatus.Locked, "未解锁")]
    [InlineData(BootloaderStatus.CustomKey, "未解锁 (自定义密钥)")]
    [InlineData(BootloaderStatus.Verified, "未解锁 (已验证)")]
    [InlineData(BootloaderStatus.Unknown, "--")]
    public void BootloaderDisplay_is_the_pinned_Chinese_label(
        BootloaderStatus status, string expected)
    {
        var info = DetailedDeviceInfo.Empty("s", "Fastboot") with { Bootloader = status };

        Assert.Equal(expected, info.BootloaderDisplay);
    }

    [Fact]
    public void DisplayName_uses_the_detection_type_not_a_connection_name()
    {
        var info = DetailedDeviceInfo.Empty("1a2b3c4d", "Recovery");

        Assert.Equal("1a2b3c4d [--] (Recovery)", info.DisplayName);
    }

    [Fact]
    public void With_expression_changes_only_the_named_field()
    {
        var info = DetailedDeviceInfo.Empty("1a2b3c4d", "ADB");
        var updated = info with { SystemVersion = "OS3.0.313.0.WPBCNXM" };

        Assert.Equal("OS3.0.313.0.WPBCNXM", updated.SystemVersion);
        Assert.Equal("ADB", updated.DetectionType);
        Assert.Equal("--", updated.KernelVersion);
    }
}