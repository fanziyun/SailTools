namespace SailTool.Enums;

/// <summary>
/// Conversion between <see cref="BootloaderStatus"/> and the Chinese labels the
/// device detail grid shows.
/// </summary>
internal static class BootloaderStatusExtensions
{
    /// <summary>
    /// The label rendered in the BL 状态 column. These strings are user-visible
    /// and are unchanged from before the enum existed.
    /// </summary>
    internal static string ToDisplayText(this BootloaderStatus status) => status switch
    {
        BootloaderStatus.Unlocked  => "已解锁",
        BootloaderStatus.Locked    => "未解锁",
        BootloaderStatus.CustomKey => "未解锁 (自定义密钥)",
        BootloaderStatus.Verified  => "未解锁 (已验证)",
        _                          => "--"
    };
}
