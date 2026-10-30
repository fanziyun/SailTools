using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using FastTool.Services;
using FastTool.ViewModels;

namespace FastTool.Pages;

public sealed partial class FlashPage : Page
{
    private readonly FlashPageViewModel ViewModel;
    private readonly DispatcherTimer _autoRefreshTimer;

    /// <summary>
    /// 当从「一键修补」卡片点击「刷入修补后的镜像」触发刷写时为 true。
    /// 此时进度条和状态都显示在一键修补卡片内，而不是下方自定义刷入卡片。
    /// </summary>
    private bool _isPatchFlowActive;

    public FlashPage()
    {
        ViewModel = FastbootServiceAccessor.CreateFlashViewModel();
        InitializeComponent();
        DataContext = ViewModel;

        // XAML 中不设 IsChecked="True"，因为 Checked 事件依赖 PartitionCombo / CustomPartitionBox，
        // 这些控件在 InitializeComponent 完成后才可用。此处设 IsChecked 可安全触发 Checked。
        PresetPartitionRadio.IsChecked = true;

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;

        // 3 秒自动检测定时器（在 UI 线程上触发，可安全操作 UI）
        _autoRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3),
        };
        _autoRefreshTimer.Tick += AutoRefreshTimer_Tick;

        // 显式刷新一次（页面初显时提示用户设备状态）
        Loaded += async (_, _) =>
        {
            try
            {
                RestoreUiState();
                await ViewModel.RefreshDevicesCommand.ExecuteAsync(null);
                _autoRefreshTimer.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        };

        // 离开页面时停定时器
        Unloaded += (_, _) => _autoRefreshTimer.Stop();
    }

    private async void AutoRefreshTimer_Tick(object? sender, object e)
    {
        try
        {
            // 正在刷写或上一次刷新还没完成 → 跳过本次 tick，避免冲突
            if (ViewModel.IsFlashing || ViewModel.IsRefreshingDevices) return;

            await ViewModel.AutoRefreshAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private void ViewModel_PropertyChanged(object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(FlashPageViewModel.ImagePath):
                FileNameTextBox.Text = ViewModel.ImagePath;
                break;

            case nameof(FlashPageViewModel.FlashProgress):
                UpdateProgressUi();
                break;

            case nameof(FlashPageViewModel.FlashSpeedText):
                SpeedLabel.Text = ViewModel.FlashSpeedText;
                if (_isPatchFlowActive)
                    PatchFlashSpeedLabel.Text = ViewModel.FlashSpeedText;
                break;

            case nameof(FlashPageViewModel.StatusText):
                // patch-flow 时把状态文本路由到 patch 卡片内，不更新下方自定义刷入卡片的 StatusLabel
                if (_isPatchFlowActive)
                    PatchFlashStatusLabel.Text = ViewModel.StatusText;
                else
                    StatusLabel.Text = ViewModel.StatusText;
                break;

            case nameof(FlashPageViewModel.IsFlashing):
                // patch-flow 模式：只更新 patch 卡片内的进度环 + 转圈环，不动下方自定义刷入卡片
                if (_isPatchFlowActive)
                {
                    PatchFlashRing.IsActive = ViewModel.IsFlashing;
                    if (!ViewModel.IsFlashing)
                    {
                        PatchFlashSpeedLabel.Text = "";
                        PatchFlashBorder.Visibility = Visibility.Collapsed;
                        if (ViewModel.StatusText.StartsWith("[OK]", StringComparison.OrdinalIgnoreCase))
                            PatchResultBorder.Visibility = Visibility.Visible;
                        _isPatchFlowActive = false;
                    }
                    break;
                }

                // 普通 flash-flow：更新自定义刷入卡片的按钮/转圈/进度行
                FlashBtn.IsEnabled          = !ViewModel.IsFlashing;
                CancelBtn.IsEnabled         =  ViewModel.IsFlashing;
                FlashRing.IsActive          =  ViewModel.IsFlashing;
                if (ViewModel.IsFlashing)
                {
                    ProgressRow.Visibility      = Visibility.Visible;
                    IndeterminateBar.Visibility = Visibility.Visible;
                    ExactBar.Visibility         = Visibility.Collapsed;
                    PercentLabel.Text           = "";
                    SpeedLabel.Text             = "";
                }
                else if (!ViewModel.HasExactProgress)
                {
                    ProgressRow.Visibility      = Visibility.Collapsed;
                    IndeterminateBar.Visibility = Visibility.Collapsed;
                    PercentLabel.Text           = "";
                    SpeedLabel.Text             = "";
                }
                break;

            case nameof(FlashPageViewModel.LogText):
                LogTextBlock.Text = ViewModel.LogText;
                // Auto-scroll to bottom
                LogScroll.ChangeView(null, double.MaxValue, null);
                break;
        }
    }

    /// <summary>
    /// 根据 ViewModel 的 FlashProgress / HasExactProgress 同步两套进度 UI。
    /// patch-flow 激活时只更新一键修补卡片内的进度，跳过自定义刷入卡片。
    /// </summary>
    private void UpdateProgressUi()
    {
        var pct = ViewModel.FlashProgress;

        // ── 自定义刷入卡片的主进度行（patch-flow 时完全跳过）─────
        if (!_isPatchFlowActive)
        {
            ProgressRow.Visibility = Visibility.Visible;
            if (ViewModel.HasExactProgress)
            {
                ExactBar.Value              = pct;
                ExactBar.Visibility         = Visibility.Visible;
                IndeterminateBar.Visibility = Visibility.Collapsed;
                PercentLabel.Text           = pct >= 0 ? $"{pct}%" : "";
            }
            else if (ViewModel.IsFlashing)
            {
                IndeterminateBar.Visibility = Visibility.Visible;
                ExactBar.Visibility         = Visibility.Collapsed;
                PercentLabel.Text           = "";
            }
            else
            {
                ExactBar.Visibility         = Visibility.Collapsed;
                IndeterminateBar.Visibility = Visibility.Collapsed;
                ProgressRow.Visibility      = Visibility.Collapsed;
                PercentLabel.Text           = "";
                SpeedLabel.Text             = "";
            }
        }

        // ── 一键修补卡片内嵌进度（仅 patch-flow 激活时）──────
        if (_isPatchFlowActive)
        {
            PatchFlashBorder.Visibility = Visibility.Visible;
            if (ViewModel.HasExactProgress)
            {
                PatchFlashExactBar.Value          = pct;
                PatchFlashExactBar.Visibility     = Visibility.Visible;
                PatchFlashIndeterminateBar.Visibility = Visibility.Collapsed;
                PatchFlashPercentLabel.Text       = pct >= 0 ? $"{pct}%" : "";
            }
            else if (ViewModel.IsFlashing)
            {
                PatchFlashIndeterminateBar.Visibility = Visibility.Visible;
                PatchFlashExactBar.Visibility     = Visibility.Collapsed;
                PatchFlashPercentLabel.Text       = "";
            }
        }
    }

    // ── Event handlers delegate to ViewModel commands ─────────────

    private void PresetPartition_Checked(object sender, RoutedEventArgs e)
    {
        ViewModel.UseCustomPartition  = false;
        PartitionCombo.Visibility     = Visibility.Visible;
        CustomPartitionBox.Visibility = Visibility.Collapsed;
    }

    private void CustomPartition_Checked(object sender, RoutedEventArgs e)
    {
        ViewModel.UseCustomPartition  = true;
        PartitionCombo.Visibility     = Visibility.Collapsed;
        CustomPartitionBox.Visibility = Visibility.Visible;
        CustomPartitionBox.Focus(FocusState.Programmatic);
    }

    private async void RefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.RefreshDevicesCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private async void BrowseImage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.BrowseImageCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private async void Flash_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.FlashCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
        => ViewModel.CancelFlashCommand.Execute(null);

    // ── Patch event handlers ──────────────────────────────────────

    private async void BrowsePatchFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.BrowsePatchFileCommand.ExecuteAsync(null);

            // 根据文件扩展名自动识别并显示徽章和文件路径
            if (!string.IsNullOrEmpty(ViewModel.PatchFilePath))
            {
                // 显示文件大小和路径
                var fileInfo = new System.IO.FileInfo(ViewModel.PatchFilePath);
                string fileSize = fileInfo.Length > 1024 * 1024
                    ? $"{fileInfo.Length / (1024.0 * 1024.0):F2} MB"
                    : $"{fileInfo.Length / 1024.0:F2} KB";

                PatchFileNameLabel.Text = $"{ViewModel.PatchFileName} ({fileSize})";
                PatchFileNameLabel.Foreground = Application.Current.Resources["TextFillColorPrimaryBrush"] as Microsoft.UI.Xaml.Media.Brush;

                // 识别文件类型并显示徽章
                string extension = System.IO.Path.GetExtension(ViewModel.PatchFilePath).ToLowerInvariant();
                string detectedType = extension switch
                {
                    ".apk" => "Magisk",
                    ".zip" => "AK3",
                    _ => "未知"
                };

                ViewModel.PatchType = extension switch
                {
                    ".apk" => "magisk",
                    ".zip" => "ak3",
                    _ => ""
                };

                PatchTypeBadgeText.Text = detectedType;
                PatchTypeBadge.Visibility = detectedType != "未知" ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private async void BrowsePatchImage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.BrowsePatchImageCommand.ExecuteAsync(null);

            // 显示文件大小和路径
            if (!string.IsNullOrEmpty(ViewModel.PatchImageFilePath))
            {
                var fileInfo = new System.IO.FileInfo(ViewModel.PatchImageFilePath);
                string fileSize = fileInfo.Length > 1024 * 1024
                    ? $"{fileInfo.Length / (1024.0 * 1024.0):F2} MB"
                    : $"{fileInfo.Length / 1024.0:F2} KB";

                PatchImageFileNameLabel.Text = $"{ViewModel.PatchImageFileName} ({fileSize})";
                PatchImageFileNameLabel.Foreground = Application.Current.Resources["TextFillColorPrimaryBrush"] as Microsoft.UI.Xaml.Media.Brush;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private async void Patch_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.PatchCommand.ExecuteAsync(null);

            // 修补完成后显示结果面板
            if (!string.IsNullOrEmpty(ViewModel.PatchOutputPath))
            {
                PatchResultBorder.Visibility = Visibility.Visible;
                PatchResultImageLabel.Text = $"输出文件: {Path.GetFileName(ViewModel.PatchOutputPath)}";

                // 根据是否检测到设备显示不同的按钮
                bool hasDevice = !string.IsNullOrWhiteSpace(ViewModel.PatchSelectedDevice);
                FlashPatchedBtn.Visibility = hasDevice ? Visibility.Visible : Visibility.Collapsed;
                OpenPatchFolderBtn.Visibility = hasDevice ? Visibility.Collapsed : Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private async void FlashPatchedImage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 使用修补后的镜像文件刷入对应的分区
            if (!string.IsNullOrEmpty(ViewModel.PatchOutputPath))
            {
                // 切换到快速刷入模式，设置镜像文件和分区
                ViewModel.ImagePath = ViewModel.PatchOutputPath;
                ViewModel.ImageFileName = System.IO.Path.GetFileName(ViewModel.PatchOutputPath);

                // 用原始镜像文件名推断分区（boot.img -> boot, init_boot.img -> init_boot）
                string originalName = System.IO.Path.GetFileNameWithoutExtension(ViewModel.PatchImageFilePath)
                    .ToLowerInvariant();
                string partition = originalName switch
                {
                    "init_boot" => "init_boot",
                    "vendor_boot" => "vendor_boot",
                    "vbmeta" => "vbmeta",
                    _ => "boot" // 默认 boot
                };
                ViewModel.SelectedPartition = partition;

                // 更新日志
                ViewModel.AppendLog($"[补修] 快速刷入: {partition} ({Path.GetFileName(ViewModel.PatchOutputPath)})");

                // 切到 patch-flow 模式：隐藏「修补完成」卡片，显示「刷入中」卡片
                PatchResultBorder.Visibility    = Visibility.Collapsed;
                PatchFlashBorder.Visibility     = Visibility.Visible;
                PatchFlashPartitionLabel.Text  = $"分区：{partition}    镜像：{Path.GetFileName(ViewModel.PatchOutputPath)}";
                _isPatchFlowActive             = true;

                // 自动触发刷写
                await Task.Delay(200); // 让日志/进度有机会刷新
                await ViewModel.FlashCommand.ExecuteAsync(null);

                // 刷写成功后弹出重启确认
                if (ViewModel.StatusText.StartsWith("[OK]", StringComparison.OrdinalIgnoreCase))
                {
                    var dialog = new ContentDialog
                    {
                        XamlRoot          = Content.XamlRoot,
                        Title             = "刷写完成",
                        Content           = "修补后的镜像已成功刷入设备。\n是否立即重启设备到系统？",
                        PrimaryButtonText = "重启设备",
                        CloseButtonText   = "暂不重启",
                        DefaultButton     = ContentDialogButton.Primary
                    };

                    if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                        await ViewModel.RebootToSystemCommand.ExecuteAsync(null);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private async void OpenPatchOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrEmpty(ViewModel.PatchOutputDirectory) && System.IO.Directory.Exists(ViewModel.PatchOutputDirectory))
            {
                await Windows.System.Launcher.LaunchFolderPathAsync(ViewModel.PatchOutputDirectory);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    /// <summary>页面加载时从 ViewModel 恢复 UI 标签（VS 重建后标签会丢失但 ViewModel 缓存还在）</summary>
    private void RestoreUiState()
    {
        // 快速刷入 — 镜像路径
        if (!string.IsNullOrEmpty(ViewModel.ImagePath))
        {
            if (System.IO.File.Exists(ViewModel.ImagePath))
            {
                var info = new System.IO.FileInfo(ViewModel.ImagePath);
                string size = info.Length > 1024 * 1024
                    ? $"{info.Length / (1024.0 * 1024.0):F2} MB"
                    : $"{info.Length / 1024.0:F2} KB";
                FileNameTextBox.Text = $"{ViewModel.ImagePath}  ({size})";
            }
            else
            {
                FileNameTextBox.Text = ViewModel.ImagePath;
            }
            FileNameTextBox.Foreground = Application.Current.Resources["TextFillColorPrimaryBrush"] as Microsoft.UI.Xaml.Media.Brush;
        }

        // 一键修补 — 工具文件 + 徽章
        if (!string.IsNullOrEmpty(ViewModel.PatchFilePath))
        {
            string label;
            if (System.IO.File.Exists(ViewModel.PatchFilePath))
            {
                var info = new System.IO.FileInfo(ViewModel.PatchFilePath);
                string size = info.Length > 1024 * 1024
                    ? $"{info.Length / (1024.0 * 1024.0):F2} MB"
                    : $"{info.Length / 1024.0:F2} KB";
                label = $"{ViewModel.PatchFileName} ({size})";
            }
            else
            {
                label = ViewModel.PatchFileName;
            }
            PatchFileNameLabel.Text = label;
            PatchFileNameLabel.Foreground = Application.Current.Resources["TextFillColorPrimaryBrush"] as Microsoft.UI.Xaml.Media.Brush;

            // 类型徽章
            string badge = ViewModel.PatchType switch
            {
                "magisk" => "Magisk",
                "ak3" => "AK3",
                _ => ""
            };
            if (!string.IsNullOrEmpty(badge))
            {
                PatchTypeBadgeText.Text = badge;
                PatchTypeBadge.Visibility = Visibility.Visible;
            }
        }

        // 一键修补 — 目标镜像
        if (!string.IsNullOrEmpty(ViewModel.PatchImageFilePath))
        {
            string label;
            if (System.IO.File.Exists(ViewModel.PatchImageFilePath))
            {
                var info = new System.IO.FileInfo(ViewModel.PatchImageFilePath);
                string size = info.Length > 1024 * 1024
                    ? $"{info.Length / (1024.0 * 1024.0):F2} MB"
                    : $"{info.Length / 1024.0:F2} KB";
                label = $"{ViewModel.PatchImageFileName} ({size})";
            }
            else
            {
                label = ViewModel.PatchImageFileName;
            }
            PatchImageFileNameLabel.Text = label;
            PatchImageFileNameLabel.Foreground = Application.Current.Resources["TextFillColorPrimaryBrush"] as Microsoft.UI.Xaml.Media.Brush;
        }

        // 日志内容（ViewModel 缓存了，新 Page 的 TextBlock 需要手动恢复）
        if (!string.IsNullOrEmpty(ViewModel.LogText))
        {
            LogTextBlock.Text = ViewModel.LogText;
            LogScroll.ChangeView(null, double.MaxValue, null);
        }
    }
}
