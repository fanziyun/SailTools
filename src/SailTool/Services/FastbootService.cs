using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using FastTool.Models;

namespace FastTool.Services;

/// <summary>
/// High-level fastboot operations (device detection, flashing).
/// Delegates raw CLI execution to <see cref="FastbootProcessService"/>.
/// </summary>
public sealed partial class FastbootService(FastbootProcessService processService) : IDisposable
{
    private readonly FastbootProcessService _processService = processService;

    // ── Source-generated regexes (zero-alloc, compiled at build time) ──

    [GeneratedRegex(@"^([A-Za-z0-9_-]+)\s+(fastboot(?:_usb)?)", RegexOptions.Compiled)]
    private static partial Regex DeviceLineRegex();

    [GeneratedRegex(@"^([A-Za-z0-9_-]+)\s+(device|recovery|bootloader|sideload|unauthorized|offline)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex AdbDeviceLineRegex();

    [GeneratedRegex(
        @"\((\d{1,3})%",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex ProgressPercentRegex();

    /// <summary>Matches sparse chunk lines like: Sending sparse 'super' 1/13 (721288 KB)</summary>
    [GeneratedRegex(
        @"[Ss]ending\s+sparse\s+'\w+'\s+(\d+)/(\d+)",
        RegexOptions.Compiled)]
    private static partial Regex SparseChunkRegex();

    [GeneratedRegex(
        @"(OKAY|FAILED|finished|error)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex StatusRegex();

    /// <summary>
    /// Matches any "Sending" header (with or without progress/speed):
    ///   Sending 'vendor_boot' (98304 KB)
    ///   Sending 'super' (2097152 KB)   45.2%  37.05 MB/s
    /// Extracts partition name and size in KB.
    /// </summary>
    [GeneratedRegex(
        @"[Ss]ending\s+(?:sparse\s+)?'(\w+)'\s+\((\d+)\s*KB\)",
        RegexOptions.Compiled)]
    private static partial Regex SendingSizeRegex();

    /// <summary>
    /// Matches OKAY with timing: "OKAY [  0.202s]"  or  "OKAY [ 22.022s]"
    /// </summary>
    [GeneratedRegex(
        @"OKAY\s+\[\s*([\d.]+)s?\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex OkayTimeRegex();

    /// <summary>
    /// Matches fastboot v36+ progress lines with percentage AND speed:
    ///   Sending 'vendor_boot' (98304 KB)   45.2%  37.05 MB/s
    ///   Sending 'super' (2097152 KB)   12.3%  1024.00 KB/s
    /// </summary>
    [GeneratedRegex(
        @"[Ss]ending\s+'(\w+)'\s+\(\d+ KB\)\s+(\d+\.?\d*)%\s+(\d+\.?\d*)\s*(MB/s|KB/s|GB/s)",
        RegexOptions.Compiled)]
    private static partial Regex ProgressSpeedRegex();

    /// <summary>All partitions typically flashable on an Android device.</summary>
    public static string[] FlashablePartitions { get; } =
    [
        "boot", "dtbo", "init_boot", "vbmeta", "vbmeta_system",
        "vendor_boot", "recovery", "system", "system_ext",
        "vendor", "product", "cache", "userdata", "super"
    ];

    // ── Forwarded members (delegated to FastbootProcessService) ──
    public string FastbootPath => _processService.FastbootPath;
    public bool HasAdb => _processService.HasAdb;

    public Task<string> RunFastbootAsync(string arguments, CancellationToken ct = default)
        => _processService.RunFastbootAsync(arguments, ct);

    public Task<(int exitCode, string output)> RunAdbAsync(string arguments, CancellationToken ct = default)
        => _processService.RunAdbAsync(arguments, ct);

    // ══════════════════════════════════════════════════════════════
    //  GET CONNECTED DEVICES  (Fastboot + ADB)
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Detects all devices connected via Fastboot AND ADB.
    /// If a device appears in both, the Fastboot entry wins.
    /// </summary>
    public async Task<List<DeviceInfo>> GetConnectedDevicesAsync(
        CancellationToken ct = default)
    {
        var fbTask = GetFastbootOnlyDevicesAsync(ct);
        var adbTask = GetAdbDevicesAsync(ct);

        await Task.WhenAll(fbTask, adbTask);

        var fbDevices  = fbTask.Result;
        var adbDevices = adbTask.Result;

        // Merge: add ADB devices that aren't already in fastboot list
        var knownSerials = fbDevices.Select(d => d.Serial).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var adb in adbDevices)
        {
            if (!knownSerials.Contains(adb.Serial))
                fbDevices.Add(adb);
        }

        return fbDevices;
    }

    // ══════════════════════════════════════════════════════════════
    //  FASTBOOT DEVICES ONLY
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Runs <c>fastboot devices</c> and parses connected devices.
    /// </summary>
    public async Task<List<DeviceInfo>> GetFastbootOnlyDevicesAsync(
        CancellationToken ct = default)
    {
        var output = await _processService.RunFastbootAsync("devices", ct);
        var devices = new List<DeviceInfo>();

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var match = DeviceLineRegex().Match(line);
            if (match.Success)
            {
                devices.Add(new DeviceInfo(
                    Serial:         match.Groups[1].Value.Trim(),
                    Mode:           match.Groups[2].Value.Trim(),
                    ConnectionType: "Fastboot"));
            }
        }

        return devices;
    }

    // ══════════════════════════════════════════════════════════════
    //  ADB DEVICES ONLY
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Runs <c>adb devices</c> and parses connected devices.
    /// Returns empty list if adb.exe is not available.
    /// </summary>
    public async Task<List<DeviceInfo>> GetAdbDevicesAsync(
        CancellationToken ct = default)
    {
        if (!_processService.HasAdb) return [];

        try
        {
            var (exitCode, stdout) = await _processService.RunAdbAsync("devices", ct);
            if (exitCode != 0) return [];

            var devices = new List<DeviceInfo>();
            bool headerSkipped = false;

            foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                // Skip the "List of devices attached" header line
                if (!headerSkipped)
                {
                    if (line.Contains("List of devices attached", StringComparison.OrdinalIgnoreCase))
                    {
                        headerSkipped = true;
                        continue;
                    }
                    headerSkipped = true;
                }

                var match = AdbDeviceLineRegex().Match(line);
                if (match.Success)
                {
                    devices.Add(new DeviceInfo(
                        Serial:         match.Groups[1].Value.Trim(),
                        Mode:           match.Groups[2].Value.Trim(),
                        ConnectionType: "ADB"));
                }
            }

            return devices;
        }
        catch
        {
            return [];
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  DETAILED DEVICE INFO  (via fastboot getvar / adb getprop)
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Fetch detailed device information.
    /// Dispatches to Fastboot or ADB depending on <paramref name="connectionType"/>.
    /// Missing fields default to "--".
    /// </summary>
    public async Task<DetailedDeviceInfo> GetDetailedInfoAsync(
        string serial, string connectionType, string mode, CancellationToken ct = default)
    {
        // Compute the user-facing detection type based on connection type + mode
        var detectionType = MapDetectionType(connectionType, mode);

        if (connectionType.StartsWith("Fastboot", StringComparison.OrdinalIgnoreCase))
            return await GetDetailedFastbootInfoAsync(serial, detectionType, ct);
        else
            return await GetDetailedAdbInfoAsync(serial, detectionType, ct);
    }

    /// <summary>
    /// 把 (connectionType, mode) 翻译成用户想看的检测方式文本。
    /// <para>规则：</para>
    /// <list type="bullet">
    ///   <item>Fastboot 连接 → "Fastboot"</item>
    ///   <item>ADB + recovery / sideload → "Recovery"</item>
    ///   <item>ADB + device → "系统"</item>
    ///   <item>ADB + unauthorized → "未授权"</item>
    ///   <item>ADB + offline → "离线"</item>
    /// </list>
    /// </summary>
    private static string MapDetectionType(string connectionType, string mode)
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
    /// Uses <c>fastboot getvar all</c> to collect as many fields as possible.
    /// </summary>
    private async Task<DetailedDeviceInfo> GetDetailedFastbootInfoAsync(
        string serial, string detectionType, CancellationToken ct)
    {
        var info = DetailedDeviceInfo.Empty(serial, detectionType);
        try
        {
            var output = await _processService.RunFastbootAsync("getvar all", ct);
            // Parse: "(bootloader) key: value"  OR  "key: value"
            var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("(bootloader)", StringComparison.OrdinalIgnoreCase))
                    trimmed = trimmed.Substring("(bootloader)".Length).TrimStart();
                var colon = trimmed.IndexOf(':');
                if (colon <= 0) continue;
                var key = trimmed[..colon].Trim();
                var val = trimmed[(colon + 1)..].Trim();
                vars[key] = val;
            }

            info = info with
            {
                DeviceCodename   = DisplayOrDash(GetOrEmpty(vars, "product")),
                Brand            = DisplayOrDash(GetOrEmpty(vars, "brand")),
                Model            = DisplayOrDash(GetOrEmpty(vars, "model")),
                BootloaderStatus = vars.TryGetValue("unlocked", out var u) && u.Equals("yes", StringComparison.OrdinalIgnoreCase)
                                       ? "已解锁"
                                     : vars.ContainsKey("unlocked") ? "未解锁" : "--",
                Slot             = FormatSlot(GetOrEmpty(vars, "current-slot")),
                // AndroidVersion can't be retrieved via fastboot on most devices
                SystemVersion    = DisplayOrDash(GetOrEmpty(vars, "version-bootloader")),
                KernelVersion    = DisplayOrDash(GetOrEmpty(vars, "kernel")),
            };
        }
        catch { /* keep defaults */ }
        return info;
    }

    /// <summary>
    /// Uses <c>adb shell getprop</c> to collect Android-side info,
    /// plus <c>uname -r</c> for kernel version.
    /// </summary>
    private async Task<DetailedDeviceInfo> GetDetailedAdbInfoAsync(
        string serial, string detectionType, CancellationToken ct)
    {
        var info = DetailedDeviceInfo.Empty(serial, detectionType);
        if (!_processService.HasAdb) return info;

        try
        {
            // Batch all getprop queries in one shell call (fast + atomic)
            var batchCmd =
                "echo DEVICE:$(getprop ro.product.device);" +
                "echo BRAND:$(getprop ro.product.brand);" +
                "echo MODEL:$(getprop ro.product.model);" +
                "echo SLOT:$(getprop ro.boot.slot_suffix);" +
                "echo BL:$(getprop ro.boot.verifiedbootstate);" +
                "echo ANDROID:$(getprop ro.build.version.release);" +
                "echo SYSTEM:$(getprop ro.build.display.id);" +
                "echo KERNEL:$(uname -r);" +
                // HyperOS / MIUI 专属 props
                "echo HYPEROS_NAME:$(getprop ro.mi.os.version.name);" +
                "echo HYPEROS_INCR:$(getprop ro.mi.os.version.incremental);" +
                "echo MIUI_NAME:$(getprop ro.miui.ui.version.name)";

            var (exitCode, output) = await _processService.RunAdbAsync(
                $"-s {serial} shell \"{batchCmd}\"", ct);
            if (exitCode != 0) return info;

            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                var colon = trimmed.IndexOf(':');
                if (colon <= 0) continue;
                var key = trimmed[..colon].Trim();
                var val = trimmed[(colon + 1)..].Trim();
                if (!string.IsNullOrEmpty(val)) dict[key] = val;
            }

            var brand        = GetOrEmpty(dict, "BRAND");
            var hyperName    = GetOrEmpty(dict, "HYPEROS_NAME");
            var hyperIncr    = GetOrEmpty(dict, "HYPEROS_INCR");
            var miuiName     = GetOrEmpty(dict, "MIUI_NAME");
            var displayId    = GetOrEmpty(dict, "SYSTEM");

            info = info with
            {
                DeviceCodename   = DisplayOrDash(GetOrEmpty(dict, "DEVICE")),
                Brand            = DisplayOrDash(brand),
                Model            = DisplayOrDash(GetOrEmpty(dict, "MODEL")),
                Slot             = FormatSlot(GetOrEmpty(dict, "SLOT")),
                BootloaderStatus = FormatBlStatus(GetOrEmpty(dict, "BL")),
                AndroidVersion   = DisplayOrDash(GetOrEmpty(dict, "ANDROID")),
                SystemVersion    = DisplayOrDash(FormatSystemVersion(brand, hyperName, hyperIncr, miuiName, displayId)),
                KernelVersion    = DisplayOrDash(GetOrEmpty(dict, "KERNEL")),
            };
        }
        catch { /* keep defaults */ }
        return info;
    }

    // ── Helpers ──────────────────────────────────────────────────

    /// <summary>
    /// 取字典值，取不到返回空串（不做显示层默认值转换）。
    /// 供需要在逻辑层判断"有无"的方法使用（如 FormatSystemVersion）。
    /// </summary>
    private static string GetOrEmpty(IDictionary<string, string> d, string key)
        => d.TryGetValue(key, out var v) ? v?.Trim() ?? "" : "";

    /// <summary>空值 / 空白 → "--"，供最终显示。</summary>
    private static string DisplayOrDash(string val)
        => string.IsNullOrWhiteSpace(val) ? "--" : val;

    /// <summary>Normalize slot values like "_a" → "A", "_b" → "B", "" → "--".</summary>
    private static string FormatSlot(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "--") return "--";
        return raw.TrimStart('_').ToUpperInvariant() switch
        {
            "A" => "A",
            "B" => "B",
            _   => raw.ToUpperInvariant()
        };
    }

    /// <summary>Normalize BL status from ro.boot.verifiedbootstate values.</summary>
    private static string FormatBlStatus(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "--") return "--";
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
    /// Build the user-facing system version string.
    /// <para>Priorities:</para>
    /// <list type="number">
    ///   <item>Xiaomi HyperOS: 直接展示 ro.mi.os.version.incremental（如 "OS3.0.313.0.WPBCNXM"）</item>
    ///   <item>Xiaomi MIUI legacy: "MIUI " + ro.miui.ui.version.name（如 "MIUI V14"）</item>
    ///   <item>ColorOS (OPPO / OnePlus / Realme): 剥掉型号前缀，只留版本号（如 "16.0.7.206(CN01)"）</item>
    ///   <item>其他品牌: 原样返回 ro.build.display.id</item>
    /// </list>
    /// </summary>
    private static string FormatSystemVersion(
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
        // displayId 形如 "PLK110_16.0.7.206(CN01)",用户实际想看到的是后面的版本号部分
        if (!string.IsNullOrWhiteSpace(brand) && IsColorOsBrand(brand) &&
            !string.IsNullOrWhiteSpace(displayId))
        {
            return StripColorOsModelPrefix(displayId);
        }

        // ── Other brands ──────────────────────────────────────────
        return string.IsNullOrWhiteSpace(displayId) ? "--" : displayId;
    }

    private static bool IsColorOsBrand(string brand)
    {
        return brand.Equals("oppo", StringComparison.OrdinalIgnoreCase)
            || brand.Equals("oneplus", StringComparison.OrdinalIgnoreCase)
            || brand.Equals("realme", StringComparison.OrdinalIgnoreCase)
            || brand.Equals("iqoo", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Strip the leading model-code prefix from ColorOS display IDs.
    /// <para>
    /// Examples:<br/>
    ///   "PLK110_16.0.7.206(CN01)" → "16.0.7.206(CN01)"<br/>
    ///   "CPH2449_14.0.0.400(EX01)" → "14.0.0.400(EX01)"<br/>
    ///   "RMX3700_14.0.0.500(EX01)" → "14.0.0.500(EX01)"
    /// </para>
    /// </summary>
    private static string StripColorOsModelPrefix(string displayId)
    {
        // 通用模式: 任意大写字母/数字组成的型号 + "_" + 版本号
        int underscore = displayId.IndexOf('_');
        if (underscore > 0 && underscore < displayId.Length - 1)
        {
            var prefix = displayId[..underscore];
            // 仅当前缀全是字母/数字时才剥掉(避免误伤正常版本字符串)
            if (prefix.Length > 0 && prefix.All(c => char.IsLetterOrDigit(c)))
                return displayId[(underscore + 1)..];
        }
        return displayId;
    }

    /// <summary>
    /// Flashes <paramref name="imgPath"/> to <paramref name="partition"/>.
    /// Progress (percentage + transfer speed) is reported via <paramref name="onProgress"/>.
    /// Real-time output lines are forwarded to <paramref name="onOutput"/>.
    /// <para>
    /// NOTE: <paramref name="onProgress"/> is called from a background thread!
    /// The caller is responsible for dispatching UI updates via <c>DispatcherQueue</c>.
    /// </para>
    /// </summary>
    public async Task<bool> FlashImageAsync(
        string partition,
        string imgPath,
        Action<FlashProgressInfo>? onProgress = null,
        Action<string>? onOutput = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(imgPath);

        if (!File.Exists(imgPath))
            throw new FileNotFoundException($"Image file not found: {imgPath}");

        var psi = new ProcessStartInfo
        {
            FileName               = _processService.FastbootPath,
            Arguments              = $"flash {partition} \"{imgPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        using var process       = _processService.TrackProcess(psi);
        var       gotFinalPct   = false;
        var       tcsCompletion = new TaskCompletionSource<bool>();

        // ── Sparse chunk speed tracking (closure state for ParseProgress)
        int     _sparseTotal      = 0;    // total chunks (e.g. 13)
        int     _currentChunk     = 0;    // most recent chunk number
        long    _sparseBytesTotal = 0;    // total bytes announced across chunks so far
        DateTime _lastChunkTime   = DateTime.MinValue;
        long    _lastChunkBytes   = 0;    // cumulative bytes at previous chunk boundary

        // ── Fallback speed tracking: "Sending" phase timing ────────
        // When fastboot doesn't output % or MB/s (sparse or old protocol),
        // we compute speed from Sending size / OKAY elapsed time.
        long     _pendingSendKB = 0;     // KB announced by the last "Sending" header
        bool     _inSendPhase   = false; // true between "Sending" and "OKAY"
        DateTime _sendStart     = DateTime.MinValue;

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            tcsCompletion.TrySetResult(process.ExitCode == 0);
        };

        process.Start();

        // ── Read stderr as raw byte stream ────────────────────────
        // fastboot v36+ uses \r (not \n) to overwrite the same line for
        // progress updates, so BeginErrorReadLine() misses intermediate
        // frames.  We split manually on both \r and \n.
        using var stderrStream   = process.StandardError.BaseStream;
        using var stdoutStream   = process.StandardOutput.BaseStream;
        var       stderrReadTask = FastbootProcessService.ReadStreamLinesAsync(stderrStream, onOutput, ParseProgress, ct);
        var       stdoutReadTask = FastbootProcessService.ReadStreamLinesAsync(stdoutStream, onOutput, null,           ct);

        // ── Wait for exit (cooperative with CancellationToken) ────
        using var ctReg = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* swallow */ }
            tcsCompletion.TrySetCanceled(ct);
        });

        await Task.WhenAll(tcsCompletion.Task, stderrReadTask, stdoutReadTask);
        return await tcsCompletion.Task;

        // ── Local function: parse progress from a single line ─────
        void ParseProgress(string line)
        {
            // ── 0. Track "Sending" phase start (fallback speed) ───
            // On any "Sending" line (with or without % or MB/s), capture
            // the file size in KB.  When OKAY [Xs] arrives later, we can
            // derive speed = size / elapsed_time even if fastboot doesn't
            // output native speed data (common on many devices).
            var sendMatch = SendingSizeRegex().Match(line);
            if (sendMatch.Success &&
                long.TryParse(sendMatch.Groups[2].Value, out long sendKB))
            {
                _pendingSendKB = sendKB;
                _inSendPhase   = true;
                _sendStart     = DateTime.UtcNow;
            }

            // 1. fastboot v36+: "Sending 'vendor_boot' (98304 KB)  45.2%  37.05 MB/s"
            var speedMatch = ProgressSpeedRegex().Match(line);
            if (speedMatch.Success &&
                double.TryParse(speedMatch.Groups[2].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double pctVal) &&
                double.TryParse(speedMatch.Groups[3].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double speedVal))
            {
                var unit = speedMatch.Groups[4].Value;
                double speedMBs = unit switch
                {
                    "KB/s" => speedVal / 1024.0,
                    "GB/s" => speedVal * 1024.0,
                    _      => speedVal,   // already MB/s
                };
                gotFinalPct = true;
                // Suppress fallback speed calc (native speed is more accurate)
                _inSendPhase = false;
                onProgress?.Invoke(new FlashProgressInfo(
                    Math.Clamp((int)pctVal, 0, 100), speedMBs));
                return;
            }

            // 2. Bootloader explicit %: "(bootloader) 5%"
            var pctMatch = ProgressPercentRegex().Match(line);
            if (pctMatch.Success &&
                int.TryParse(pctMatch.Groups[1].Value, out int pct))
            {
                gotFinalPct = true;
                onProgress?.Invoke(new FlashProgressInfo(Math.Clamp(pct, 0, 100)));
                return;
            }

            // 3. Sparse chunks: "Sending sparse 'super' 1/13 (721288 KB)"
            var sparseMatch = SparseChunkRegex().Match(line);
            if (sparseMatch.Success &&
                int.TryParse(sparseMatch.Groups[1].Value, out int current) &&
                int.TryParse(sparseMatch.Groups[2].Value, out int total) &&
                total > 0)
            {
                gotFinalPct = true;
                var chunkPct = (int)((long)current * 100 / total);

                // Extract chunk size in KB for speed tracking
                double? speedMBs = null;
                var sizeMatch = System.Text.RegularExpressions.Regex.Match(
                    line, @"\((\d+)\s*KB\)");
                if (sizeMatch.Success &&
                    long.TryParse(sizeMatch.Groups[1].Value, out long chunkKB))
                {
                    _sparseBytesTotal += chunkKB * 1024;

                    if (_lastChunkTime != DateTime.MinValue)
                    {
                        var elapsed = (DateTime.UtcNow - _lastChunkTime).TotalSeconds;
                        if (elapsed > 0.1) // avoid div-by-zero for instant chunks
                        {
                            var bytesSent = _sparseBytesTotal - _lastChunkBytes;
                            speedMBs = bytesSent / elapsed / (1024.0 * 1024.0);
                        }
                    }

                    _lastChunkTime  = DateTime.UtcNow;
                    _lastChunkBytes = _sparseBytesTotal;
                }

                _currentChunk = current;
                _sparseTotal  = total;
                onProgress?.Invoke(new FlashProgressInfo(
                    Math.Clamp(chunkPct, 0, 99), speedMBs));
                return;
            }

            // 4. Terminal status: OKAY/FAILED/finished/error
            //    Also derives transfer speed from OKAY timing as a fallback
            //    when the protocol didn't output native speed data.
            var statusMatch = StatusRegex().Match(line);
            if (statusMatch.Success)
            {
                var status = statusMatch.Groups[1].Value;
                var isOkay = status.Equals("OKAY", StringComparison.OrdinalIgnoreCase)
                          || status.Equals("finished", StringComparison.OrdinalIgnoreCase);

                if (isOkay)
                {
                    // ── Fallback speed: derive from Sending size / OKAY time ──
                    // Only if we were in a send phase and fastboot didn't already
                    // provide speed natively (step 1 would have cleared _inSendPhase).
                    if (_inSendPhase && _pendingSendKB > 0)
                    {
                        var timeMatch = OkayTimeRegex().Match(line);
                        if (timeMatch.Success &&
                            double.TryParse(timeMatch.Groups[1].Value,
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out double elapsed) && elapsed > 0.05)
                        {
                            var derivedMBs = _pendingSendKB / 1024.0 / elapsed;
                            onProgress?.Invoke(new FlashProgressInfo(100, derivedMBs));
                            _inSendPhase   = false;
                            _pendingSendKB = 0;
                            gotFinalPct    = true;
                            return;
                        }
                    }

                    if (!gotFinalPct) onProgress?.Invoke(new FlashProgressInfo(100));
                    _inSendPhase = false;
                }
                else if (status.Equals("FAILED", StringComparison.OrdinalIgnoreCase) ||
                         status.Equals("error", StringComparison.OrdinalIgnoreCase))
                {
                    onProgress?.Invoke(new FlashProgressInfo(-1));
                    _inSendPhase = false;
                }
            }
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  DISPOSE
    // ══════════════════════════════════════════════════════════════

    public void Dispose() => _processService.Dispose();
}
