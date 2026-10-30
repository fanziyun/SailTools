using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FastTool.Models;
using FastTool.Services;

namespace FastTool.ViewModels;

public partial class FlashPageViewModel(FastbootService fastboot) : ObservableObject
{
    private readonly PatchService _patchService = new(fastboot.FastbootPath);
    // -- Static data --
    public string[] AvailablePartitions { get; } = FastbootService.FlashablePartitions;

    // -- Device selection --
    [ObservableProperty]
    public partial string SelectedDevice { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string[] AvailableDevices { get; set; } = [];

    [ObservableProperty]
    public partial bool IsRefreshingDevices { get; set; }

    [ObservableProperty]
    public partial string DeviceRefreshMessage { get; set; } = string.Empty;

    // -- Partition and image --
    [ObservableProperty]
    public partial string SelectedPartition { get; set; } = "boot";

    /// <summary>是否使用自定义分区（true = 用 CustomPartitionName，false = 用 SelectedPartition）</summary>
    [ObservableProperty]
    public partial bool UseCustomPartition { get; set; }

    /// <summary>用户手动输入的自定义分区名</summary>
    [ObservableProperty]
    public partial string CustomPartitionName { get; set; } = string.Empty;

    /// <summary>当前生效的分区名（FlashAsync 使用此属性）</summary>
    public string ActivePartition =>
        UseCustomPartition ? CustomPartitionName.Trim() : SelectedPartition;

    [ObservableProperty]
    public partial string ImagePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ImageFileName { get; set; } = "请选择镜像文件";

    // -- Progress --
    [ObservableProperty]
    public partial int FlashProgress { get; set; }

    [ObservableProperty]
    public partial bool HasExactProgress { get; set; }

    /// <summary>
    /// Transfer speed text for display, e.g. "37.05 MB/s".
    /// Empty when speed is not available (old fastboot or completed).
    /// </summary>
    [ObservableProperty]
    public partial string FlashSpeedText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsFlashing { get; set; }

    [ObservableProperty]
    public partial bool IsLogVisible { get; set; } = true;

    [ObservableProperty]
    public partial string StatusText { get; set; } = "就绪";

    // -- Log --
    private string _logText = string.Empty;

    public string LogText
    {
        get => _logText;
        private set { _logText = value; OnPropertyChanged(); }
    }

    private CancellationTokenSource? _flashCts;
    private CancellationTokenSource? _patchCts;

    // -- Patch --
    [ObservableProperty]
    public partial string PatchType { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PatchFilePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PatchFileName { get; set; } = "请先选择修补文件";

    [ObservableProperty]
    public partial string PatchBootPartition { get; set; } = "boot";

    [ObservableProperty]
    public partial string PatchSelectedDevice { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int PatchProgress { get; set; }

    [ObservableProperty]
    public partial bool IsPatching { get; set; }

    [ObservableProperty]
    public partial string PatchStatusText { get; set; } = "就绪";

    [ObservableProperty]
    public partial string PatchOutputPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PatchImageFilePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PatchImageFileName { get; set; } = "请先选择 .img 文件";

    [ObservableProperty]
    public partial string PatchOutputDirectory { get; set; } = string.Empty;

    // -- Commands --

    [RelayCommand]
    private async Task RefreshDevicesAsync()
        => await RefreshDevicesCoreAsync(silent: false);

    /// <summary>
    /// 供定时器和外部调用的自动刷新入口。
    /// silent=true：不弹提示、不覆盖用户已选设备、不显示刷新中动画。
    /// </summary>
    public async Task AutoRefreshAsync()
        => await RefreshDevicesCoreAsync(silent: true);

    /// <summary>核心刷新逻辑。</summary>
    /// <param name="silent">
    /// true  = 静默模式（自动刷新）：不显示刷新动画、不覆盖用户已选设备、不附加 raw 输出。
    /// false = 显式模式（手动点刷新）：完整反馈 + 自动选首台设备 + 显示 raw 输出诊断。
    /// </param>
    private async Task RefreshDevicesCoreAsync(bool silent)
    {
        // 防止并发刷新（上次还没跑完又来一次）
        if (IsRefreshingDevices) return;

        IsRefreshingDevices = !silent; // 静默时不显示刷新动画
        if (!silent) DeviceRefreshMessage = string.Empty;

        try
        {
            // 捕获 raw fastboot devices 输出用于诊断
            string rawOutput;
            try
            {
                rawOutput = await fastboot.RunFastbootAsync("devices");
            }
            catch (Exception ex)
            {
                DeviceRefreshMessage = $"fastboot devices 执行失败: {ex.Message}";
                AvailableDevices = [];
                return;
            }

            var devices = await fastboot.GetConnectedDevicesAsync();
            var newSerials = devices.Select(d => d.Serial).ToArray();

            // 列表完全没变 → 消息应已经是当前状态，直接返回
            if (AvailableDevices.SequenceEqual(newSerials))
                return;

            AvailableDevices = newSerials;

            if (newSerials.Length == 0)
            {
                SelectedDevice = string.Empty;
                if (silent)
                {
                    DeviceRefreshMessage = "未检测到设备";
                }
                else
                {
                    var trimmed = string.IsNullOrWhiteSpace(rawOutput) ? "(空)" : rawOutput.Trim();
                    if (trimmed.Length > 120) trimmed = trimmed[..120] + "...";
                    DeviceRefreshMessage = $"未检测到设备。fastboot 原始输出: {trimmed}";
                }
            }
            else
            {
                // 只要当前选中设备不在新列表中，就自动选中第一台
                // （无论手动还是自动刷新，让用户始终有一个有效目标）
                bool stillPresent = newSerials.Contains(SelectedDevice, StringComparer.Ordinal);
                if (!stillPresent)
                    SelectedDevice = newSerials[0];

                // 两种模式都更新状态文本，让用户随时看到最新设备数
                DeviceRefreshMessage = $"已发现 {newSerials.Length} 台设备";
            }
        }
        catch (Exception ex)
        {
            DeviceRefreshMessage = $"刷新失败: {ex.Message}";
            AvailableDevices = [];
        }
        finally
        {
            IsRefreshingDevices = false;
        }
    }

    [RelayCommand]
    private async Task BrowseImageAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        picker.FileTypeFilter.Add(".img");
        picker.FileTypeFilter.Add(".bin");
        picker.FileTypeFilter.Add(".zip");
        picker.FileTypeFilter.Add("*");
        picker.SuggestedStartLocation =
            Windows.Storage.Pickers.PickerLocationId.Desktop;

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            ImagePath     = file.Path;
            ImageFileName = file.Name;
        }
    }

    [RelayCommand]
    private async Task FlashAsync()
    {
        var partition = ActivePartition;

        if (string.IsNullOrWhiteSpace(partition) ||
            string.IsNullOrWhiteSpace(ImagePath))
        {
            StatusText = "请先选择分区和镜像文件";
            return;
        }

        if (!File.Exists(ImagePath))
        {
            StatusText = $"文件不存在: {ImagePath}";
            return;
        }

        IsFlashing       = true;
        FlashProgress    = 0;
        HasExactProgress = false;
        FlashSpeedText   = "";
        LogText          = string.Empty;
        StatusText       = $"正在刷写 {partition} 分区...";

        _flashCts = new CancellationTokenSource();

        try
        {
            Action<FlashProgressInfo> progressReporter = info =>
            {
                App.DispatcherQueue.TryEnqueue(() =>
                {
                    if (info.Percent < 0)
                    {
                        StatusText = "[FAIL] 刷写失败";
                        HasExactProgress = false;
                        FlashSpeedText = "";
                        return;
                    }

                    if (info.Percent > 0)
                        HasExactProgress = true;

                    FlashProgress = info.Percent;
                    FlashSpeedText = info.SpeedText;

                    StatusText = info.Percent >= 100
                        ? $"[OK] {partition} 分区刷写完成!"
                        : !string.IsNullOrEmpty(info.SpeedText)
                            ? $"正在刷写... {info.Percent}%  {info.SpeedText}"
                            : $"正在刷写... {info.Percent}%";
                });
            };

            var success = await fastboot.FlashImageAsync(
                partition,
                ImagePath,
                progressReporter,
                line =>
                {
                    App.DispatcherQueue.TryEnqueue(() =>
                    {
                        LogText += line + "\n";
                        OnPropertyChanged(nameof(LogText));
                    });
                },
                _flashCts.Token);

            StatusText       = success ? "[OK] 刷写完成" : "[FAIL] 刷写失败";
            HasExactProgress = false;
            FlashSpeedText   = "";
        }
        catch (OperationCanceledException)
        {
            StatusText = "[CANCEL] 刷写已取消";
        }
        catch (Exception ex)
        {
            StatusText = $"[ERROR] {ex.Message}";
            AppendLog($"[ERROR] {ex.Message}");
        }
        finally
        {
            IsFlashing = false;
            _flashCts?.Dispose();
            _flashCts = null;
        }
    }

    [RelayCommand]
    private void CancelFlash()
    {
        _flashCts?.Cancel();
        StatusText = "正在取消...";
    }

    // -- Patch Commands --
    [RelayCommand]
    private async Task BrowsePatchFileAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        picker.FileTypeFilter.Add(".apk");
        picker.FileTypeFilter.Add(".zip");
        picker.SuggestedStartLocation =
            Windows.Storage.Pickers.PickerLocationId.Desktop;

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            PatchFilePath = file.Path;
            PatchFileName = file.Name;
        }
    }

    [RelayCommand]
    private async Task BrowsePatchImageAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        picker.FileTypeFilter.Add(".img");
        picker.SuggestedStartLocation =
            Windows.Storage.Pickers.PickerLocationId.Desktop;

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            PatchImageFilePath = file.Path;
            PatchImageFileName = file.Name;
        }
    }

    [RelayCommand]
    private async Task PatchAsync()
    {
        AppendPatchLog("[DEBUG] 开始修补流程...");
        
        // Validate inputs - 不需要检查 PatchType，因为会自动检测
        if (string.IsNullOrWhiteSpace(PatchFilePath) || !File.Exists(PatchFilePath))
        {
            PatchStatusText = "请先选择修补工具 (Magisk APK 或 AK3 ZIP)";
            AppendPatchLog($"[错误] {PatchStatusText}");
            return;
        }

        AppendPatchLog($"[OK] 修补工具: {PatchFilePath}");

        if (string.IsNullOrWhiteSpace(PatchImageFilePath) || !File.Exists(PatchImageFilePath))
        {
            PatchStatusText = "请先选择要修补的镜像文件";
            AppendPatchLog($"[错误] {PatchStatusText}");
            return;
        }

        AppendPatchLog($"[OK] 镜像文件: {PatchImageFilePath}");

        // 自动检测修补工具类型
        string fileExtension = Path.GetExtension(PatchFilePath).ToLowerInvariant();
        string detectedType = fileExtension switch
        {
            ".apk" => "magisk",
            ".zip" => "ak3",
            _ => ""
        };

        if (string.IsNullOrEmpty(detectedType))
        {
            PatchStatusText = "无法识别修补工具类型（需要 .apk 或 .zip 文件）";
            AppendPatchLog($"[错误] {PatchStatusText}");
            return;
        }

        AppendPatchLog($"[OK] 检测到工具类型: {detectedType}");

        PatchType = detectedType;

        // 检查 magiskboot 是否存在
        AppendPatchLog($"[DEBUG] 检查 magiskboot: {_patchService.HasMagiskBoot}");
        AppendPatchLog($"[DEBUG] magiskboot 路径: {_patchService.MagiskBootPath}");
        
        if (!_patchService.HasMagiskBoot)
        {
            var errorMsg = $"找不到 magiskboot.exe 工具！\n\n" +
                          $"请在以下位置之一放置 magiskboot.exe:\n" +
                          $"• {_patchService.MagiskBootPath}\n" +
                          $"• 应用目录: {Path.Combine(AppContext.BaseDirectory, "magiskboot.exe")}\n" +
                          $"• platform-tools 目录";
            
            PatchStatusText = "[错误] 缺少 magiskboot.exe";
            AppendPatchLog($"[错误] {errorMsg}");
            
            return;
        }

        // 检查是否检测到设备
        bool hasDevice = !string.IsNullOrWhiteSpace(PatchSelectedDevice);

        // 如果未检测到设备，使用输入文件所在目录作为输出
        if (!hasDevice)
        {
            // 使用镜像文件所在的目录作为输出目录
            PatchOutputDirectory = Path.GetDirectoryName(PatchImageFilePath) ?? "";
            AppendPatchLog($"[INFO] 未检测设备，使用镜像文件目录: {PatchOutputDirectory}");
        }

        IsPatching = true;
        PatchProgress = 0;
        PatchStatusText = "准备修补 boot 镜像...";
        AppendPatchLog($"[INFO] 修补工具: {detectedType} ({(detectedType == "magisk" ? "Magisk" : "AK3")})");
        AppendPatchLog($"[INFO] 修补文件: {PatchFileName}");
        AppendPatchLog($"[INFO] 要修补的镜像: {Path.GetFileName(PatchImageFilePath)}");
        AppendPatchLog(hasDevice ? $"[INFO] 目标设备: {PatchSelectedDevice}" : $"[INFO] 输出目录: {PatchOutputDirectory}");

        _patchCts = new CancellationTokenSource();

        try
        {
            // Step 1: Check if magiskboot is available
            if (!_patchService.HasMagiskBoot)
            {
                throw new FileNotFoundException(
                    $"找不到 magiskboot.exe 工具。\n请将 magiskboot 放在以下位置之一:\n- {_patchService.MagiskBootPath}\n- 应用目录\n- platform-tools 目录",
                    _patchService.MagiskBootPath);
            }

            AppendPatchLog($"[OK] 找到 magiskboot: {_patchService.MagiskBootPath}");

            // Step 2: Determine output directory
            string outputDir;
            if (hasDevice)
            {
                // Device detected - use temp directory for auto-flash
                outputDir = Path.Combine(Path.GetTempPath(), "FastTool_Patch_" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(outputDir);
                AppendPatchLog($"[INFO] 工作目录: {outputDir}");
            }
            else
            {
                // No device - use user-selected directory
                outputDir = PatchOutputDirectory;
                AppendPatchLog($"[INFO] 输出目录: {outputDir}");
            }

            // Step 3: Call PatchService to perform the actual patching
            PatchStatusText = "开始修补 boot 镜像...";
            
            var patchedImagePath = await _patchService.PatchBootImageAsync(
                PatchFilePath,
                PatchImageFilePath,
                outputDir,
                message => AppendPatchLog(message),
                _patchCts.Token);

            PatchOutputPath = patchedImagePath;
            
            // Step 4: Clean up temp directory if no device (already in output dir)
            if (!hasDevice && outputDir != PatchOutputDirectory && Directory.Exists(outputDir))
            {
                try
                {
                    Directory.Delete(outputDir, true);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }

            PatchStatusText = hasDevice ? "修补完成，可以刷入设备" : $"修补完成，文件已保存到: {PatchOutputDirectory}";
            AppendPatchLog($"[成功] 修补完成: {patchedImagePath}");
        }
        catch (OperationCanceledException)
        {
            PatchStatusText = "修补已取消";
        }
        catch (Exception ex)
        {
            PatchStatusText = $"[ERROR] 修补失败: {ex.Message}";
            AppendPatchLog($"[ERROR] {ex.Message}");
        }
        finally
        {
            IsPatching = false;
            _patchCts?.Dispose();
            _patchCts = null;
        }
    }

    private void AppendPatchLog(string message)
    {
        App.DispatcherQueue.TryEnqueue(() =>
        {
            LogText += message + "\n";
            OnPropertyChanged(nameof(LogText));
        });
    }

    public void AppendLog(string message)
    {
        App.DispatcherQueue.TryEnqueue(() =>
        {
            LogText += message + "\n";
            OnPropertyChanged(nameof(LogText));
        });
    }

    /// <summary>修补刷入完成后，用户确认重启到系统时调用</summary>
    [RelayCommand]
    private async Task RebootToSystemAsync()
    {
        try
        {
            AppendLog("[重启] 正在重启设备到系统...");
            await fastboot.RunFastbootAsync("reboot");
            AppendLog("[重启] 重启命令已发送");
            PatchStatusText = "修补完成，设备正在重启到系统";
        }
        catch (Exception ex)
        {
            AppendLog($"[重启错误] {ex.Message}");
            PatchStatusText = $"重启失败: {ex.Message}";
        }
    }
}
