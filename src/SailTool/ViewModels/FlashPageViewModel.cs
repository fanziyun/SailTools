using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SailTool.Helpers;
using SailTool.Models;
using SailTool.Services;

namespace SailTool.ViewModels;

/// <summary>
/// Drives the custom-flash card on <c>FlashPage</c>: device selection, partition
/// choice, image browsing, flashing and the shared log panel.
/// </summary>
/// <remarks>
/// <para>
/// The boot-patching half of this page lives in <see cref="PatchPageViewModel"/>.
/// The two are composed here rather than merged into the page, because
/// <c>FlashPage.xaml</c> binds every patch control through
/// <c>ViewModel.Patch*</c> and that XAML is not being touched. The one such
/// binding the page needs — <see cref="PatchSelectedDevice"/> — is forwarded
/// below; the rest are driven by the page's event handlers, which now call the
/// patch ViewModel's commands directly.
/// </para>
/// <para>
/// Cached for the session by <see cref="FastbootServiceAccessor"/>, so state
/// survives navigating away and back.
/// </para>
/// </remarks>
public partial class FlashPageViewModel : ObservableObject
{
    private readonly FastbootService _fastboot;

    // -- Static data --
    public string[] AvailablePartitions { get; } = FastbootService.FlashablePartitions;

    /// <summary>
    /// The patch card's ViewModel. The page reaches it for the patch commands;
    /// this ViewModel only routes log lines into it and forwards the device
    /// selection the XAML binds.
    /// </summary>
    public PatchPageViewModel Patch { get; }

    /// <summary>
    /// Forwards the patch card's target device. <c>FlashPage.xaml</c> binds this
    /// as <c>ViewModel.PatchSelectedDevice</c>, so the name has to stay here even
    /// though the state belongs to <see cref="PatchPageViewModel"/>.
    /// </summary>
    public string PatchSelectedDevice
    {
        get => Patch.PatchSelectedDevice;
        set => Patch.PatchSelectedDevice = value;
    }

    /// <summary>
    /// Builds the page ViewModel together with its cached patch counterpart and
    /// wires the patch card's log output into this page's single log panel, so
    /// patch lines and flash lines interleave in the order they happen — exactly
    /// as they did when both halves lived in one class.
    /// </summary>
    public FlashPageViewModel(FastbootService fastboot)
        : this(fastboot, FastbootServiceAccessor.CreatePatchViewModel())
    {
    }

    /// <summary>
    /// Explicit-composition constructor, used where a caller wants to supply its
    /// own patch ViewModel (and is what the test project can reach without
    /// touching the accessor's static caches).
    /// </summary>
    public FlashPageViewModel(FastbootService fastboot, PatchPageViewModel patch)
    {
        _fastboot = fastboot;
        Patch = patch;
        Patch.LogSink = AppendLog;

        // <c>FlashPage.xaml</c> binds ComboBox SelectedItem to
        // ViewModel.PatchSelectedDevice with Mode=TwoWay. That forwarding
        // property is not itself observable, so re-raise the patch ViewModel's
        // notification under this name — otherwise the ComboBox would stop
        // seeing changes, which it did before the split.
        Patch.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PatchPageViewModel.PatchSelectedDevice))
                OnPropertyChanged(nameof(PatchSelectedDevice));
        };
    }

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
                rawOutput = await _fastboot.RunFastbootAsync("devices");
            }
            catch (Exception ex)
            {
                DeviceRefreshMessage = $"fastboot devices 执行失败: {ex.Message}";
                AvailableDevices = [];
                return;
            }

            var devices = await _fastboot.GetConnectedDevicesAsync();
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
        // "*" is required for the dialog to offer an "All files" entry, which is
        // what lets a user pick a payload whose extension is not in the list.
        var file = await FilePickerHelper.PickStorageFileAsync(
            App.Window, [".img", ".bin", ".zip", "*"]);

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

            var success = await _fastboot.FlashImageAsync(
                partition,
                ImagePath,
                progressReporter,
                AppendLog,
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

    /// <summary>
    /// Appends a line to the shared log panel. Marshals onto the UI thread, since
    /// fastboot output arrives on the stream reader's thread. This is also the
    /// sink <see cref="PatchPageViewModel"/> writes to, so patch and flash lines
    /// interleave in one buffer.
    /// </summary>
    public void AppendLog(string message)
        => SailTool.Helpers.LogWriter.Append(() => LogText, t => LogText = t, message);
}
