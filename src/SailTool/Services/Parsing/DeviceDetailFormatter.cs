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
    /// Translates the <c>ro.boot.verifiedbootstate</c> value into the label the
    /// detail card shows.
    /// </summary>
    internal static string FormatBlStatus(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == Dash) return Dash;
        return raw.ToLowerInvariant() switch
        {
            "green"  => "未解锁",
            "orange" => "已解锁",
            "yellow" => "未解锁 (自定义密钥)",
            "red"    => "未解锁 (已验证)",
            _        => raw
        };
    }

    /// <summary>
    /// Translates a bootloader lock state read from <c>fastboot getvar unlocked</c>
    /// into the same labels <see cref="FormatBlStatus"/> produces.
    /// </summary>
    /// <param name="hasVar">Whether the <c>unlocked</c> variable was present at all.</param>
    /// <param name="value">Its raw value when present.</param>
    internal static string FormatFastbootUnlocked(bool hasVar, string? value)
    {
        if (!hasVar) return Dash;
        return value?.Equals("yes", StringComparison.OrdinalIgnoreCase) == true
            ? "已解锁"
            : "未解锁";
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
    /// Maps the (connection type, mode) pair onto the short label the UI shows in
    /// its detection-method column.
    /// </summary>
    /// <remarks>
    /// Rules:
    /// <list type="bullet">
    ///   <item>Fastboot connection → <c>Fastboot</c></item>
    ///   <item>ADB with <c>recovery</c> or <c>sideload</c> → <c>Recovery</c></item>
    ///   <item>ADB with <c>device</c> → <c>系统</c></item>
    ///   <item>ADB with <c>unauthorized</c> → <c>未授权</c></item>
    ///   <item>ADB with <c>offline</c> → <c>离线</c></item>
    ///   <item>ADB with <c>bootloader</c> → <c>Fastboot</c></item>
    /// </list>
    /// </remarks>
    internal static string MapDetectionType(string connectionType, string mode)
    {
        if (connectionType.StartsWith("Fastboot", StringComparison.OrdinalIgnoreCase))
            return "Fastboot";

        return mode?.ToLowerInvariant() switch
        {
            "recovery"     => "Recovery",
            "sideload"     => "Recovery",
            "unauthorized" => "未授权",
            "offline"      => "离线",
            "bootloader"   => "Fastboot",
            _              => "系统"   // default for ADB "device" / unknown
        };
    }

    /// <summary>
    /// Whether <paramref name="connectionType"/> denotes a fastboot connection
    /// rather than an adb one.
    /// </summary>
    internal static bool IsFastbootConnection(string connectionType)
        => connectionType.StartsWith("Fastboot", StringComparison.OrdinalIgnoreCase);
}
