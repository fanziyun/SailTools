using Windows.Storage;
using Windows.Storage.Pickers;

namespace SailTool.Helpers;

/// <summary>
/// Shared file-picker plumbing for unpackaged WinUI 3.
/// </summary>
/// <remarks>
/// A picker outside an app window needs the HWND initialised through
/// <c>InitializeWithWindow</c>; forgetting it throws on
/// <c>PickSingleFileAsync</c>. Every pick in the app goes through here so that
/// cannot happen twice.
/// </remarks>
public static class FilePickerHelper
{
    /// <summary>
    /// Shows an open-file dialog and returns the chosen file, or null when the
    /// user cancels.
    /// </summary>
    /// <param name="window">The app window the dialog is modal to.</param>
    /// <param name="extensions">Type filters, each including its dot.</param>
    /// <param name="startLocation">Where the dialog opens.</param>
    public static async Task<StorageFile?> PickStorageFileAsync(
        Microsoft.UI.Xaml.Window window,
        string[] extensions,
        PickerLocationId startLocation = PickerLocationId.Desktop)
    {
        var picker = new FileOpenPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        foreach (var ext in extensions) picker.FileTypeFilter.Add(ext);
        picker.SuggestedStartLocation = startLocation;
        return await picker.PickSingleFileAsync();
    }

    /// <summary>
    /// Same dialog, returning just the path, or null when the user cancels.
    /// </summary>
    public static async Task<string?> PickFileAsync(
        Microsoft.UI.Xaml.Window window,
        string[] extensions,
        PickerLocationId startLocation = PickerLocationId.Desktop)
    {
        var file = await PickStorageFileAsync(window, extensions, startLocation);
        return file?.Path;
    }
}
