using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SailTool.ViewModels;

public sealed partial class SettingsPageViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string FastbootPath { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "platform-tools", "fastboot.exe");

    [ObservableProperty]
    public partial string AppVersion { get; set; } = ReadAssemblyVersion();

    [ObservableProperty]
    public partial string DeployMode { get; set; } = DetectDeployMode();

    /// <summary>
    /// Reads the version stamped into the assembly by MSBuild from the csproj
    /// &lt;Version&gt; and &lt;VersionSuffix&gt; properties, so the number shown here
    /// is the same one the installer puts in its artifact filename.
    /// Falls back to the file version, then to "unknown" if neither is present.
    /// </summary>
    private static string ReadAssemblyVersion()
    {
        var assembly = typeof(SettingsPageViewModel).Assembly;

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Strip any "+buildmetadata" suffix if one is ever reintroduced.
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }

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
