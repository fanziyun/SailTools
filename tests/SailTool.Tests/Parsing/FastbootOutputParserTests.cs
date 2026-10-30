using SailTool.Enums;
using SailTool.Services.Parsing;
using Xunit;

namespace SailTool.Tests.Parsing;

/// <summary>
/// Covers <see cref="FastbootOutputParser"/>: the line-oriented parse of what
/// <c>fastboot</c> and <c>adb</c> actually print.
/// </summary>
/// <remarks>
/// Every sample below is copied from real tool output rather than invented, so a
/// regression here means the app would stop seeing a device that is plugged in.
/// </remarks>
public class FastbootOutputParserTests
{
    // ══════════════════════════════════════════════════════════════
    //  fastboot devices
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void ParseFastbootDevices_single_device()
    {
        var devices = FastbootOutputParser.ParseFastbootDevices("1a2b3c4d\tfastboot\n");

        var device = Assert.Single(devices);
        Assert.Equal("1a2b3c4d", device.Serial);
        Assert.Equal(AdbMode.Fastboot, device.Mode);
        Assert.Equal(ConnectionType.Fastboot, device.Connection);
    }

    [Fact]
    public void ParseFastbootDevices_usb3_variant_maps_to_its_own_member()
    {
        var devices = FastbootOutputParser.ParseFastbootDevices("abc123   fastboot_usb\n");

        var device = Assert.Single(devices);
        Assert.Equal(AdbMode.FastbootUsb, device.Mode);
        // Still a fastboot connection, and still routed to fastboot.exe.
        Assert.True(device.IsFastboot);
    }

    [Fact]
    public void ParseFastbootDevices_serial_may_contain_dash_and_underscore()
    {
        var devices = FastbootOutputParser.ParseFastbootDevices(
            "R58M12-3_ABC\tfastboot\n0123456789ABCDEF\tfastboot\n");

        Assert.Equal(2, devices.Count);
        Assert.Equal("R58M12-3_ABC", devices[0].Serial);
        Assert.Equal("0123456789ABCDEF", devices[1].Serial);
    }

    [Fact]
    public void ParseFastbootDevices_skips_diagnostics_interleaved_on_the_same_stream()
    {
        // fastboot writes warnings to stderr, which the service merges into one
        // buffer; those lines must not become phantom devices.
        var output = """
            < waiting for any device >
            fastboot: warning: skipping 0-byte partition
            1a2b3c4d	fastboot
            OKAY [  0.002s]

            """;

        var devices = FastbootOutputParser.ParseFastbootDevices(output);

        var device = Assert.Single(devices);
        Assert.Equal("1a2b3c4d", device.Serial);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n\n")]
    [InlineData("< waiting for any device >\n")]
    public void ParseFastbootDevices_no_device_yields_empty_list(string output)
    {
        Assert.Empty(FastbootOutputParser.ParseFastbootDevices(output));
    }

    // ══════════════════════════════════════════════════════════════
    //  adb devices
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void ParseAdbDevices_skips_the_attached_header()
    {
        var output = "List of devices attached\n1a2b3c4d\tdevice\n";

        var devices = FastbootOutputParser.ParseAdbDevices(output);

        var device = Assert.Single(devices);
        Assert.Equal("1a2b3c4d", device.Serial);
        Assert.Equal(AdbMode.Device, device.Mode);
        Assert.Equal(ConnectionType.ADB, device.Connection);
    }

    [Fact]
    public void ParseAdbDevices_also_accepts_output_with_no_header()
    {
        // adb only prints the header on some paths; the first line may be a device.
        var devices = FastbootOutputParser.ParseAdbDevices("1a2b3c4d\tdevice\n");

        var device = Assert.Single(devices);
        Assert.Equal("1a2b3c4d", device.Serial);
    }

    [Theory]
    [InlineData("device", AdbMode.Device)]
    [InlineData("recovery", AdbMode.Recovery)]
    [InlineData("sideload", AdbMode.Sideload)]
    [InlineData("unauthorized", AdbMode.Unauthorized)]
    [InlineData("offline", AdbMode.Offline)]
    [InlineData("bootloader", AdbMode.Bootloader)]
    public void ParseAdbDevices_recognises_every_state_adb_reports(string wire, AdbMode expected)
    {
        var devices = FastbootOutputParser.ParseAdbDevices(
            $"List of devices attached\n1a2b3c4d\t{wire}\n");

        var device = Assert.Single(devices);
        Assert.Equal(expected, device.Mode);
    }

    [Fact]
    public void ParseAdbDevices_keeps_several_usb_devices_in_order()
    {
        var output = """
            List of devices attached
            1a2b3c4d	device
            5e6f7a8b	unauthorized
            R58M12-3_ABC	offline

            """;

        var devices = FastbootOutputParser.ParseAdbDevices(output);

        Assert.Equal(3, devices.Count);
        Assert.Equal(AdbMode.Device, devices[0].Mode);
        Assert.Equal(AdbMode.Unauthorized, devices[1].Mode);
        Assert.Equal(AdbMode.Offline, devices[2].Mode);
        Assert.Equal("R58M12-3_ABC", devices[2].Serial);
    }

    [Fact]
    public void ParseAdbDevices_drops_a_network_serial()
    {
        // KNOWN DEFECT, pre-existing and preserved by the refactor.
        //
        // The serial group is [A-Za-z0-9_-]+, which has no dot and no colon, so a
        // device connected over adb tcpip is not recognised and never reaches the
        // device list. Real output is "192.168.1.50:5555\tdevice"; the regex
        // anchors on ^ and the serial class stops at the first dot.
        //
        // This test pins the current behaviour so the gap is visible rather than
        // silent. Widening the character class changes what the app can see, and
        // needs approval.
        var output = """
            List of devices attached
            1a2b3c4d	device
            192.168.1.50:5555	device

            """;

        var devices = FastbootOutputParser.ParseAdbDevices(output);

        var device = Assert.Single(devices);
        Assert.Equal("1a2b3c4d", device.Serial);
    }

    [Fact]
    public void ParseAdbDevices_drops_the_emulator_serial_form_too()
    {
        // Same character-class gap: "emulator-5554" is fine, but the port form a
        // remote adb server reports is not.
        var output = "List of devices attached\nemulator-5554\tdevice\n";

        var devices = FastbootOutputParser.ParseAdbDevices(output);

        var device = Assert.Single(devices);
        Assert.Equal("emulator-5554", device.Serial);
    }

    [Fact]
    public void ParseAdbDevices_drops_the_permission_hint_adb_prints_as_a_daemon()
    {
        // Real output when the user never authorised the key; the trailing lines
        // are advice, not devices.
        var output = """
            List of devices attached
            1a2b3c4d	unauthorized

            """;

        var devices = FastbootOutputParser.ParseAdbDevices(output);

        Assert.Single(devices);
    }

    [Fact]
    public void ParseAdbDevices_empty_output_yields_empty_list()
    {
        Assert.Empty(FastbootOutputParser.ParseAdbDevices("List of devices attached\n\n"));
    }

    // ══════════════════════════════════════════════════════════════
    //  fastboot getvar all
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void ParseGetVarAll_strips_the_bootloader_prefix()
    {
        var output = """
            (bootloader) version: 0.5
            (bootloader) product: shennong
            (bootloader) unlocked: yes
            (bootloader) current-slot: a

            """;

        var vars = FastbootOutputParser.ParseGetVarAll(output);

        Assert.Equal("shennong", vars["product"]);
        Assert.Equal("yes", vars["unlocked"]);
        Assert.Equal("a", vars["current-slot"]);
    }

    [Fact]
    public void ParseGetVarAll_also_accepts_bare_key_value_lines()
    {
        var vars = FastbootOutputParser.ParseGetVarAll("kernel: 5.15.0\nbrand: Xiaomi\n");

        Assert.Equal("5.15.0", vars["kernel"]);
        Assert.Equal("Xiaomi", vars["brand"]);
    }

    [Fact]
    public void ParseGetVarAll_lookup_is_case_insensitive()
    {
        var vars = FastbootOutputParser.ParseGetVarAll("(bootloader) PRODUCT: aurora\n");

        Assert.Equal("aurora", vars["product"]);
        Assert.Equal("aurora", vars["PRODUCT"]);
    }

    [Fact]
    public void ParseGetVarAll_keeps_a_value_that_itself_contains_colons()
    {
        // Only the first colon splits; the rest belongs to the value.
        var vars = FastbootOutputParser.ParseGetVarAll(
            "(bootloader) build-id: TP1A.220905.001:eng:userdebug\n");

        Assert.Equal("TP1A.220905.001:eng:userdebug", vars["build-id"]);
    }

    [Fact]
    public void ParseGetVarAll_skips_status_lines_that_are_not_variables()
    {
        var output = """
            (bootloader) product: shennong
            : orphan value
            no-colon-here

            """;

        var vars = FastbootOutputParser.ParseGetVarAll(output);

        Assert.Single(vars);
        Assert.Equal("shennong", vars["product"]);
    }

    [Fact]
    public void ParseGetVarAll_preserves_an_empty_value_so_callers_can_tell_present_from_absent()
    {
        var vars = FastbootOutputParser.ParseGetVarAll("(bootloader) kernel:\n(bootloader) brand: Xiaomi\n");

        Assert.True(vars.ContainsKey("kernel"));
        Assert.Equal("", vars["kernel"]);
        Assert.False(vars.ContainsKey("missing"));
    }

    // ══════════════════════════════════════════════════════════════
    //  the batched adb shell getprop probe
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void ParseGetPropBatch_reads_the_batched_echo_output()
    {
        // This is what the app's single-shot shell command produces.
        var output = """
            DEVICE:shennong
            BRAND:Xiaomi
            MODEL:24129PN74C
            SLOT:_a
            BL:orange
            ANDROID:16
            SYSTEM:OS3.0.313.0.WPBCNXM
            KERNEL:6.6.60-android14-11-g9d0d4ba7c9f1-ab13833012
            HYPEROS_NAME:OS3.0
            HYPEROS_INCR:OS3.0.313.0.WPBCNXM
            MIUI_NAME:

            """;

        var dict = FastbootOutputParser.ParseGetPropBatch(output);

        Assert.Equal("shennong", dict["DEVICE"]);
        Assert.Equal("Xiaomi", dict["BRAND"]);
        Assert.Equal("_a", dict["SLOT"]);
        Assert.Equal("orange", dict["BL"]);
        Assert.Equal("16", dict["ANDROID"]);
    }

    [Fact]
    public void ParseGetPropBatch_drops_props_the_device_did_not_set()
    {
        // A device that is not running MIUI echoes an empty value; dropping it is
        // what lets FormatSystemVersion distinguish "no MIUI" from "MIUI named ''".
        var dict = FastbootOutputParser.ParseGetPropBatch("DEVICE:aurora\nMIUI_NAME:\nHYPEROS_NAME:\n");

        Assert.Equal("aurora", dict["DEVICE"]);
        Assert.False(dict.ContainsKey("MIUI_NAME"));
        Assert.False(dict.ContainsKey("HYPEROS_NAME"));
    }

    [Fact]
    public void ParseGetPropBatch_keeps_a_kernel_version_containing_dashes_and_dots()
    {
        var dict = FastbootOutputParser.ParseGetPropBatch(
            "KERNEL:5.15.41-android13-8-00003-g4a5a6b7c8d9e-ab1234567\n");

        Assert.Equal(
            "5.15.41-android13-8-00003-g4a5a6b7c8d9e-ab1234567",
            dict["KERNEL"]);
    }

    [Fact]
    public void ParseGetPropBatch_empty_output_yields_empty_dictionary()
    {
        Assert.Empty(FastbootOutputParser.ParseGetPropBatch(""));
    }
}
