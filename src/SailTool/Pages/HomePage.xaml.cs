using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SailTool.Helpers;
using SailTool.Models;
using SailTool.Services;
using SailTool.ViewModels;
using Color = Windows.UI.Color;

namespace SailTool.Pages;

public sealed partial class HomePage : Page
{
    public HomePageViewModel ViewModel { get; }

    // Auto-detect polling timer
    private DispatcherTimer? _autoTimer;
    private volatile bool _isRefreshing; // prevents overlapping refreshes

    // Polling intervals (ms)
    private const int FastPoll = 3000;  // no device: poll every 3s
    private const int SlowPoll = 5000;  // device connected: poll every 5s

    public HomePage()
    {
        ViewModel = FastbootServiceAccessor.CreateHomeViewModel();
        InitializeComponent();

        Loaded   += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // ── Helper for x:Bind (extracts a property from nullable DetailedDeviceInfo) ──
    // The field-name literals come from HomePage.xaml; 'ConnectionType' and
    // 'BootloaderStatus' now read the renamed display properties so the XAML
    // binding expressions stay byte-identical.
    public string DisplayField(DetailedDeviceInfo? info, string fieldName)
    {
        if (info is null) return "--";
        return fieldName switch
        {
            "ConnectionType"   => info.DetectionType,
            "DeviceCodename"   => info.DeviceCodename,
            "Brand"            => info.Brand,
            "Model"            => info.Model,
            "BootloaderStatus" => info.BootloaderDisplay,
            "Slot"             => info.Slot,
            "AndroidVersion"   => info.AndroidVersion,
            "SystemVersion"    => info.SystemVersion,
            "KernelVersion"    => info.KernelVersion,
            _ => "--"
        };
    }

    // ── Lifecycle ──────────────────────────────────────────────────

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunDetectAsync(isManual: true);

            _autoTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(FastPoll)
            };
            _autoTimer.Tick += async (_, _) =>
            {
                try
                {
                    await AutoTickAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex);
                }
            };
            _autoTimer.Start();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _autoTimer?.Stop();
        _autoTimer = null;
    }

    // ── Auto-polling ──────────────────────────────────────────────

    private async Task AutoTickAsync()
    {
        if (_isRefreshing) return;

        await RunDetectAsync(isManual: false);

        if (_autoTimer is { } timer)
        {
            var hasDevice = ViewModel.Devices is { Count: > 0 };
            var newInterval = TimeSpan.FromMilliseconds(hasDevice ? SlowPoll : FastPoll);
            if (timer.Interval != newInterval)
                timer.Interval = newInterval;
        }
    }

    // ── Manual detect button ──────────────────────────────────────

    private async void DetectButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunDetectAsync(isManual: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    // ── Core detect routine ───────────────────────────────────────

    private async Task RunDetectAsync(bool isManual)
    {
        if (_isRefreshing) return;
        _isRefreshing = true;

        if (isManual)
        {
            LoadingRing.IsActive = true;
            ErrorLabel.Text = string.Empty;
        }

        // 记录旧的详情引用,用于判断是否需要触发 Bindings.Update
        var previousDetail = ViewModel.CurrentDetailedDevice;

        try
        {
            await ViewModel.RefreshDevicesCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            if (isManual) ErrorLabel.Text = ex.Message;
        }
        finally
        {
            _isRefreshing = false;
        }

        SyncUi();

        // 仅当 CurrentDetailedDevice 引用改变时,才强制所有 DisplayField 函数重算
        // 避免每次自动刷新都让信息网格的文本重新渲染(闪烁根因之一)
        if (!ReferenceEquals(previousDetail, ViewModel.CurrentDetailedDevice))
        {
            Bindings.Update();
        }

        LoadingRing.IsActive = false;
    }

    // ── UI sync ───────────────────────────────────────────────────

    private static readonly Color Green  = Color.FromArgb(0xFF, 0x4C, 0xAF, 0x50);
    private static readonly Color Blue   = Color.FromArgb(0xFF, 0x21, 0x96, 0xF3);
    private static readonly Color GreenBg = Color.FromArgb(0x30, 0x4C, 0xAF, 0x50);
    private static readonly Color BlueBg  = Color.FromArgb(0x30, 0x21, 0x96, 0xF3);
    private static readonly Color RedBg   = Color.FromArgb(0x30, 0xFF, 0x52, 0x52);

    /// <summary>Assigns TextBlock.Text only when it actually changes, so repeated renders
    /// do not flicker.</summary>
    private static void SetText(TextBlock tb, string text)
    {
        if (tb.Text != text) tb.Text = text;
    }

    /// <summary>Assigns SolidColorBrush.Color only when it actually changes.</summary>
    private static void SetColor(SolidColorBrush brush, Color color)
    {
        if (brush.Color != color) brush.Color = color;
    }

    /// <summary>Assigns Visibility only when it actually changes.</summary>
    private static void SetVisibility(UIElement el, Visibility visibility)
    {
        if (el.Visibility != visibility) el.Visibility = visibility;
    }

    private void SyncUi()
    {
        var devices = ViewModel.Devices;

        if (devices is { Count: > 0 })
        {
            var primary = devices[0];
            bool isFb   = primary.IsFastboot;

            SetVisibility(ConnectedDot, Visibility.Visible);
            SetVisibility(DisconnDot,   Visibility.Collapsed);
            SetColor(ConnectedFill, isFb ? Green : Blue);
            SetColor(IndicatorBg,   isFb ? GreenBg : BlueBg);

            SetText(PrimaryStatusLabel,   isFb ? "Fastboot 已连接" : "ADB 已连接");
            SetText(SecondaryStatusLabel, devices.Count > 1
                ? $"已自动检测到 {devices.Count} 台设备"
                : $"设备 {primary.Serial} ({primary.ConnectionDisplay})");
        }
        else
        {
            SetVisibility(ConnectedDot, Visibility.Collapsed);
            SetVisibility(DisconnDot,   Visibility.Visible);
            SetColor(IndicatorBg, RedBg);

            SetText(PrimaryStatusLabel,   "未检测到设备");
            SetText(SecondaryStatusLabel, "等待设备连接，将自动检测");
        }

        // Error
        if (!string.IsNullOrEmpty(ViewModel.ErrorMessage) && ErrorLabel.Text == string.Empty)
        {
            SetText(SecondaryStatusLabel, ViewModel.ErrorMessage);
        }

        // 说明:不再在 SyncUi 内无条件调用 Bindings.Update()
        // 改为由 RunDetectAsync 在 CurrentDetailedDevice 真正变化时才调用,避免闪烁
    }

    // ── 驱动安装 ──────────────────────────────────────────────────

    private async void InstallDriver_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var filePath = await FilePickerHelper.PickFileAsync(App.Window, new[] { ".exe" });
            if (filePath is null) return; // user cancelled

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                await DialogHelper.ShowErrorAsync(this.XamlRoot, "启动失败", ex.Message);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    // ── 快速重启 ──────────────────────────────────────────────────

    private async void QuickReboot_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not Button btn || btn.Tag is not string mode)
                return;

            // 检查是否有设备连接
            var devices = ViewModel.Devices;
            if (devices is null || devices.Count == 0)
            {
                await ShowErrorDialog("没有检测到设备", "请先连接设备后再执行重启操作");
                return;
            }

            var device = devices[0]; // 使用第一个设备
            var fastbootService = FastbootServiceAccessor.Instance;

            try
            {
                // Tag 现在直接是命令名称：system, recovery, bootloader, fastbootd, shutdown, edl
                var success = await RebootToDeviceAsync(device, mode, fastbootService);

                if (!success)
                {
                    await ShowErrorDialog("重启失败", $"无法执行 reboot {mode} 命令");
                }
            }
            catch (Exception ex)
            {
                await ShowErrorDialog("重启失败", ex.Message);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private async Task<bool> RebootToDeviceAsync(DeviceInfo device, string mode, FastbootService service)
    {
        // 根据设备当前状态选择合适的工具
        if (device.IsAdb)
        {
            // 设备在 ADB 模式
            var (exitCode, output) = await service.RunAdbAsync($"reboot {mode}");
            return exitCode == 0;
        }
        else if (device.IsFastboot)
        {
            // 设备在 Fastboot 模式
            var output = await service.RunFastbootAsync($"reboot {mode}");
            // Fastboot 命令通常通过输出中的 "OKAY" 或无错误输出来判断成功
            return !output.Contains("FAILED", StringComparison.OrdinalIgnoreCase) &&
                   !output.Contains("error", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private async Task ShowErrorDialog(string title, string content)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = "确定",
            XamlRoot = this.XamlRoot
        };
        await dialog.ShowAsync();
    }
}
