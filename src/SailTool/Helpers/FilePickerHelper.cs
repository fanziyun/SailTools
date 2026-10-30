using Windows.Storage.Pickers;

namespace SailTool.Helpers;

public static class FilePickerHelper
{
    public static async Task<string?> PickFileAsync(
        Microsoft.UI.Xaml.Window window,
        string[] extensions,
        PickerLocationId startLocation = PickerLocationId.Desktop)
    {
        var picker = new FileOpenPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        foreach (var ext in extensions) picker.FileTypeFilter.Add(ext);
        picker.SuggestedStartLocation = startLocation;
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }
}
