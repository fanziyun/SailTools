using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using FastTool.Helpers;
using FastTool.Services;
using FastTool.ViewModels;

namespace FastTool.Pages;

public sealed partial class ToolsPage : Page
{
    public ToolsPageViewModel ViewModel { get; }

    public ToolsPage()
    {
        ViewModel = new ToolsPageViewModel(FastbootServiceAccessor.Instance);
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ViewModel.ConfirmAsync = async (title, msg) =>
                await DialogHelper.ConfirmAsync(Content.XamlRoot, title, msg, "确认解锁");
        };
    }

    // ── Click handlers ─────────────────────────────────────────────
    private async void Reboot_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.OnRebootAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private async void RebootRecovery_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.OnRebootRecoveryAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private async void RebootBootloader_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.OnRebootBootloaderAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private async void Erase_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.OnEraseAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private async void Unlock_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.OnUnlockAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private async void GetVar_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.OnGetVarAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private async void CustomCmd_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.OnCustomCommandAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private async void PermissiveMode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.OnPermissiveModeAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }
}
