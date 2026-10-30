using Microsoft.UI.Xaml.Controls;

namespace FastTool.Helpers;

public static class DialogHelper
{
    public static async Task ShowErrorAsync(Microsoft.UI.Xaml.XamlRoot xamlRoot, string title, string content)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = "确定",
            XamlRoot = xamlRoot
        };
        await dialog.ShowAsync();
    }

    public static async Task<bool> ConfirmAsync(
        Microsoft.UI.Xaml.XamlRoot xamlRoot,
        string title,
        string content,
        string primaryText,
        string closeText = "取消",
        ContentDialogButton defaultButton = ContentDialogButton.Close)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            DefaultButton = defaultButton
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
