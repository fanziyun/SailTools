using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SailTool.ViewModels;

namespace SailTool.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPageViewModel ViewModel { get; } = new();
    private bool _isInitializing = true;

    public SettingsPage()
    {
        InitializeComponent();

        // 启动时读取保存的主题设置，同步 ComboBox
        var saved = App.GetSavedTheme();
        ThemeComboBox.SelectedIndex = saved switch
        {
            ElementTheme.Light  => 1,
            ElementTheme.Dark   => 2,
            _                   => 0
        };
        
        _isInitializing = false;
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 跳过初始化期间的 SelectionChanged 事件
        if (_isInitializing) return;
        
        if (ThemeComboBox.SelectedItem is not ComboBoxItem item) return;

        var theme = item.Tag?.ToString() switch
        {
            "Light" => ElementTheme.Light,
            "Dark"  => ElementTheme.Dark,
            _       => ElementTheme.Default
        };

        App.ApplyTheme(theme);
    }
}
