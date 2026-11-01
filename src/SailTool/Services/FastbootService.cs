using System.Diagnostics;
using SailTool.Models;
using SailTool.Services.Parsing;

namespace SailTool.Services;

/// <summary>
/// Flashes images over fastboot and reports progress.
/// </summary>
/// <remarks>
/// <para>
/// Raw CLI execution is delegated to <see cref="FastbootProcessService"/> and
/// device discovery to <see cref="DeviceDetectionService"/>; both are re-exposed
/// here so existing callers keep working through a single object.
/// </para>
/// <para>
/// Text interpretation lives in <see cref="FastbootOutputParser"/>, which is what
/// the test project exercises — see the remarks on
/// <see cref="FastbootOutputParser.TransferProgressRegex"/> before changing anything
/// about progress handling.
/// </para>
/// </remarks>
public sealed class FastbootService(
    FastbootProcessService processService,
    DeviceDetectionService deviceDetection) : IDisposable
{
    private readonly FastbootProcessService _processService = processService;
    private readonly DeviceDetectionService _deviceDetection = deviceDetection;

    /// <summary>All partitions typically flashable on an Android device.</summary>
    public static string[] FlashablePartitions { get; } =
    [
        "boot", "dtbo", "init_boot", "vbmeta", "vbmeta_system",
        "vendor_boot", "recovery", "system", "system_ext",
        "vendor", "product", "cache", "userdata", "super"
    ];

    // ── Forwarded members ────────────────────────────────────────

    /// <summary>Full path to the fastboot.exe executable.</summary>
    public string FastbootPath => _processService.FastbootPath;

    /// <summary>Whether adb.exe was found and is available.</summary>
    public bool HasAdb => _processService.HasAdb;

    /// <summary>Runs any fastboot command and returns combined stdout+stderr.</summary>
    public Task<string> RunFastbootAsync(string arguments, CancellationToken ct = default)
        => _processService.RunFastbootAsync(arguments, ct);

    /// <summary>Runs any adb command and returns its exit code and combined output.</summary>
    public Task<(int exitCode, string output)> RunAdbAsync(string arguments, CancellationToken ct = default)
        => _processService.RunAdbAsync(arguments, ct);

    /// <summary>Detects all connected devices; fastboot entries win over adb ones.</summary>
    public Task<List<DeviceInfo>> GetConnectedDevicesAsync(CancellationToken ct = default)
        => _deviceDetection.GetConnectedDevicesAsync(ct);

    /// <summary>Detects devices visible to <c>fastboot devices</c> only.</summary>
    public Task<List<DeviceInfo>> GetFastbootOnlyDevicesAsync(CancellationToken ct = default)
        => _deviceDetection.GetFastbootOnlyDevicesAsync(ct);

    /// <summary>Detects devices visible to <c>adb devices</c> only.</summary>
    public Task<List<DeviceInfo>> GetAdbDevicesAsync(CancellationToken ct = default)
        => _deviceDetection.GetAdbDevicesAsync(ct);

    /// <summary>Fetches detailed device information over fastboot or adb.</summary>
    public Task<DetailedDeviceInfo> GetDetailedInfoAsync(
        DeviceInfo device, CancellationToken ct = default)
        => _deviceDetection.GetDetailedInfoAsync(device, ct);

    // ══════════════════════════════════════════════════════════════
    //  FLASH
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Whether the fastboot.exe in use can emit transfer progress lines, i.e.
    /// whether the flash UI can show a real percentage and an instantaneous rate.
    /// </summary>
    /// <remarks>
    /// Decided by scanning the binary for the progress format string rather than by
    /// watching what arrives, so the UI can tell the user before a flash starts
    /// instead of leaving an empty bar and no explanation. See
    /// <see cref="FastbootCapability"/> and <c>tools/platform-tools/PROVENANCE.md</c>.
    /// </remarks>
    public bool SupportsTransferProgress
        => FastbootCapability.SupportsTransferProgress(FastbootPath);

    /// <summary>
    /// Flashes <paramref name="imgPath"/> to <paramref name="partition"/>.
    /// Progress (percentage, transfer speed, byte counts and phase) is reported via
    /// <paramref name="onProgress"/>; raw output lines go to <paramref name="onOutput"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// NOTE: <paramref name="onProgress"/> is called from a background thread.
    /// The caller is responsible for dispatching UI updates via <c>DispatcherQueue</c>.
    /// </para>
    /// <para>
    /// Updates are rate-limited to one per <see cref="ProgressThrottle.IntervalMs"/>,
    /// with identical repeats dropped and terminal states passed through at once.
    /// A progress-capable fastboot redraws its transfer line per chunk sent and no
    /// faster than the bar's own 25 ms gate — up to 40 frames a second on a large
    /// image — so an unthrottled callback would saturate the dispatcher.
    /// </para>
    /// <para>
    /// Against an official fastboot build, which reports no intermediate percentage
    /// for a single-chunk transfer, the paths that do yield progress are sparse
    /// chunking, an explicit <c>(bootloader) N%</c> on some devices, and an average
    /// speed derived from the <c>OKAY [Ns]</c> timing. See
    /// <see cref="FastbootOutputParser"/>.
    /// </para>
    /// </remarks>
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

        // The shipped fastboot draws its progress bar only when stderr is a real
        // console, because the bar repaints the line with \r and would corrupt a
        // script's output otherwise. This app always reads through a pipe, so
        // without this the whole transfer is silent. The variable is our patch to
        // the binary (see tools/platform-tools/PROVENANCE.md); a build that does
        // not know it ignores it, and NO_PROGRESS=1 would still win over it.
        psi.Environment["FORCE_PROGRESS"] = "1";

        // The throttle sits between the tracker and the caller: the tracker still
        // sees every line (it needs them all to keep its cursors correct), while the
        // UI only hears about the ones worth rendering.
        var       throttle      = new ProgressThrottle();
        Action<FlashProgressInfo>? throttled = onProgress is null
            ? null
            : info => { if (throttle.ShouldRender(info)) onProgress(info); };

        // Same idea for the log panel: progress lines are sampled, everything else
        // is appended verbatim. Without this a large image writes several thousand
        // near-identical lines into the panel.
        var       logGate       = new ProgressLogGate();
        Action<string>? gatedOutput = onOutput is null
            ? null
            : line => { if (logGate.ShouldLog(line)) onOutput(line); };

        using var process       = _processService.TrackProcess(psi);
        var       tracker       = new FlashProgressTracker(throttled);
        var       tcsCompletion = new TaskCompletionSource<bool>();

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            tcsCompletion.TrySetResult(process.ExitCode == 0);
        };

        process.Start();

        // ── Read stderr as raw byte stream ────────────────────────
        // A progress-capable fastboot uses \r (not \n) to overwrite the same line,
        // so BeginErrorReadLine() misses intermediate frames. We split manually on
        // both \r and \n. onOutput receives the gated lines; the tracker receives
        // every line via onParsed and must not be gated, or its cursors lose data.
        using var stderrStream   = process.StandardError.BaseStream;
        using var stdoutStream   = process.StandardOutput.BaseStream;
        var       stderrReadTask = FastbootProcessService.ReadStreamLinesAsync(stderrStream, gatedOutput, tracker.HandleLine, ct);
        var       stdoutReadTask = FastbootProcessService.ReadStreamLinesAsync(stdoutStream, gatedOutput, null,               ct);

        // ── Wait for exit (cooperative with CancellationToken) ────
        using var ctReg = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* swallow */ }
            tcsCompletion.TrySetCanceled(ct);
        });

        await Task.WhenAll(tcsCompletion.Task, stderrReadTask, stdoutReadTask);
        return await tcsCompletion.Task;
    }

    // ══════════════════════════════════════════════════════════════
    //  DISPOSE
    // ══════════════════════════════════════════════════════════════

    public void Dispose() => _processService.Dispose();
}
