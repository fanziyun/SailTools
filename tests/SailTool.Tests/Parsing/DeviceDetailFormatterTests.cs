using SailTool.Enums;
using SailTool.Services.Parsing;
using Xunit;

namespace SailTool.Tests.Parsing;

/// <summary>
/// Covers <see cref="DeviceDetailFormatter"/>: the normalisation of raw tool
/// values into what the detail grid renders.
/// </summary>
/// <remarks>
/// Several assertions pin the exact Chinese label. Those strings are what the
/// user reads, so a change there is a UI change and should fail a test.
/// </remarks>
public class DeviceDetailFormatterTests
{
    // ══════════════════════════════════════════════════════════════
    //  slot
    // ══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("_a", "A")]
    [InlineData("_b", "B")]
    [InlineData("a", "A")]
    [InlineData("B", "B")]
    public void FormatSlot_normalises_the_ab_suffix(string raw, string expected)
    {
        Assert.Equal(expected, DeviceDetailFormatter.FormatSlot(raw));
    }

    [Fact]
    public void FormatSlot_does_not_trim_padding_itself()
    {
        // Callers pass values through GetOrEmpty, which already trims, so this
        // documents where the responsibility sits rather than testing a path that
        // cannot occur. Padded input falls through to the upper-casing default.
        Assert.Equal(" _A ", DeviceDetailFormatter.FormatSlot(" _a "));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("--")]
    public void FormatSlot_missing_value_becomes_the_placeholder(string raw)
    {
        Assert.Equal(DeviceDetailFormatter.Dash, DeviceDetailFormatter.FormatSlot(raw));
    }

    [Fact]
    public void FormatSlot_upper_cases_an_unexpected_value_rather_than_discarding_it()
    {
        // A device reporting a slot outside the A/B scheme keeps its raw text
        // upper-cased: showing it beats showing the placeholder, which would read
        // as "not detected". The underscore is part of that raw text.
        Assert.Equal("_X", DeviceDetailFormatter.FormatSlot("_x"));
        Assert.Equal("SLOT_C", DeviceDetailFormatter.FormatSlot("slot_c"));
    }

    // ══════════════════════════════════════════════════════════════
    //  bootloader state
    // ══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("green", BootloaderStatus.Locked, "未解锁")]
    [InlineData("orange", BootloaderStatus.Unlocked, "已解锁")]
    [InlineData("yellow", BootloaderStatus.CustomKey, "未解锁 (自定义密钥)")]
    [InlineData("red", BootloaderStatus.Verified, "未解锁 (已验证)")]
    public void ParseBlStatus_maps_verified_boot_states(string raw, BootloaderStatus status, string label)
    {
        Assert.Equal(status, DeviceDetailFormatter.ParseBlStatus(raw));
        Assert.Equal(label, status.ToDisplayText());
    }

    [Theory]
    [InlineData("GREEN")]
    [InlineData("Orange")]
    [InlineData("  red  ")]
    public void ParseBlStatus_ignores_case_and_padding(string raw)
    {
        Assert.NotEqual(BootloaderStatus.Unknown, DeviceDetailFormatter.ParseBlStatus(raw));
    }

    [Theory]
    [InlineData("")]
    [InlineData("--")]
    [InlineData("purple")]
    [InlineData(null)]
    public void ParseBlStatus_unrecognised_value_is_unknown(string? raw)
    {
        Assert.Equal(BootloaderStatus.Unknown, DeviceDetailFormatter.ParseBlStatus(raw!));
        Assert.Equal(DeviceDetailFormatter.Dash, BootloaderStatus.Unknown.ToDisplayText());
    }

    [Theory]
    [InlineData(true, "yes", BootloaderStatus.Unlocked)]
    [InlineData(true, "YES", BootloaderStatus.Unlocked)]
    [InlineData(true, "no", BootloaderStatus.Locked)]
    [InlineData(true, "", BootloaderStatus.Locked)]
    [InlineData(false, "yes", BootloaderStatus.Unknown)]
    public void ParseFastbootUnlocked_distinguishes_absent_from_present(bool hasVar, string? value, BootloaderStatus expected)
    {
        // getvar all omits "unlocked" entirely on devices that cannot report it;
        // that must not read as locked.
        Assert.Equal(expected, DeviceDetailFormatter.ParseFastbootUnlocked(hasVar, value));
    }

    [Fact]
    public void Both_bootloader_paths_agree_on_the_same_labels()
    {
        // The two producers feed one display table now. Before the enum they each
        // hardcoded the strings, and a mismatch would have been invisible.
        Assert.Equal(
            DeviceDetailFormatter.ParseBlStatus("orange").ToDisplayText(),
            DeviceDetailFormatter.ParseFastbootUnlocked(true, "yes").ToDisplayText());
        Assert.Equal(
            DeviceDetailFormatter.ParseBlStatus("green").ToDisplayText(),
            DeviceDetailFormatter.ParseFastbootUnlocked(true, "no").ToDisplayText());
    }

    // ══════════════════════════════════════════════════════════════
    //  system version
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void FormatSystemVersion_hyperos_shows_the_incremental_verbatim()
    {
        var result = DeviceDetailFormatter.FormatSystemVersion(
            brand: "Xiaomi",
            hyperName: "OS3.0",
            hyperIncr: "OS3.0.313.0.WPBCNXM",
            miuiName: "",
            displayId: "OS3.0.313.0.WPBCNXM");

        Assert.Equal("OS3.0.313.0.WPBCNXM", result);
    }

    [Fact]
    public void FormatSystemVersion_hyperos_wins_over_a_legacy_miui_name_on_the_same_device()
    {
        // HyperOS devices still carry ro.miui.ui.version.name; the newer name is
        // the one worth showing.
        var result = DeviceDetailFormatter.FormatSystemVersion(
            brand: "Xiaomi",
            hyperName: "OS2.0",
            hyperIncr: "OS2.0.5.0.VNACNXM",
            miuiName: "V816.0",
            displayId: "ignored");

        Assert.Equal("OS2.0.5.0.VNACNXM", result);
    }

    [Fact]
    public void FormatSystemVersion_falls_back_to_miui_when_there_is_no_hyperos_name()
    {
        var result = DeviceDetailFormatter.FormatSystemVersion(
            brand: "Xiaomi", hyperName: "", hyperIncr: "",
            miuiName: "V14", displayId: "TKQ1.220905.001");

        Assert.Equal("MIUI V14", result);
    }

    [Fact]
    public void FormatSystemVersion_hyperos_requires_a_non_empty_incremental()
    {
        // A device that sets the name but not the incremental must not render blank.
        var result = DeviceDetailFormatter.FormatSystemVersion(
            brand: "Xiaomi", hyperName: "OS3.0", hyperIncr: "",
            miuiName: "", displayId: "OS3.0.313.0.WPBCNXM");

        Assert.Equal("OS3.0.313.0.WPBCNXM", result);
    }

    [Theory]
    [InlineData("OPPO", "PLK110_16.0.7.206(CN01)", "16.0.7.206(CN01)")]
    [InlineData("OnePlus", "CPH2449_14.0.0.400(EX01)", "14.0.0.400(EX01)")]
    [InlineData("realme", "RMX3700_14.0.0.500(EX01)", "14.0.0.500(EX01)")]
    [InlineData("iQOO", "V2241A_15.0.9.1(CN01)", "15.0.9.1(CN01)")]
    public void FormatSystemVersion_coloros_family_strips_the_model_prefix(
        string brand, string displayId, string expected)
    {
        var result = DeviceDetailFormatter.FormatSystemVersion(
            brand: brand, hyperName: "", hyperIncr: "", miuiName: "", displayId: displayId);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("samsung")]
    [InlineData("google")]
    [InlineData("HUAWEI")]
    [InlineData("")]
    public void FormatSystemVersion_other_brands_keep_the_display_id(string brand)
    {
        var result = DeviceDetailFormatter.FormatSystemVersion(
            brand: brand, hyperName: "", hyperIncr: "", miuiName: "",
            displayId: "TP1A.220905.001");

        Assert.Equal("TP1A.220905.001", result);
    }

    [Fact]
    public void FormatSystemVersion_nothing_readable_becomes_the_placeholder()
    {
        var result = DeviceDetailFormatter.FormatSystemVersion("", "", "", "", "");

        Assert.Equal(DeviceDetailFormatter.Dash, result);
    }

    [Theory]
    [InlineData("PLK110_16.0.7.206(CN01)", "16.0.7.206(CN01)")]
    [InlineData("CPH2449_14.0.0.400(EX01)", "14.0.0.400(EX01)")]
    [InlineData("PLK110_", "PLK110_")]
    [InlineData("_16.0", "_16.0")]
    [InlineData("plain", "plain")]
    [InlineData("", "")]
    public void StripColorOsModelPrefix_only_strips_an_alphanumeric_prefix(
        string input, string expected)
    {
        Assert.Equal(expected, DeviceDetailFormatter.StripColorOsModelPrefix(input));
    }

    [Fact]
    public void StripColorOsModelPrefix_strips_any_alphanumeric_prefix_not_just_model_codes()
    {
        // The rule is a character test, not a model-code test: "no" is letters, so
        // it goes. Documenting the actual rule rather than the intent, because a
        // real ColorOS display id is only ever passed here after IsColorOsBrand has
        // already said the brand ships that format.
        Assert.Equal("underscore_but_dots",
            DeviceDetailFormatter.StripColorOsModelPrefix("no_underscore_but_dots"));
    }

    [Theory]
    [InlineData("PL-K110_16.0.7.206(CN01)")]
    [InlineData("1.0_2.0")]
    [InlineData("RMX 3700_14.0")]
    public void StripColorOsModelPrefix_leaves_a_punctuated_prefix_alone(string input)
    {
        // A prefix containing punctuation is not a model code, so an ordinary
        // version string that happens to contain an underscore survives intact.
        Assert.Equal(input, DeviceDetailFormatter.StripColorOsModelPrefix(input));
    }

    [Fact]
    public void IsColorOsBrand_covers_the_four_shippers()
    {
        Assert.True(DeviceDetailFormatter.IsColorOsBrand("oppo"));
        Assert.True(DeviceDetailFormatter.IsColorOsBrand("OnePlus"));
        Assert.True(DeviceDetailFormatter.IsColorOsBrand("realme"));
        Assert.True(DeviceDetailFormatter.IsColorOsBrand("iQOO"));
        Assert.False(DeviceDetailFormatter.IsColorOsBrand("xiaomi"));
        Assert.False(DeviceDetailFormatter.IsColorOsBrand("vivo"));
    }

    // ══════════════════════════════════════════════════════════════
    //  detection label
    // ══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(ConnectionType.Fastboot, AdbMode.Fastboot, "Fastboot")]
    [InlineData(ConnectionType.Fastboot, AdbMode.FastbootUsb, "Fastboot")]
    [InlineData(ConnectionType.ADB, AdbMode.Recovery, "Recovery")]
    [InlineData(ConnectionType.ADB, AdbMode.Sideload, "Recovery")]
    [InlineData(ConnectionType.ADB, AdbMode.Device, "系统")]
    [InlineData(ConnectionType.ADB, AdbMode.Unauthorized, "未授权")]
    [InlineData(ConnectionType.ADB, AdbMode.Offline, "离线")]
    [InlineData(ConnectionType.ADB, AdbMode.Bootloader, "Fastboot")]
    public void MapDetectionType_reproduces_the_labels_the_column_has_always_shown(
        ConnectionType connection, AdbMode mode, string expected)
    {
        Assert.Equal(expected, DeviceDetailFormatter.MapDetectionType(connection, mode));
    }

    [Fact]
    public void MapDetectionType_prefers_the_connection_over_the_mode()
    {
        // fastboot devices are always labelled Fastboot, whatever mode text came back.
        Assert.Equal("Fastboot",
            DeviceDetailFormatter.MapDetectionType(ConnectionType.Fastboot, AdbMode.Device));
    }

    // ══════════════════════════════════════════════════════════════
    //  dictionary accessors
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void GetOrEmpty_distinguishes_absent_from_empty_without_substituting_a_placeholder()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["present"] = "  value  ",
            ["empty"] = ""
        };

        Assert.Equal("value", DeviceDetailFormatter.GetOrEmpty(dict, "present"));
        Assert.Equal("", DeviceDetailFormatter.GetOrEmpty(dict, "empty"));
        Assert.Equal("", DeviceDetailFormatter.GetOrEmpty(dict, "absent"));
    }

    [Theory]
    [InlineData("", "--")]
    [InlineData("   ", "--")]
    [InlineData(null, "--")]
    [InlineData("shennong", "shennong")]
    public void DisplayOrDash_maps_blank_to_the_placeholder(string? input, string expected)
    {
        Assert.Equal(expected, DeviceDetailFormatter.DisplayOrDash(input!));
    }
}
