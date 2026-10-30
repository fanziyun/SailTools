using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SailTool.Services;
using SailTool.ViewModels;

namespace SailTool.Pages;

public sealed partial class ROMFlashPage : Page
{
    private readonly ROMFlashPageViewModel ViewModel;

    public ROMFlashPage()
    {
        ViewModel = FastbootServiceAccessor.CreateROMFlashViewModel();
        InitializeComponent();
        DataContext = ViewModel;

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;

        // 注入重启确认弹窗回调
        ViewModel.ConfirmRebootAsync = ConfirmRebootDialogAsync;

        // 从缓存的 ViewModel 恢复 UI 状态（切页面后再回来）
        Loaded += (_, _) => RestoreUiState();
    }

    private void ViewModel_PropertyChanged(object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ROMFlashPageViewModel.LogText):
                LogTextBlock.Text = ViewModel.LogText;
                LogScroll.ChangeView(null, double.MaxValue, null);
                break;

            case nameof(ROMFlashPageViewModel.StatusText):
                StatusLabel.Text = ViewModel.StatusText;
                break;

            case nameof(ROMFlashPageViewModel.IsFlashing):
                ApplyFlashingState(ViewModel.IsFlashing);
                if (ViewModel.IsFlashing)
                {
                    ProgressRow.Visibility      = Visibility.Visible;
                    RomIndeterminateBar.Visibility = Visibility.Visible;
                    RomExactBar.Visibility         = Visibility.Collapsed;
                    PercentLabel.Text              = "";
                    CurrentPartitionLabel.Text     = "";
                }
                else
                {
                    ProgressRow.Visibility      = Visibility.Collapsed;
                    RomIndeterminateBar.Visibility = Visibility.Collapsed;
                    RomExactBar.Visibility         = Visibility.Collapsed;
                    PercentLabel.Text              = "";
                }
                break;

            case nameof(ROMFlashPageViewModel.CurrentPartitionName):
                CurrentPartitionLabel.Text = ViewModel.CurrentPartitionName;
                break;

            case nameof(ROMFlashPageViewModel.OverallProgress):
                var pct = ViewModel.OverallProgress;
                ProgressRow.Visibility      = Visibility.Visible;
                if (ViewModel.HasExactProgress)
                {
                    RomExactBar.Value          = pct;
                    RomExactBar.Visibility     = Visibility.Visible;
                    RomIndeterminateBar.Visibility = Visibility.Collapsed;
                    PercentLabel.Text          = $"{pct}%";
                }
                else
                {
                    RomIndeterminateBar.Visibility = Visibility.Visible;
                    RomExactBar.Visibility     = Visibility.Collapsed;
                    PercentLabel.Text          = "";
                }
                break;

            case nameof(ROMFlashPageViewModel.FolderPath):
            case nameof(ROMFlashPageViewModel.HasFlashAll):
            case nameof(ROMFlashPageViewModel.HasFlashExceptStorage):
            case nameof(ROMFlashPageViewModel.HasFlashLock):
                ApplyFlashingState(ViewModel.IsFlashing);
                break;
        }
    }

    /// <summary>Refreshes button and spinner state from IsFlashing in one place, shared by
    /// PropertyChanged and RestoreUiState.</summary>
    private void ApplyFlashingState(bool isFlashing)
    {
        var hasBat = ViewModel.HasFlashAll
                  || ViewModel.HasFlashExceptStorage
                  || ViewModel.HasFlashLock;

        StartFlashBtn.IsEnabled  = !isFlashing && !string.IsNullOrEmpty(ViewModel.FolderPath) && hasBat;
        BrowseFolderBtn.IsEnabled = !isFlashing;
        CancelFlashBtn.IsEnabled  =  isFlashing;
        CancelFlashBtn.Visibility =  isFlashing ? Visibility.Visible : Visibility.Collapsed;
        FlashRing.IsActive        =  isFlashing;
    }

    // ── RadioButton Checked → 更新 SelectedBatFile ─────────────

    private void RadioFlashAll_Checked(object sender, RoutedEventArgs e)
        => ViewModel.SelectedBatFile = "flash_all.bat";

    private void RadioFlashExceptStorage_Checked(object sender, RoutedEventArgs e)
        => ViewModel.SelectedBatFile = "flash_all_except_storage.bat";

    private void RadioFlashLock_Checked(object sender, RoutedEventArgs e)
        => ViewModel.SelectedBatFile = "flash_all_lock.bat";

    // ── 事件处理 ──────────────────────────────────────────────

    private async void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.BrowseFolderCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private async void StartFlash_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 开始刷机前强提醒确认
            var dialog = new ContentDialog
            {
                XamlRoot          = Content.XamlRoot,
                Title             = "确认开始线刷",
                Content           = $"即将执行：{ViewModel.SelectedBatFile}\n"
                                    + $"刷包路径：{ViewModel.FolderPath}\n\n"
                                    + "线刷过程中请勿断开设备连接，确认开始吗？",
                PrimaryButtonText = "确认开始",
                CloseButtonText   = "取消",
                DefaultButton     = ContentDialogButton.Close
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            await ViewModel.StartFlashCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private void CancelFlash_Click(object sender, RoutedEventArgs e)
        => ViewModel.CancelFlashCommand.Execute(null);

    // ── 重启确认弹窗（ViewModel 通过委托回调） ─────────────────

    private async Task<bool> ConfirmRebootDialogAsync(string modeLabel)
    {
        var dialog = new ContentDialog
        {
            XamlRoot          = Content.XamlRoot,
            Title             = "设备需要重启",
            Content           = $"设备当前处于 [{modeLabel}] 模式，\n"
                                + "需要重启到 Bootloader (Fastboot) 模式后才能执行线刷。\n\n"
                                + "确认重启吗？",
            PrimaryButtonText = "确认重启",
            CloseButtonText   = "取消",
            DefaultButton     = ContentDialogButton.Primary
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    // ── 页面切换后恢复 UI 状态 ─────────────────────────────────

    /// <summary>Restores UI state after navigating back to this page: the ViewModel is
    /// cached, but a new Page instance has to render it again.</summary>
    private void RestoreUiState()
    {
        // 路径
        FolderPathLabel.Text = ViewModel.FolderDisplayText;

        // 日志
        if (!string.IsNullOrEmpty(ViewModel.LogText))
        {
            LogTextBlock.Text = ViewModel.LogText;
            LogScroll.ChangeView(null, double.MaxValue, null);
        }

        // 恢复 RadioButton 选中状态
        RadioFlashAll.IsChecked           = ViewModel.SelectedBatFile == "flash_all.bat"                ? true : null;
        RadioFlashExceptStorage.IsChecked = ViewModel.SelectedBatFile == "flash_all_except_storage.bat" ? true : null;
        RadioFlashLock.IsChecked          = ViewModel.SelectedBatFile == "flash_all_lock.bat"           ? true : null;

        // 状态文本
        StatusLabel.Text = ViewModel.StatusText;

        // 按钮 + 转圈动画（解决切页后 ProgressRing 消失的问题）
        ApplyFlashingState(ViewModel.IsFlashing);

        // 恢复进度行（若仍在刷写中）
        if (ViewModel.IsFlashing)
        {
            ProgressRow.Visibility            = Visibility.Visible;
            CurrentPartitionLabel.Text        = ViewModel.CurrentPartitionName;
            RomIndeterminateBar.Visibility    = ViewModel.HasExactProgress ? Visibility.Collapsed : Visibility.Visible;
            RomExactBar.Visibility            = ViewModel.HasExactProgress ? Visibility.Visible   : Visibility.Collapsed;
            RomExactBar.Value                 = ViewModel.OverallProgress;
            PercentLabel.Text                 = ViewModel.HasExactProgress ? $"{ViewModel.OverallProgress}%" : "";
        }
        else
        {
            ProgressRow.Visibility = Visibility.Collapsed;
        }
    }
}
