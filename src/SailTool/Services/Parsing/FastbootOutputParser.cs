using System.Globalization;
using System.Text.RegularExpressions;
using SailTool.Enums;
using SailTool.Models;

namespace SailTool.Services.Parsing;

/// <summary>
/// Pure text parsing for the output of <c>fastboot</c> and <c>adb</c>.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is side-effect free and touches no process, no WinUI type and
/// no global state, which is what makes it reachable from the test project.
/// </para>
/// <para>
/// The format strings below were verified against the real binaries shipped in
/// <c>tools/platform-tools</c>. The shipped <c>fastboot.exe</c> is a build of AOSP
/// fastboot (36.0.0) carrying MeowArch's progress bar, which is switched on by
/// <c>FORCE_PROGRESS=1</c> because a redirected stderr is not a terminal; Google's
/// signed releases report no transfer progress at all. Every line shape any of them
/// can produce is matched:
/// </para>
/// <code>
///   Sending '%s' (%zu KB)                       all
///   Sending sparse '%s' %zu/%zu (%u KB)         all
///   Sending '%s' (%zu KB)  [####&gt;---]  45%  8.0 MiB/19.1 MiB 17.5 MiB/s ETA 0:01
///                                               MeowArch bar (FORCE_PROGRESS)
///   %s: %s/%s (%.1f%%) [%s] %.2f MB/s           older 35.0.2-eng build only
///   Writing '%s'                                all
///   OKAY [%7.3fs]                               all
///   Finished. Total time: %.3fs                 all
/// </code>
/// <para>
/// Against an official build the transfer-progress pattern simply never matches and
/// the tracker falls back to sparse-chunk accounting and OKAY timing. See
/// <see cref="TransferProgressRegex"/> and <c>PROVENANCE.md</c>.
/// </para>
/// </remarks>
internal static partial class FastbootOutputParser
{
    /// <summary>
    /// Matches a <c>fastboot devices</c> line: <c>"1a2b3c4d    fastboot"</c>.
    /// The second group also accepts the <c>fastboot_usb</c> variant some
    /// bootloaders report when connected over a USB3 port.
    /// </summary>
    [GeneratedRegex(@"^([A-Za-z0-9_-]+)\s+(fastboot(?:_usb)?)", RegexOptions.Compiled)]
    internal static partial Regex DeviceLineRegex();

    /// <summary>
    /// Matches an <c>adb devices</c> line: <c>"1a2b3c4d    device"</c>.
    /// The six states adb can report are enumerated in the alternation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Known gap.</strong> The serial class <c>[A-Za-z0-9_-]+</c> has no
    /// dot and no colon, so a device connected over <c>adb tcpip</c> — reported as
    /// <c>"192.168.1.50:5555    device"</c> — does not match and never reaches the
    /// device list. USB serials, including ones with dashes and underscores, and
    /// the <c>emulator-5554</c> form are unaffected.
    /// </para>
    /// <para>
    /// Left as-is deliberately: widening the class changes which devices the app
    /// offers to flash, which is a behaviour change rather than a refactor.
    /// <c>FastbootOutputParserTests</c> pins the current behaviour so the gap does
    /// not become silent.
    /// </para>
    /// </remarks>
    [GeneratedRegex(@"^([A-Za-z0-9_-]+)\s+(device|recovery|bootloader|sideload|unauthorized|offline)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    internal static partial Regex AdbDeviceLineRegex();

    /// <summary>
    /// Matches a percentage in parentheses, tolerating whitespace inside the parens
    /// and the bootloader relay prefix: <c>"(5.0%)"</c>, <c>"( 65.0%)"</c>,
    /// <c>"(bootloader) 47%"</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The recorded sample output shows the percentage itself unpadded —
    /// <c>"(5.0%)"</c>, <c>"(65.0%)"</c>, <c>"(100.0%)"</c> — because it is
    /// formatted by <c>%.1f%%</c> with no width modifier. What is right-aligned is
    /// the size field before it (<c>"recovery:   5.0 MB"</c> versus
    /// <c>"recovery: 100.0 MB"</c>). The <c>\s*</c> inside the parens is therefore
    /// defensive: it costs nothing and covers a build that does pad the percentage,
    /// rather than encoding a claim this binary contradicts.
    /// </para>
    /// <para>
    /// This is the fallback path. When the fastboot build supports transfer
    /// progress, <see cref="TransferProgressRegex"/> matches first and supplies the
    /// byte counts as well; this one only yields a bare percentage.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"\(\s*(?:bootloader\s*\)\s*)?(?<percent>\d{1,3}(?:[.,]\d+)?)\s*%",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    internal static partial Regex ProgressPercentRegex();

    /// <summary>
    /// Matches the transfer progress line emitted by a progress-capable fastboot
    /// build, and captures all four numbers from it:
    /// <code>
    ///   recovery:   5.0 MB/100.0 MB (  5.0%) [raw] 26.40 MB/s
    ///   super:    512.0 MB/2048.0 MB ( 25.0%) [sparse] 38.00 MB/s
    /// </code>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The format string this parses is
    /// <c>"%s: %s/%s (%.1f%%) [%s] %.2f MB/s"</c>, which the 35.0.2-eng build this
    /// repo used to ship emitted. The shipped binary no longer produces it — it
    /// draws <see cref="BarProgressRegex"/>'s bar instead — so this pattern only
    /// matters if someone swaps that older binary back in. Google's signed releases
    /// contain neither format — 37.0.1 and 34.0.5 both have zero occurrences of
    /// <c>(%.1f%%)</c> and zero of <c>MB/s</c>, and no flag turns one on — so both
    /// patterns are dormant against an official build. See
    /// <c>tools/platform-tools/PROVENANCE.md</c>.
    /// </para>
    /// <para>
    /// The partition group is lazy (<c>.+?</c>) so that a target containing a
    /// colon, such as <c>vendor_boot:RAMDISK</c>, is captured whole: the engine
    /// backtracks past the first colon because what follows it is not a number.
    /// A <c>[^:]</c> class could never span the inner colon at all, lazy or not.
    /// </para>
    /// <para>
    /// False positives on other colon-bearing lines are excluded by what must
    /// follow: a number with a size unit, a slash, another number with a size
    /// unit, and a parenthesised percentage. <c>"Finished. Total time: 2.573s"</c>
    /// gets past the colon and the number but fails on the unit, and no other line
    /// fastboot prints carries that whole shape.
    /// </para>
    /// <para>
    /// Decimal separators accept a comma as well as a dot. fastboot formats with
    /// <c>%.1f</c>, which follows the C locale rather than the user's, but accepting
    /// both costs nothing and avoids a silent parse failure on a build that does
    /// honour the locale.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"^\s*(?<partition>.+?):\s*" +
        @"(?<cur>\d+(?:[.,]\d+)?)\s*(?<curUnit>GB|MB|KB|B)\s*/\s*" +
        @"(?<total>\d+(?:[.,]\d+)?)\s*(?<totalUnit>GB|MB|KB|B)\s*" +
        @"\(\s*(?<percent>\d+(?:[.,]\d+)?)\s*%\)\s*" +
        @"(?:\[\s*(?<kind>raw|sparse)\s*\]\s*)?" +
        @"(?:(?<speed>\d+(?:[.,]\d+)?)\s*(?<speedUnit>GB/s|MB/s|KB/s|B/s))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    internal static partial Regex TransferProgressRegex();

    /// <summary>
    /// Matches the single-line bar MeowArch's fastboot draws while a transfer runs,
    /// and captures the same four numbers as <see cref="TransferProgressRegex"/> so
    /// both shapes feed the tracker through one code path:
    /// <code>
    ///   Sending 'boot' (19531 KB)        [#&gt;----------------------]   5%  1.0 MiB/19.1 MiB
    ///   Sending 'boot' (19531 KB)        [#######################&gt;]  94%  18.0 MiB/19.1 MiB 15.0 MiB/s ETA 0:00
    /// </code>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bar redraws with <c>\r</c>, so each frame arrives as its own line once the
    /// reader has split on both CR and LF. Every frame repeats the prolog it was
    /// armed with — in practice the <c>Sending 'name' (N KB)</c> header — which is why
    /// this pattern is not anchored at the start of the line: the prolog is part of
    /// the frame, not a separate line.
    /// </para>
    /// <para>
    /// The speed and eta fields are both optional because the format suppresses them
    /// until two samples exist (no speed on the first frame) and drops the eta on the
    /// final one. The eta text itself is deliberately not captured: it is localised
    /// by MeowArch's i18n table, while nothing in the app displays it — the bar and
    /// the speed label already say what the user needs.
    /// </para>
    /// <para>
    /// Sizes are binary units (<c>MiB</c>), unlike the older build's decimal-looking
    /// <c>MB</c>, so the unit groups accept both spellings and
    /// <see cref="ToBytes(double, string)"/> treats them identically — fastboot's
    /// <c>MB</c> has always been 1024² anyway.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"\[(?<bar>[#>\-]+)\]\s*" +
        @"(?<percent>\d{1,3})%\s+" +
        @"(?<cur>\d+(?:[.,]\d+)?)\s*(?<curUnit>[KMGT]iB|B)\s*/\s*" +
        @"(?<total>\d+(?:[.,]\d+)?)\s*(?<totalUnit>[KMGT]iB|B)" +
        @"(?:\s+(?<speed>\d+(?:[.,]\d+)?)\s*(?<speedUnit>[KMGT]iB/s))?",
        RegexOptions.Compiled)]
    internal static partial Regex BarProgressRegex();

    /// <summary>
    /// Whether <paramref name="line"/> is a transfer-progress frame rather than a
    /// message worth keeping. All three progress shapes count, including a bar
    /// frame that has already had its graphic removed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This decides presentation, not tracking: a progress frame is rendered by
    /// overwriting the previous one in the log panel, every other line is appended.
    /// The tracker still sees all of them — see <c>FastbootService.FlashImageAsync</c>.
    /// </para>
    /// <para>
    /// The stripped shape has to be recognised because the log panel stores the
    /// stripped text: <c>LogBuffer.LastLineIsProgress</c> re-reads the buffer's own
    /// last line to decide whether the next frame may overwrite it, and a frame
    /// whose bar is gone would otherwise stop matching and every frame after the
    /// first would be appended again.
    /// </para>
    /// </remarks>
    internal static bool IsProgressLine(string line) =>
        BarProgressRegex().IsMatch(line) ||
        TransferProgressRegex().IsMatch(line) ||
        StrippedBarFrameRegex().IsMatch(line);

    /// <summary>
    /// Matches a bar frame after <see cref="RemoveProgressBar"/> has run:
    /// <c>93%  93.0 MiB/100.0 MiB</c>.
    /// </summary>
    /// <remarks>
    /// The binary-unit letters are required on both sides of the slash. That is what
    /// keeps this tight enough to run over every buffer line: no ordinary fastboot
    /// message carries <c>NN% N.N MiB/M.M MiB</c>.
    /// </remarks>
    [GeneratedRegex(
        @"\d{1,3}%\s+\d+(?:[.,]\d+)?\s*[KMGT]iB\s*/\s*\d+(?:[.,]\d+)?\s*[KMGT]iB",
        RegexOptions.Compiled)]
    internal static partial Regex StrippedBarFrameRegex();

    /// <summary>Matches just the drawn bar, with the padding around it.</summary>
    [GeneratedRegex(@"\s*\[[#>\-]+\]\s*", RegexOptions.Compiled)]
    internal static partial Regex BarGraphicRegex();

    /// <summary>
    /// Strips the bar graphic from a frame, keeping the numbers:
    /// <code>
    ///   Sending 'boot' (19531 KB)   [####&gt;---]  45%  8.0 MiB/19.1 MiB 17.5 MiB/s
    /// → Sending 'boot' (19531 KB)  45%  8.0 MiB/19.1 MiB 17.5 MiB/s
    /// </code>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The log panel sits directly under a real progress bar, so drawing a second
    /// one in text is pure duplication — and at 24 characters wide it is also the
    /// widest thing on the line. The numbers are kept: they are the part that still
    /// says something after the transfer is over and the bar above has been reset.
    /// </para>
    /// <para>
    /// The padding the binary prints before the bar is consumed with it, so the
    /// prolog and the percentages end up two spaces apart instead of twenty-odd
    /// columns, as if the bar had never been there.
    /// </para>
    /// <para>
    /// Only bar frames are touched. The older build's line carries <c>[raw]</c> /
    /// <c>[sparse]</c> markers, which do not match <see cref="BarGraphicRegex"/> and
    /// are left alone.
    /// </para>
    /// </remarks>
    internal static string RemoveProgressBar(string line)
    {
        var m = BarGraphicRegex().Match(line);
        if (!m.Success) return line;

        // A frame with no prolog starts at the bar itself; there is nothing on the
        // left to separate from, so the replacement would only add a stray indent.
        var replacement = m.Index == 0 ? string.Empty : "  ";
        return line[..m.Index] + replacement + line[(m.Index + m.Length)..];
    }

    /// <summary>
    /// Matches the write phase: <c>"Writing 'recovery'"</c>, which fastboot may
    /// print with its <c>OKAY</c> on the same line.
    /// </summary>
    /// <remarks>
    /// fastboot reports no intermediate progress while writing to flash, so this
    /// line is the signal to move the phase on and take the bar to 100% — the data
    /// has been received and is being committed.
    /// </remarks>
    [GeneratedRegex(
        @"^\s*Writing\s+'(?<partition>[^']+)'",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    internal static partial Regex WritingRegex();

    /// <summary>Matches <c>"Finished. Total time: 2.573s"</c>.</summary>
    [GeneratedRegex(
        @"^\s*Finished\.",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    internal static partial Regex FinishedRegex();

    /// <summary>
    /// Converts a size value and its unit to bytes.
    /// Units are binary (1 MB = 1 MiB = 1024² B), matching what fastboot prints in
    /// both spellings: the older build writes <c>MB</c>, MeowArch's bar writes <c>MiB</c>.
    /// </summary>
    internal static long ToBytes(double value, string unit) => unit.ToUpperInvariant() switch
    {
        "TB" or "TIB" => (long)(value * 1024 * 1024 * 1024 * 1024),
        "GB" or "GIB" => (long)(value * 1024 * 1024 * 1024),
        "MB" or "MIB" => (long)(value * 1024 * 1024),
        "KB" or "KIB" => (long)(value * 1024),
        _             => (long)value          // "B"
    };

    /// <summary>
    /// Converts a rate value and its unit to MB/s.
    /// </summary>
    internal static double ToMegabytesPerSecond(double value, string? unit) =>
        (unit ?? "MB/s").ToUpperInvariant() switch
        {
            "GB/S" or "GIB/S" => value * 1024,
            "KB/S" or "KIB/S" => value / 1024,
            "B/S"             => value / (1024 * 1024),
            _                 => value          // "MB/S", "MIB/S"
        };

    /// <summary>
    /// Parses a number the way fastboot printed it, accepting a comma decimal
    /// separator and always using the invariant culture.
    /// </summary>
    /// <remarks>
    /// <c>double.Parse</c> with the current culture misreads <c>"100.0"</c> on
    /// locales whose decimal separator is a comma — it either fails or yields 1000,
    /// which turns a finished transfer into a nonsensical percentage.
    /// </remarks>
    internal static bool TryParseInvariant(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        return double.TryParse(
            text.Replace(',', '.'),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value);
    }

    /// <summary>
    /// Matches sparse chunk headers such as
    /// <c>"Sending sparse 'super' 1/13 (721288 KB)"</c>.
    /// Fastboot only splits an image into chunks when it exceeds the device's
    /// <c>max-download-size</c>, so this is the main way stock fastboot yields
    /// any intermediate progress at all.
    /// </summary>
    [GeneratedRegex(
        @"[Ss]ending\s+sparse\s+'\w+'\s+(\d+)/(\d+)",
        RegexOptions.Compiled)]
    internal static partial Regex SparseChunkRegex();

    /// <summary>Extracts the size in KB from any <c>"(12345 KB)"</c> fragment.</summary>
    [GeneratedRegex(@"\((\d+)\s*KB\)", RegexOptions.Compiled)]
    internal static partial Regex ChunkSizeKbRegex();

    /// <summary>Matches the terminal status words fastboot prints.</summary>
    [GeneratedRegex(
        @"(OKAY|FAILED|finished|error)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    internal static partial Regex StatusRegex();

    /// <summary>
    /// Matches any "Sending" header, sparse or not, and captures the partition
    /// name and the size in KB:
    /// <code>
    ///   Sending 'vendor_boot' (98304 KB)
    ///   Sending 'super' (2097152 KB)   45.2%  37.05 MB/s
    /// </code>
    /// </summary>
    [GeneratedRegex(
        @"[Ss]ending\s+(?:sparse\s+)?'(\w+)'\s+\((\d+)\s*KB\)",
        RegexOptions.Compiled)]
    internal static partial Regex SendingSizeRegex();

    /// <summary>
    /// Matches OKAY with its timing: <c>"OKAY [  0.202s]"</c> or <c>"OKAY [ 22.022s]"</c>.
    /// </summary>
    [GeneratedRegex(
        @"OKAY\s+\[\s*([\d.]+)s?\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    internal static partial Regex OkayTimeRegex();

    /// <summary>The literal adb header line that precedes the device list.</summary>
    internal const string AdbDevicesHeader = "List of devices attached";

    /// <summary>The literal fastboot prefix on <c>getvar</c> output lines.</summary>
    internal const string BootloaderPrefix = "(bootloader)";

    /// <summary>
    /// Parses the combined output of <c>fastboot devices</c>.
    /// Lines that do not match are skipped rather than treated as an error,
    /// because fastboot interleaves diagnostics on the same stream.
    /// </summary>
    internal static List<DeviceInfo> ParseFastbootDevices(string output)
    {
        var devices = new List<DeviceInfo>();
        if (string.IsNullOrEmpty(output)) return devices;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var match = DeviceLineRegex().Match(line);
            if (!match.Success) continue;

            // The regex already restricts this to fastboot / fastboot_usb, but go
            // through the parser anyway so an unexpected value degrades to a skip
            // instead of silently becoming Fastboot.
            var mode = AdbModeExtensions.FromWireText(match.Groups[2].Value);
            if (mode is null) continue;

            devices.Add(new DeviceInfo(
                Serial:     match.Groups[1].Value.Trim(),
                Mode:       mode.Value,
                Connection: ConnectionType.Fastboot));
        }

        return devices;
    }

    /// <summary>
    /// Parses the output of <c>adb devices</c>, skipping the
    /// "List of devices attached" header.
    /// </summary>
    internal static List<DeviceInfo> ParseAdbDevices(string output)
    {
        var devices = new List<DeviceInfo>();
        if (string.IsNullOrEmpty(output)) return devices;

        var headerSkipped = false;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!headerSkipped)
            {
                if (line.Contains(AdbDevicesHeader, StringComparison.OrdinalIgnoreCase))
                {
                    headerSkipped = true;
                    continue;
                }
                headerSkipped = true;
            }

            var match = AdbDeviceLineRegex().Match(line);
            if (!match.Success) continue;

            var mode = AdbModeExtensions.FromWireText(match.Groups[2].Value);
            if (mode is null) continue;

            devices.Add(new DeviceInfo(
                Serial:     match.Groups[1].Value.Trim(),
                Mode:       mode.Value,
                Connection: ConnectionType.ADB));
        }

        return devices;
    }

    /// <summary>
    /// Parses <c>fastboot getvar all</c> into a case-insensitive lookup.
    /// Accepts both <c>"(bootloader) key: value"</c> and bare <c>"key: value"</c>.
    /// </summary>
    internal static Dictionary<string, string> ParseGetVarAll(string output)
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(output)) return vars;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(BootloaderPrefix, StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(BootloaderPrefix.Length).TrimStart();

            if (!TrySplitKeyValue(trimmed, out var key, out var val)) continue;
            vars[key] = val;
        }

        return vars;
    }

    /// <summary>
    /// Parses the <c>echo KEY:$(...)</c> batch used against <c>adb shell</c>,
    /// dropping entries whose value came back empty.
    /// </summary>
    internal static Dictionary<string, string> ParseGetPropBatch(string output)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(output)) return dict;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TrySplitKeyValue(line.Trim(), out var key, out var val)) continue;
            if (!string.IsNullOrEmpty(val)) dict[key] = val;
        }

        return dict;
    }

    /// <summary>
    /// Splits on the first colon. Returns false when there is no colon, or when
    /// nothing precedes it — a leading colon is a diagnostic, not a key.
    /// </summary>
    private static bool TrySplitKeyValue(string line, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;

        var colon = line.IndexOf(':');
        if (colon <= 0) return false;

        key = line[..colon].Trim();
        value = line[(colon + 1)..].Trim();
        return true;
    }
}
