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
/// <c>tools/platform-tools</c> (Platform-Tools 37.0.1). Stock fastboot only ever
/// emits these four shapes:
/// </para>
/// <code>
///   Sending '%s' (%zu KB)
///   Sending sparse '%s' %zu/%zu (%u KB)
///   OKAY [%7.3fs]
///   Finished. Total time: %.3fs
/// </code>
/// <para>
/// It emits no percentage and no transfer rate of its own — see
/// <see cref="ProgressSpeedRegex"/> for the one pattern that expects them.
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
    [GeneratedRegex(@"^([A-Za-z0-9_-]+)\s+(device|recovery|bootloader|sideload|unauthorized|offline)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    internal static partial Regex AdbDeviceLineRegex();

    /// <summary>
    /// Matches an explicit percentage pushed by the bootloader itself, as in
    /// <c>"(bootloader) 5%"</c>. Only some devices emit this.
    /// </summary>
    [GeneratedRegex(
        @"\((\d{1,3})%",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    internal static partial Regex ProgressPercentRegex();

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

    /// <summary>
    /// Matches a progress line that carries BOTH a percentage and a transfer rate:
    /// <code>
    ///   Sending 'vendor_boot' (98304 KB)   45.2%  37.05 MB/s
    ///   Sending 'super' (2097152 KB)   12.3%  1024.00 KB/s
    /// </code>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This never matches stock fastboot.</strong> Verified by extracting
    /// every printable string from both Platform-Tools 37.0.0 and 37.0.1:
    /// neither contains <c>MB/s</c>, <c>KB/s</c>, <c>GB/s</c> or any
    /// <c>%d%%</c>-style format. Upstream <c>fastboot_driver</c> exposes only
    /// prolog / epilog / info / text callbacks and no percentage, and the binary
    /// imports <c>_isatty</c>, so any interactive progress drawing is suppressed
    /// when stderr is redirected to a pipe — which is how this app reads it.
    /// </para>
    /// <para>
    /// The pattern is retained because some vendor-customised fastboot builds do
    /// print this shape, and it costs nothing when it does not match.
    /// <c>FastbootOutputParserTests</c> pins the "does not match stock output"
    /// behaviour so a future fastboot that starts emitting rates is caught.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"[Ss]ending\s+'(\w+)'\s+\(\d+ KB\)\s+(\d+\.?\d*)%\s+(\d+\.?\d*)\s*(MB/s|KB/s|GB/s)",
        RegexOptions.Compiled)]
    internal static partial Regex ProgressSpeedRegex();

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
