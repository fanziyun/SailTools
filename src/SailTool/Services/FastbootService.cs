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
/// <see cref="FastbootOutputParser.ProgressSpeedRegex"/> before changing anything
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
        string serial, string connectionType, string mode, CancellationToken ct = default)
        => _deviceDetection.GetDetailedInfoAsync(serial, connectionType, mode, ct);

    // ══════════════════════════════════════════════════════════════
    //  FLASH
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Flashes <paramref name="imgPath"/> to <paramref name="partition"/>.
    /// Progress (percentage and, where available, transfer speed) is reported via
    /// <paramref name="onProgress"/>; raw output lines go to <paramref name="onOutput"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// NOTE: <paramref name="onProgress"/> is called from a background thread.
    /// The caller is responsible for dispatching UI updates via <c>DispatcherQueue</c>.
    /// </para>
    /// <para>
    /// Stock fastboot reports no intermediate percentage for a single-chunk
    /// transfer. The paths that do yield one are sparse chunking, an explicit
    /// <c>(bootloader) N%</c> on some devices, and a speed derived from the
    /// <c>OKAY [Ns]</c> timing. See <see cref="FastbootOutputParser"/>.
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

        using var process       = _processService.TrackProcess(psi);
        var       tracker       = new FlashProgressTracker(onProgress);
        var       tcsCompletion = new TaskCompletionSource<bool>();

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
        var       stderrReadTask = FastbootProcessService.ReadStreamLinesAsync(stderrStream, onOutput, tracker.HandleLine, ct);
        var       stdoutReadTask = FastbootProcessService.ReadStreamLinesAsync(stdoutStream, onOutput, null,            ct);

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
