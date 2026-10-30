using SailTool.Enums;

namespace SailTool.Services.Parsing;

/// <summary>
/// Formats raw <c>fastboot getvar</c> / <c>adb getprop</c> values into the strings
/// shown on the device detail card.
/// </summary>
/// <remarks>
/// <para>
/// Pure functions over strings — no I/O, no WinUI types — so they are reachable
/// from the test project.
/// </para>
/// <para>
/// The returned values are user-visible and are Chinese by design; the UI has
/// always shown them that way. Only these XML docs are English.
/// </para>
/// </remarks>
internal static class DeviceDetailFormatter
{
    /// <summary>The placeholder shown when a field could not be read.</summary>
    internal const string Dash = "--";

    /// <summary>
    /// Reads a dictionary entry, returning an empty string when absent.
    /// Deliberately does NOT apply the <see cref="Dash"/> fallback: callers such as
    /// <see cref="FormatSystemVersion"/> need to distinguish "missing" from "present".
    /// </summary>
    internal static string GetOrEmpty(IDictionary<string, string> d, string key)
        => d.TryGetValue(key, out var v) ? v?.Trim() ?? "" : "";

    /// <summary>Maps an empty or whitespace value to the display placeholder.</summary>
    internal static string DisplayOrDash(string val)
        => string.IsNullOrWhiteSpace(val) ? Dash : val;

    /// <summary>
    /// Normalizes A/B slot suffixes: <c>"_a"</c> to <c>"A"</c>, <c>"_b"</c> to
    /// <c>"B"</c>, empty to the placeholder. Anything else is upper-cased as-is.
    /// </summary>
    internal static string FormatSlot(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == Dash) return Dash;
        return raw.TrimStart('_').ToUpperInvariant() switch
        {
            "A" => "A",
            "B" => "B",
            _   => raw.ToUpperInvariant()
        };
    }

    /// <summary>
    /// Normalizes the <c>ro.boot.verifiedbootstate</c> value adb reports into a
    /// <see cref="BootloaderStatus"/>. An empty or unrecognised value becomes
    /// <see cref="BootloaderStatus.Unknown"/>, which renders as the placeholder.
    /// </summary>
    /// <remarks>
    /// The colour semantics are Android Verified Boot's: green means the device
    /// verifies against the OEM key (locked), orange means it does not verify
    /// against it (unlocked), yellow means it verifies against a user-supplied
    /// key, and red means verification failed outright.
    /// </remarks>
    internal static BootloaderStatus ParseBlStatus(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == Dash) return BootloaderStatus.Unknown;
        return raw.Trim().ToLowerInvariant() switch
        {
            "green"  => BootloaderStatus.Locked,
            "orange" => BootloaderStatus.Unlocked,
            "yellow" => BootloaderStatus.CustomKey,
            "red"    => BootloaderStatus.Verified,
            _        => BootloaderStatus.Unknown
        };
    }

    /// <summary>
    /// Normalizes the <c>unlocked</c> variable from <c>fastboot getvar all</c>.
    /// </summary>
    /// <param name="hasVar">Whether the variable was present at all.</param>
    /// <param name="value">Its raw value when present.</param>
    internal static BootloaderStatus ParseFastbootUnlocked(bool hasVar, string? value)
    {
        if (!hasVar) return BootloaderStatus.Unknown;
        return value?.Equals("yes", StringComparison.OrdinalIgnoreCase) == true
            ? BootloaderStatus.Unlocked
            : BootloaderStatus.Locked;
    }

    /// <summary>
    /// Builds the user-facing system version string.
    /// </summary>
    /// <remarks>
    /// Priority order:
    /// <list type="number">
    ///   <item>Xiaomi HyperOS — show <c>ro.mi.os.version.incremental</c> verbatim,
    ///   e.g. <c>OS3.0.313.0.WPBCNXM</c>.</item>
    ///   <item>Xiaomi MIUI (legacy) — <c>"MIUI "</c> plus
    ///   <c>ro.miui.ui.version.name</c>, e.g. <c>MIUI V14</c>.</item>
    ///   <item>ColorOS family (OPPO / OnePlus / Realme / iQOO) — strip the model
    ///   prefix, leaving <c>16.0.7.206(CN01)</c>.</item>
    ///   <item>Any other brand — <c>ro.build.display.id</c> verbatim.</item>
    /// </list>
    /// </remarks>
    internal static string FormatSystemVersion(
        string brand,
        string hyperName,
        string hyperIncr,
        string miuiName,
        string displayId)
    {
        // ── Xiaomi HyperOS ────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(hyperName) &&
            hyperName.StartsWith("OS", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(hyperIncr))
        {
            return hyperIncr;
        }

        // ── Xiaomi MIUI legacy ────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(miuiName))
        {
            return $"MIUI {miuiName}";
        }

        // ── ColorOS family: OPPO / OnePlus / Realme ───────────────
        // displayId looks like "PLK110_16.0.7.206(CN01)"; the part the user
        // actually wants to read is the version after the model prefix.
        if (!string.IsNullOrWhiteSpace(brand) && IsColorOsBrand(brand) &&
            !string.IsNullOrWhiteSpace(displayId))
        {
            return StripColorOsModelPrefix(displayId);
        }

        // ── Other brands ──────────────────────────────────────────
        return string.IsNullOrWhiteSpace(displayId) ? Dash : displayId;
    }

    /// <summary>
    /// Whether a brand ships ColorOS-derived firmware and therefore formats its
    /// display id with a leading model code.
    /// </summary>
    internal static bool IsColorOsBrand(string brand)
    {
        return brand.Equals("oppo", StringComparison.OrdinalIgnoreCase)
            || brand.Equals("oneplus", StringComparison.OrdinalIgnoreCase)
            || brand.Equals("realme", StringComparison.OrdinalIgnoreCase)
            || brand.Equals("iqoo", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Strips the leading model-code prefix from a ColorOS display id.
    /// </summary>
    /// <remarks>
    /// Examples:
    /// <code>
    ///   "PLK110_16.0.7.206(CN01)"  -> "16.0.7.206(CN01)"
    ///   "CPH2449_14.0.0.400(EX01)" -> "14.0.0.400(EX01)"
    ///   "RMX3700_14.0.0.500(EX01)" -> "14.0.0.500(EX01)"
    /// </code>
    /// The prefix is only removed when it consists entirely of letters and digits,
    /// so an ordinary version string containing an underscore is left alone.
    /// </remarks>
    internal static string StripColorOsModelPrefix(string displayId)
    {
        var underscore = displayId.IndexOf('_');
        if (underscore > 0 && underscore < displayId.Length - 1)
        {
            var prefix = displayId[..underscore];
            if (prefix.Length > 0 && prefix.All(char.IsLetterOrDigit))
                return displayId[(underscore + 1)..];
        }
        return displayId;
    }

    /// <summary>
    /// Maps the (connection, mode) pair onto the short label the UI shows in its
    /// detection-method column.
    /// </summary>
    /// <remarks>
    /// Rules:
    /// <list type="bullet">
    ///   <item>Fastboot connection → <c>Fastboot</c></item>
    ///   <item>adb with <c>recovery</c> or <c>sideload</c> → <c>Recovery</c></item>
    ///   <item>adb with <c>device</c> → the localized "system" label</item>
    ///   <item>adb with <c>unauthorized</c> → the localized "not authorized" label</item>
    ///   <item>adb with <c>offline</c> → the localized "offline" label</item>
    ///   <item>adb with <c>bootloader</c> → <c>Fastboot</c></item>
    /// </list>
    /// </remarks>
    internal static string MapDetectionType(ConnectionType connection, AdbMode mode)
    {
        if (connection == ConnectionType.Fastboot)
            return "Fastboot";

        return mode switch
        {
            AdbMode.Recovery     => "Recovery",
            AdbMode.Sideload     => "Recovery",
            AdbMode.Unauthorized => "未授权",
            AdbMode.Offline      => "离线",
            AdbMode.Bootloader   => "Fastboot",
            AdbMode.Fastboot     => "Fastboot",
            AdbMode.FastbootUsb  => "Fastboot",
            _                    => "系统"   // AdbMode.Device and anything unknown
        };
    }
}
