using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SailTool.Models;
using SailTool.Services;

namespace SailTool.ViewModels;

public sealed partial class HomePageViewModel(FastbootService fastboot) : ObservableObject
{
    // ── 连接状态 ────────────────────────────────────────────────
    [ObservableProperty]
    public partial bool IsConnected { get; set; }

    [ObservableProperty]
    public partial string StatusDetail { get; set; } = "请连接设备并进入 ADB 或 Fastboot 模式";

    // ── 设备列表 ────────────────────────────────────────────────
    [ObservableProperty]
    public partial List<DeviceInfo> Devices { get; set; } = [];

    /// <summary>
    /// The device the user currently has selected (TwoWay bound from the list).
    /// The MVVM toolkit generates an <c>OnSelectedDeviceChanged</c> partial method,
    /// called after assignment, which kicks off the asynchronous detail refresh.
    /// </summary>
    [ObservableProperty]
    public partial DeviceInfo? SelectedDevice { get; set; }

    // 当 SelectedDevice 改变时自动触发 (CommunityToolkit.Mvvm 自动生成的 partial hook)
    partial void OnSelectedDeviceChanged(DeviceInfo? value)
    {
        if (value is null)
        {
            CurrentDetailedDevice = null;
            return;
        }

        // 异步拉取详细信息；不阻塞 UI 线程
        _ = RefreshDetailForCurrentSelectionAsync();
    }

    // ── UI 状态 ─────────────────────────────────────────────────
    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    // ── 详细信息网格 ────────────────────────────────────────────
    [ObservableProperty]
    public partial DetailedDeviceInfo? CurrentDetailedDevice { get; set; }

    // ── 刷新设备列表 ────────────────────────────────────────────
    [RelayCommand]
    private async Task RefreshDevicesAsync()
    {
        // 注意:不设置 IsLoading=true,避免自动刷新时转圈动画反复闪烁
        ErrorMessage = string.Empty;

        try
        {
            var devices = await fastboot.GetConnectedDevicesAsync();

            // ── 仅在列表内容真实变化时才更新 Devices(避免 ComboBox ItemsSource 重绑闪烁)──
            if (!DeviceListsEqual(Devices, devices))
            {
                Devices     = devices;
                IsConnected = devices.Count > 0;
            }
            else
            {
                IsConnected = devices.Count > 0;
            }

            if (!IsConnected)
            {
                ResetDeviceState();
                return;
            }

            // 保留用户已选设备：若仍在列表中则保留,否则回退到第一个
            DeviceInfo target;
            if (SelectedDevice is { } current)
            {
                target = devices.FirstOrDefault(d => d.Serial == current.Serial) ?? devices[0];
            }
            else
            {
                target = devices[0];
            }

            // 只有目标设备本身发生变化(新引用 / 不同 Serial / Mode/Connection 改变)才赋值
            if (!DeviceInfoEquals(SelectedDevice, target))
            {
                SelectedDevice = target;
            }

            // StatusDetail 同样只在内容变化时赋值
            var newStatusDetail = devices.Count > 1
                ? $"已检测到 {devices.Count} 台设备,当前:{target.Serial}"
                : $"设备 {target.Serial} ({target.ConnectionDisplay})";
            if (StatusDetail != newStatusDetail)
                StatusDetail = newStatusDetail;
        }
        catch (Exception ex)
        {
            ErrorMessage          = ex.Message;
            IsConnected           = false;
            StatusDetail          = "请检查 fastboot / adb 路径";
            CurrentDetailedDevice = null;
        }
    }

    /// <summary>Compares two device lists by content, on Serial, Mode and Connection.</summary>
    private static bool DeviceListsEqual(List<DeviceInfo> a, List<DeviceInfo> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Serial     != b[i].Serial     ||
                a[i].Mode       != b[i].Mode       ||
                a[i].Connection != b[i].Connection)
                return false;
        }
        return true;
    }

    /// <summary>Whether two references describe the same device in the same state.</summary>
    private static bool DeviceInfoEquals(DeviceInfo? a, DeviceInfo? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return a.Serial     == b.Serial     &&
               a.Mode       == b.Mode       &&
               a.Connection == b.Connection;
    }

    // ── 拉取当前选中设备的详细信息 ─────────────────────────────
    private async Task RefreshDetailForCurrentSelectionAsync()
    {
        if (SelectedDevice is not { } device) return;

        try
        {
            CurrentDetailedDevice = await fastboot.GetDetailedInfoAsync(device);
        }
        catch
        {
            // Fallback keeps the old behaviour: DetectionType shows the raw
            // connection ("Fastboot" / "ADB") when detail collection threw.
            CurrentDetailedDevice = DetailedDeviceInfo.Empty(device.Serial, device.ConnectionDisplay);
        }
    }

    [RelayCommand]
    private void Disconnect() => ResetDeviceState();

    private void ResetDeviceState()
    {
        IsConnected           = false;
        StatusDetail          = "请连接设备并进入 ADB 或 Fastboot 模式";
        CurrentDetailedDevice = null;
        SelectedDevice        = null;
    }
}
