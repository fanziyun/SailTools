using SailTool.Models;
using SailTool.Services.Parsing;

namespace SailTool.Services;

/// <summary>
/// Discovers connected devices and reads their detail fields.
/// </summary>
/// <remarks>
/// <para>
/// All CLI execution goes through <see cref="FastbootProcessService"/>, and all
/// text interpretation through <see cref="FastbootOutputParser"/> and
/// <see cref="DeviceDetailFormatter"/>, so this class holds only orchestration.
/// </para>
/// <para>
/// Split out of <see cref="FastbootService"/>, which now covers flashing only.
/// </para>
/// </remarks>
public sealed class DeviceDetectionService(FastbootProcessService processService)
{
    private readonly FastbootProcessService _processService = processService;

    /// <summary>
    /// Detects all devices connected via fastboot AND adb.
    /// If a device appears in both, the fastboot entry wins.
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

    /// <summary>
    /// Runs <c>fastboot devices</c> and parses connected devices.
    /// </summary>
    public async Task<List<DeviceInfo>> GetFastbootOnlyDevicesAsync(
        CancellationToken ct = default)
    {
        var output = await _processService.RunFastbootAsync("devices", ct);
        return FastbootOutputParser.ParseFastbootDevices(output);
    }

    /// <summary>
    /// Runs <c>adb devices</c> and parses connected devices.
    /// Returns an empty list if adb.exe is not available, or if adb fails —
    /// a missing adb server must not break device detection.
    /// </summary>
    public async Task<List<DeviceInfo>> GetAdbDevicesAsync(
        CancellationToken ct = default)
    {
        if (!_processService.HasAdb) return [];

        try
        {
            var (exitCode, stdout) = await _processService.RunAdbAsync("devices", ct);
            if (exitCode != 0) return [];

            return FastbootOutputParser.ParseAdbDevices(stdout);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Fetches detailed device information, dispatching to fastboot or adb
    /// depending on <paramref name="connectionType"/>.
    /// Fields that cannot be read come back as <see cref="DeviceDetailFormatter.Dash"/>.
    /// </summary>
    public async Task<DetailedDeviceInfo> GetDetailedInfoAsync(
        string serial, string connectionType, string mode, CancellationToken ct = default)
    {
        // Compute the user-facing detection type based on connection type + mode
        var detectionType = DeviceDetailFormatter.MapDetectionType(connectionType, mode);

        return DeviceDetailFormatter.IsFastbootConnection(connectionType)
            ? await GetDetailedFastbootInfoAsync(serial, detectionType, ct)
            : await GetDetailedAdbInfoAsync(serial, detectionType, ct);
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
            var vars = FastbootOutputParser.ParseGetVarAll(output);

            info = info with
            {
                DeviceCodename   = DeviceDetailFormatter.DisplayOrDash(DeviceDetailFormatter.GetOrEmpty(vars, "product")),
                Brand            = DeviceDetailFormatter.DisplayOrDash(DeviceDetailFormatter.GetOrEmpty(vars, "brand")),
                Model            = DeviceDetailFormatter.DisplayOrDash(DeviceDetailFormatter.GetOrEmpty(vars, "model")),
                BootloaderStatus = DeviceDetailFormatter.FormatFastbootUnlocked(
                    vars.TryGetValue("unlocked", out var unlocked), unlocked),
                Slot             = DeviceDetailFormatter.FormatSlot(DeviceDetailFormatter.GetOrEmpty(vars, "current-slot")),
                // AndroidVersion can't be retrieved via fastboot on most devices
                SystemVersion    = DeviceDetailFormatter.DisplayOrDash(DeviceDetailFormatter.GetOrEmpty(vars, "version-bootloader")),
                KernelVersion    = DeviceDetailFormatter.DisplayOrDash(DeviceDetailFormatter.GetOrEmpty(vars, "kernel")),
            };
        }
        catch { /* keep defaults */ }
        return info;
    }

    /// <summary>
    /// Uses <c>adb shell getprop</c> to collect Android-side info,
    /// plus <c>uname -r</c> for the kernel version.
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

            var dict = FastbootOutputParser.ParseGetPropBatch(output);

            var brand     = DeviceDetailFormatter.GetOrEmpty(dict, "BRAND");
            var hyperName = DeviceDetailFormatter.GetOrEmpty(dict, "HYPEROS_NAME");
            var hyperIncr = DeviceDetailFormatter.GetOrEmpty(dict, "HYPEROS_INCR");
            var miuiName  = DeviceDetailFormatter.GetOrEmpty(dict, "MIUI_NAME");
            var displayId = DeviceDetailFormatter.GetOrEmpty(dict, "SYSTEM");

            info = info with
            {
                DeviceCodename   = DeviceDetailFormatter.DisplayOrDash(DeviceDetailFormatter.GetOrEmpty(dict, "DEVICE")),
                Brand            = DeviceDetailFormatter.DisplayOrDash(brand),
                Model            = DeviceDetailFormatter.DisplayOrDash(DeviceDetailFormatter.GetOrEmpty(dict, "MODEL")),
                Slot             = DeviceDetailFormatter.FormatSlot(DeviceDetailFormatter.GetOrEmpty(dict, "SLOT")),
                BootloaderStatus = DeviceDetailFormatter.FormatBlStatus(DeviceDetailFormatter.GetOrEmpty(dict, "BL")),
                AndroidVersion   = DeviceDetailFormatter.DisplayOrDash(DeviceDetailFormatter.GetOrEmpty(dict, "ANDROID")),
                SystemVersion    = DeviceDetailFormatter.DisplayOrDash(
                    DeviceDetailFormatter.FormatSystemVersion(brand, hyperName, hyperIncr, miuiName, displayId)),
                KernelVersion    = DeviceDetailFormatter.DisplayOrDash(DeviceDetailFormatter.GetOrEmpty(dict, "KERNEL")),
            };
        }
        catch { /* keep defaults */ }
        return info;
    }
}
