using CommunityToolkit.Mvvm.ComponentModel;

namespace SailTool.ViewModels;

public sealed partial class SettingsPageViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string FastbootPath { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "platform-tools", "fastboot.exe");

    [ObservableProperty]
    public partial string AppVersion { get; set; } = "1.0.0-preview";

    [ObservableProperty]
    public partial string DeployMode { get; set; } = DetectDeployMode();

    private static string DetectDeployMode()
    {
        // 检测 Inno Setup 生成的卸载程序
        var uninsPath = Path.Combine(AppContext.BaseDirectory, "unins000.exe");
        if (File.Exists(uninsPath))
        {
            return "安装版 (Windows App)";
        }

        // 检测是否在 Program Files 目录下
        var basePath = AppContext.BaseDirectory;
        if (basePath.Contains("Program Files", StringComparison.OrdinalIgnoreCase))
        {
            return "安装版 (Windows App)";
        }

        // 否则为便携版
        return "便携版 (Portable)";
    }
}
