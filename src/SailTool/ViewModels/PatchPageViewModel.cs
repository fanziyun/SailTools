using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SailTool.Services;

namespace SailTool.ViewModels;

/// <summary>
/// Drives the one-click patch card on <c>FlashPage</c>: pick a Magisk APK or AK3
/// zip, pick the boot image to patch, run <see cref="PatchService"/>, and reboot
/// afterwards if the user asks.
/// </summary>
/// <remarks>
/// <para>
/// Split out of <see cref="FlashPageViewModel"/>, which now covers device
/// selection, partition choice and flashing only. The two share one log panel
/// and one device list, both of which stay on <see cref="FlashPageViewModel"/>.
/// </para>
/// <para>
/// The page XAML is deliberately unchanged, so it still binds
/// <c>ViewModel.PatchSelectedDevice</c>; <see cref="FlashPageViewModel"/> exposes
/// that one property as a forwarder to here.
/// </para>
/// <para>
/// Cached for the session by <see cref="FastbootServiceAccessor"/>, so navigating
/// away from the page and back keeps the selected files.
/// </para>
/// </remarks>
public partial class PatchPageViewModel(FastbootService fastboot) : ObservableObject
{
    private readonly FastbootService _fastboot = fastboot;
    private readonly PatchService _patchService = new(fastboot.FastbootPath);

    private CancellationTokenSource? _patchCts;

    /// <summary>
    /// Sink for log lines, injected by <see cref="FlashPageViewModel"/> so patch
    /// output lands in the same log panel the flash card writes to.
    /// Mirrors the delegate-injection idiom used by
    /// <see cref="ROMFlashPageViewModel.ConfirmRebootAsync"/>.
    /// </summary>
    public Action<string>? LogSink { get; set; }

    /// <summary>Whether magiskboot.exe was located on disk.</summary>
    public bool HasMagiskBoot => _patchService.HasMagiskBoot;

    /// <summary>Absolute path magiskboot.exe was resolved to (may not exist).</summary>
    public string MagiskBootPath => _patchService.MagiskBootPath;

    // -- Patch inputs and state --

    [ObservableProperty]
    public partial string PatchType { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PatchFilePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PatchFileName { get; set; } = "请先选择修补文件";

    /// <summary>
    /// Partition the patched image is meant for. Not bound by the page — the
    /// flash step infers the partition from the source image filename instead
    /// (see <c>FlashPage.FlashPatchedImage_Click</c>). Kept because it is part of
    /// the patch state the UI could surface later.
    /// </summary>
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
                outputDir = Path.Combine(Path.GetTempPath(), "SailTool_Patch_" + Guid.NewGuid().ToString("N")[..8]);
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

    /// <summary>修补刷入完成后，用户确认重启到系统时调用</summary>
    [RelayCommand]
    private async Task RebootToSystemAsync()
    {
        try
        {
            LogSink?.Invoke("[重启] 正在重启设备到系统...");
            await _fastboot.RunFastbootAsync("reboot");
            LogSink?.Invoke("[重启] 重启命令已发送");
            PatchStatusText = "修补完成，设备正在重启到系统";
        }
        catch (Exception ex)
        {
            LogSink?.Invoke($"[重启错误] {ex.Message}");
            PatchStatusText = $"重启失败: {ex.Message}";
        }
    }

    private void AppendPatchLog(string message) => LogSink?.Invoke(message);
}
