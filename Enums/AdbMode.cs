namespace FastTool.Enums;

/// <summary>
/// Device mode when connected via ADB or Fastboot.
/// </summary>
public enum AdbMode
{
    Device,
    Recovery,
    Sideload,
    Unauthorized,
    Offline,
    Bootloader,
    Fastboot
}
