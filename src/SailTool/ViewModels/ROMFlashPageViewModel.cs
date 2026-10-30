using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FastTool.Services;
using Windows.Storage.Pickers;

namespace FastTool.ViewModels;

public partial class ROMFlashPageViewModel(FastbootService fastboot) : ObservableObject
{
    private readonly FastbootService _fastboot = fastboot;
    private CancellationTokenSource? _flashCts;

    /// <summary>
    /// UI 层注入的重启确认回调。
    /// 参数为设备当前模式描述（如"系统"、"Recovery"），返回值表示用户是否确认。
    /// </summary>
    public Func<string, Task<bool>>? ConfirmRebootAsync { get; set; }

    // ── 文件夹路径 ─────────────────────────────────────────────
    [ObservableProperty]
    public partial string FolderPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FolderDisplayText { get; set; } = "请先选择线刷包文件夹";

    // ── Bat 文件检测结果 ───────────────────────────────────────
    [ObservableProperty]
    public partial bool HasFlashAll { get; set; }

    [ObservableProperty]
    public partial bool HasFlashExceptStorage { get; set; }

    [ObservableProperty]
    public partial bool HasFlashLock { get; set; }

    /// <summary>当前选中的 bat 文件名（对应 RadioButton 的 GroupName 逻辑）</summary>
    [ObservableProperty]
    public partial string SelectedBatFile { get; set; } = "flash_all.bat";

    // ── 状态 ───────────────────────────────────────────────────
    [ObservableProperty]
    public partial bool IsFlashing { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "就绪";

    // ── 总体进度（跨分区累计）──────────────────────────────────
    [ObservableProperty]
    public partial int OverallProgress { get; set; }

    [ObservableProperty]
    public partial bool HasExactProgress { get; set; }

    [ObservableProperty]
    public partial string CurrentPartitionName { get; set; } = string.Empty;

    // ── 日志 ───────────────────────────────────────────────────
    private string _logText = string.Empty;

    public string LogText
    {
        get => _logText;
        private set { _logText = value; OnPropertyChanged(); }
    }

    private void AppendLog(string msg)
    {
        App.DispatcherQueue.TryEnqueue(() =>
        {
            LogText += $"[{DateTime.Now:HH:mm:ss}] {msg}\n";
        });
    }

    // ══════════════════════════════════════════════════════════════
    //  选择文件夹
    // ══════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task BrowseFolderAsync()
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.Desktop
        };
        picker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        FolderPath        = folder.Path;
        FolderDisplayText = folder.Path;
        DetectBatFiles(folder.Path);
    }

    private void DetectBatFiles(string folderPath)
    {
        HasFlashAll           = File.Exists(Path.Combine(folderPath, "flash_all.bat"));
        HasFlashExceptStorage = File.Exists(Path.Combine(folderPath, "flash_all_except_storage.bat"));
        HasFlashLock          = File.Exists(Path.Combine(folderPath, "flash_all_lock.bat"));

        // 默认选中第一个可用的
        if (HasFlashAll)                  SelectedBatFile = "flash_all.bat";
        else if (HasFlashExceptStorage)   SelectedBatFile = "flash_all_except_storage.bat";
        else if (HasFlashLock)            SelectedBatFile = "flash_all_lock.bat";

        AppendLog($"已选择文件夹: {folderPath}");
        AppendLog($"  flash_all.bat                : {(HasFlashAll           ? "找到" : "未找到")}");
        AppendLog($"  flash_all_except_storage.bat : {(HasFlashExceptStorage ? "找到" : "未找到")}");
        AppendLog($"  flash_all_lock.bat           : {(HasFlashLock          ? "找到" : "未找到")}");

        if (!HasFlashAll && !HasFlashExceptStorage && !HasFlashLock)
            AppendLog("[警告] 未在任何位置找到线刷脚本，请确认所选文件夹为小米线刷包");
    }

    // ══════════════════════════════════════════════════════════════
    //  开始线刷
    // ══════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task StartFlashAsync()
    {
        if (string.IsNullOrEmpty(FolderPath) || string.IsNullOrEmpty(SelectedBatFile))
        {
            StatusText = "请先选择文件夹";
            return;
        }

        var batPath = Path.Combine(FolderPath, SelectedBatFile);
        if (!File.Exists(batPath))
        {
            AppendLog($"[错误] {SelectedBatFile} 不存在: {batPath}");
            StatusText = "脚本文件不存在";
            return;
        }

        IsFlashing       = true;
        OverallProgress  = 0;
        HasExactProgress = false;
        _flashCts        = new CancellationTokenSource();

        try
        {
            // ── 阶段 1：检测设备并引导至 Fastboot 模式 ────────────
            var ready = await PrepareDeviceForFlashAsync(_flashCts.Token);
            if (!ready)
            {
                // PrepareDevice 内部已经设置好 StatusText / 日志
                return;
            }

            // ── 阶段 2：通过 cmd.exe 直接运行 OEM 线刷脚本 ────────
            await ExecuteBatAsync(batPath, _flashCts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消";
            AppendLog("[取消] 用户取消了线刷");
        }
        catch (Exception ex)
        {
            StatusText = $"错误: {ex.Message}";
            AppendLog($"[错误] {ex.Message}");
        }
        finally
        {
            IsFlashing           = false;
            HasExactProgress     = false;
            CurrentPartitionName = string.Empty;
            _flashCts            = null;
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  阶段 1：检测设备 → 必要时重启到 Bootloader → 等待 Fastboot
    // ══════════════════════════════════════════════════════════════

    /// <returns>true = 设备已在 Fastboot 模式，可以执行脚本</returns>
    private async Task<bool> PrepareDeviceForFlashAsync(CancellationToken ct)
    {
        StatusText = "正在检测设备...";
        AppendLog("────────────────────────────────");
        AppendLog("正在检测已连接设备...");

        var devices = await _fastboot.GetConnectedDevicesAsync(ct);

        if (devices.Count == 0)
        {
            StatusText = "未检测到设备";
            AppendLog("[错误] 未检测到任何设备，请确认 USB 连接和驱动");
            return false;
        }

        // 取第一台设备（多设备场景 bat 脚本通常只能操作一台）
        var device = devices[0];

        if (devices.Count > 1)
            AppendLog($"[提示] 检测到 {devices.Count} 台设备，将使用第一台: {device.Serial}");

        AppendLog($"设备: {device.Serial}  连接方式: {device.ConnectionType}  状态: {device.Mode}");

        // ── 已在 Fastboot 模式：无需重启 ─────────────────────
        if (device.ConnectionType.Equals("Fastboot", StringComparison.OrdinalIgnoreCase))
        {
            AppendLog("设备已在 Fastboot 模式，准备开始线刷");
            StatusText = "设备就绪，开始线刷";
            return true;
        }

        // ── ADB 模式：需判断是否可操作 ────────────────────────
        if (device.Mode.Equals("unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            StatusText = "设备未授权";
            AppendLog("[错误] 设备处于未授权状态，请在手机上允许 USB 调试");
            return false;
        }

        if (device.Mode.Equals("offline", StringComparison.OrdinalIgnoreCase))
        {
            StatusText = "设备离线";
            AppendLog("[错误] 设备处于离线状态，请拔插 USB 后重试");
            return false;
        }

        // ── 系统模式 / Recovery / Sideload → 重启到 Bootloader ──
        if (!_fastboot.HasAdb)
        {
            StatusText = "缺少 adb.exe";
            AppendLog("[错误] 未找到 adb.exe，无法从系统/Recovery 重启设备");
            return false;
        }

        var modeLabel = device.Mode.ToLowerInvariant() switch
        {
            "recovery" => "Recovery",
            "sideload" => "Recovery (sideload)",
            "device"   => "系统",
            _          => device.Mode
        };

        // ── 重启前向 UI 层请求确认 ─────────────────────────────
        if (ConfirmRebootAsync is not null)
        {
            var confirmed = await ConfirmRebootAsync(modeLabel);
            if (!confirmed)
            {
                StatusText = "用户取消";
                AppendLog("[取消] 用户拒绝了重启到 Bootloader");
                return false;
            }
        }

        AppendLog($"设备当前处于 {modeLabel} 模式，正在重启到 Bootloader...");
        StatusText = $"正在从 {modeLabel} 重启到 Bootloader...";

        var (exitCode, rebootOutput) = await _fastboot.RunAdbAsync($"-s {device.Serial} reboot bootloader");

        if (exitCode != 0)
        {
            StatusText = "重启命令失败";
            AppendLog($"[错误] adb reboot bootloader 失败 (exit={exitCode}): {rebootOutput}");
            return false;
        }

        AppendLog("重启命令已发送，等待设备进入 Fastboot 模式...");
        StatusText = "等待设备进入 Fastboot 模式...";

        // ── 轮询等待 Fastboot 设备出现（最多 60 秒）─────────
        const int maxWaitSeconds   = 60;
        const int pollIntervalMs   = 2000;
        var       waited           = 0;

        while (waited < maxWaitSeconds)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(pollIntervalMs, ct);
            waited += pollIntervalMs / 1000;

            var fbDevices = await _fastboot.GetFastbootOnlyDevicesAsync(ct);
            if (fbDevices.Count > 0)
            {
                var fb = fbDevices[0];
                AppendLog($"设备已进入 Fastboot 模式: {fb.Serial}");
                StatusText = "设备就绪，开始线刷";
                return true;
            }

            AppendLog($"  等待中... ({waited}s / {maxWaitSeconds}s)");
        }

        StatusText = "等待超时";
        AppendLog($"[错误] 等待 {maxWaitSeconds} 秒后仍未检测到 Fastboot 设备，请手动将设备重启到 Bootloader 模式");
        return false;
    }

    // ══════════════════════════════════════════════════════════════
    //  阶段 2：通过 cmd.exe 直接运行 OEM 线刷脚本
    // ══════════════════════════════════════════════════════════════

    private async Task ExecuteBatAsync(string batPath, CancellationToken ct)
    {
        StatusText = $"正在执行 {SelectedBatFile} ...";
        AppendLog($"────────────────────────────────");
        AppendLog($"开始执行: {SelectedBatFile}");
        AppendLog($"工作目录: {FolderPath}");

        var psi = new ProcessStartInfo
        {
            FileName               = "cmd.exe",
            Arguments              = $"/c \"{batPath}\"",
            WorkingDirectory       = FolderPath,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        // 将 platform-tools 目录注入 PATH，确保 bat 内调用 fastboot 时能找到
        var fbDir       = Path.GetDirectoryName(_fastboot.FastbootPath) ?? "";
        var currentPath = psi.EnvironmentVariables["PATH"] ?? "";
        psi.EnvironmentVariables["PATH"] = $"{fbDir};{currentPath}";

        using var process       = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var       tcsCompletion = new TaskCompletionSource<bool>();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            App.DispatcherQueue.TryEnqueue(() => AppendLog(e.Data));
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            App.DispatcherQueue.TryEnqueue(() => AppendLog(e.Data));
        };
        process.Exited += (_, _) =>
            tcsCompletion.TrySetResult(process.ExitCode == 0);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var ctReg = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* best effort */ }
            tcsCompletion.TrySetCanceled();
        });

        var success = await tcsCompletion.Task;
        StatusText  = success ? "线刷完成" : "线刷失败 (非零退出码)";
        AppendLog(success ? "[完成] 线刷成功" : "[失败] 线刷返回非零退出码");
    }

    // ══════════════════════════════════════════════════════════════
    //  取消
    // ══════════════════════════════════════════════════════════════

    [RelayCommand]
    private void CancelFlash()
    {
        _flashCts?.Cancel();
        StatusText = "正在取消...";
    }
}
