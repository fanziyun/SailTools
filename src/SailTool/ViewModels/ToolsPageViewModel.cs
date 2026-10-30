using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using FastTool.Services;

namespace FastTool.ViewModels;

public sealed partial class ToolsPageViewModel(FastbootService fastboot) : ObservableObject
{
    [ObservableProperty]
    public partial string Title { get; set; } = "高级工具";

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusSeverity { get; set; } = "Informational";

    [ObservableProperty]
    public partial InfoBarSeverity StatusSeverityKind { get; set; } = InfoBarSeverity.Informational;

    [ObservableProperty]
    public partial bool IsStatusVisible { get; set; }

    [ObservableProperty]
    public partial string CommandOutput { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Visibility HasOutput { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial string ErasePartition { get; set; } = "userdata";

    [ObservableProperty]
    public partial string CustomCommand { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    /// <summary>
    /// UI 层注入的确认解锁回调。
    /// 参数为标题和内容，返回值表示用户是否确认。
    /// </summary>
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    // ── Helper: run a command and update status ───────────────────
    private async Task RunCommandAsync(string displayName, string args)
    {
        IsRunning = true;
        IsStatusVisible = true;
        HasOutput = Visibility.Collapsed;
        StatusMessage = $"正在执行 {displayName}...";
        StatusSeverity = "执行中";
        StatusSeverityKind = InfoBarSeverity.Informational;
        CommandOutput = string.Empty;

        try
        {
            var output = await fastboot.RunFastbootAsync(args);
            CommandOutput = string.IsNullOrWhiteSpace(output) ? "(无输出)" : output.Trim();
            StatusMessage = $"{displayName} 执行完成";
            StatusSeverity = "成功";
            StatusSeverityKind = InfoBarSeverity.Success;
            HasOutput = Visibility.Visible;
        }
        catch (Exception ex)
        {
            StatusMessage = $"{displayName} 失败: {ex.Message}";
            StatusSeverity = "失败";
            StatusSeverityKind = InfoBarSeverity.Error;
        }
        finally
        {
            IsRunning = false;
        }
    }

    // ── Commands ──────────────────────────────────────────────────

    public async Task OnRebootAsync()
        => await RunCommandAsync("Reboot", "reboot");

    public async Task OnRebootRecoveryAsync()
        => await RunCommandAsync("Reboot Recovery", "reboot recovery");

    public async Task OnRebootBootloaderAsync()
        => await RunCommandAsync("Reboot Bootloader", "reboot bootloader");

    public async Task OnEraseAsync()
    {
        if (string.IsNullOrWhiteSpace(ErasePartition))
        {
            IsStatusVisible = true;
            StatusMessage = "请输入要擦除的分区名称";
            StatusSeverity = "警告";
            StatusSeverityKind = InfoBarSeverity.Warning;
            return;
        }
        await RunCommandAsync($"Erase {ErasePartition}", $"erase {ErasePartition}");
    }

    public async Task OnUnlockAsync()
    {
        if (ConfirmAsync is not null)
        {
            var confirmed = await ConfirmAsync("确认解锁 Bootloader", "此操作将解锁设备的 Bootloader，可能导致数据丢失。确认继续吗？");
            if (!confirmed) return;
        }

        await RunCommandAsync("Unlock OEM", "flashing unlock");
    }

    public async Task OnGetVarAsync()
        => await RunCommandAsync("GetVar all", "getvar all");

    public async Task OnCustomCommandAsync()
    {
        if (string.IsNullOrWhiteSpace(CustomCommand))
        {
            IsStatusVisible = true;
            StatusMessage = "请输入要执行的 fastboot 命令";
            StatusSeverity = "警告";
            StatusSeverityKind = InfoBarSeverity.Warning;
            return;
        }
        await RunCommandAsync($"Custom: {CustomCommand}", CustomCommand.Trim());
    }

    /// <summary>
    /// 一键宽容模式：依次执行
    ///   1. oem set-gpu-preemption 0 androidboot.selinux=permissive
    ///   2. continue
    /// 第一条失败则终止（避免设备在未设置好时直接重启）。
    /// </summary>
    public async Task OnPermissiveModeAsync()
    {
        IsRunning = true;
        IsStatusVisible = true;
        HasOutput = Visibility.Collapsed;
        CommandOutput = string.Empty;
        StatusMessage = "一键宽容 (1/2): 设置 GPU 抢占 + SELinux permissive...";
        StatusSeverity = "执行中";
        StatusSeverityKind = InfoBarSeverity.Informational;

        try
        {
            // Step 1: oem set-gpu-preemption + selinux permissive
            var output1 = await fastboot.RunFastbootAsync(
                "oem set-gpu-preemption 0 androidboot.selinux=permissive");
            CommandOutput = $"$ fastboot oem set-gpu-preemption 0 androidboot.selinux=permissive\n"
                          + (string.IsNullOrWhiteSpace(output1) ? "(无输出)" : output1.Trim()) + "\n\n";

            if (output1.Contains("FAILED", StringComparison.OrdinalIgnoreCase) ||
                output1.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                StatusMessage = "一键宽容失败：oem 命令执行出错";
                StatusSeverity = "失败";
                StatusSeverityKind = InfoBarSeverity.Error;
                HasOutput = Visibility.Visible;
                return;
            }

            // Step 2: fastboot continue
            StatusMessage = "一键宽容 (2/2): 重启设备...";
            var output2 = await fastboot.RunFastbootAsync("continue");
            CommandOutput += $"$ fastboot continue\n"
                           + (string.IsNullOrWhiteSpace(output2) ? "(无输出)" : output2.Trim());

            StatusMessage = "一键宽容完成：设备已重启进入宽容模式";
            StatusSeverity = "成功";
            StatusSeverityKind = InfoBarSeverity.Success;
            HasOutput = Visibility.Visible;
        }
        catch (Exception ex)
        {
            StatusMessage = $"一键宽容失败: {ex.Message}";
            StatusSeverity = "失败";
            StatusSeverityKind = InfoBarSeverity.Error;
        }
        finally
        {
            IsRunning = false;
        }
    }
}
