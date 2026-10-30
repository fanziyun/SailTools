using System.Diagnostics;
using System.Text.Json;
using SailTool.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SailTool;

public partial class App : Application
{
    private static readonly string CrashLogPath =
        Path.Combine(AppContext.BaseDirectory, "crash.log");

    private static readonly string ThemeSettingsPath =
        Path.Combine(AppContext.BaseDirectory, "theme-settings.json");

    private const string ThemeSettingKey = "AppTheme";

    public static Window Window { get; set; } = null!;

    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            // 单文件发布时需要设置此环境变量
            Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", AppContext.BaseDirectory);
            
            Window = new MainWindow();
            DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            Window.Activate();
            
            // 应用上次保存的主题
            var saved = GetSavedTheme();
            if (saved != ElementTheme.Default)
            {
                ApplyTheme(saved);
            }
            
            Window.Closed += OnWindowClosed;
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
            throw;
        }
    }

    // ── Theme management ──────────────────────────────────────────

    /// <summary>Reads the theme setting saved by the previous run.</summary>
    public static ElementTheme GetSavedTheme()
    {
        try
        {
            if (File.Exists(ThemeSettingsPath))
            {
                var json = File.ReadAllText(ThemeSettingsPath);
                var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (settings?.TryGetValue(ThemeSettingKey, out var value) == true)
                {
                    return value switch
                    {
                        "Light" => ElementTheme.Light,
                        "Dark"  => ElementTheme.Dark,
                        _       => ElementTheme.Default
                    };
                }
            }
        }
        catch { /* file corruption or access error */ }
        return ElementTheme.Default;
    }

    /// <summary>Applies the theme and persists it to local storage.</summary>
    public static void ApplyTheme(ElementTheme theme)
    {
        ApplyThemeInternal(theme);
        SaveThemeSettings(theme);
    }

    private static void ApplyThemeInternal(ElementTheme theme)
    {
        if (Window?.Content is FrameworkElement root)
        {
            root.RequestedTheme = theme;
        }
    }

    private static void SaveThemeSettings(ElementTheme theme)
    {
        try
        {
            var settings = new Dictionary<string, string>
            {
                [ThemeSettingKey] = theme.ToString()
            };
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ThemeSettingsPath, json);
        }
        catch { /* non-critical */ }
    }

    // ── Crash handling ────────────────────────────────────────────

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        try { FastbootServiceAccessor.Instance.Dispose(); }
        catch { /* best effort during shutdown */ }
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception ?? new Exception(e.Message));
        Debug.WriteLine($"[UNHANDLED] {e.Message}");
    }

    private static void WriteCrashLog(Exception ex)
    {
        try
        {
            const long MaxLogSize = 10 * 1024 * 1024; // 10 MB
            var oldLog = CrashLogPath + ".old";
            if (File.Exists(CrashLogPath) && new FileInfo(CrashLogPath).Length > MaxLogSize)
            {
                if (File.Exists(oldLog)) File.Delete(oldLog);
                File.Move(CrashLogPath, oldLog);
            }

            var message = $"[{DateTime.Now:O}]\n{ex}\n\n{new string('-', 80)}\n";
            File.AppendAllText(CrashLogPath, message);
        }
        catch { /* don't crash during crash handler */ }
    }
}
