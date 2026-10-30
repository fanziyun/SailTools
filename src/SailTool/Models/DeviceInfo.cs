namespace FastTool.Models;

/// <summary>
/// Represents a device connected via ADB or Fastboot.
/// </summary>
public sealed record DeviceInfo(string Serial, string Mode, string ConnectionType)
{
    public string DisplayName => $"{Serial}  [{Mode}] ({ConnectionType})";

    public bool IsFastboot =>
        ConnectionType.Equals("Fastboot", StringComparison.OrdinalIgnoreCase) ||
        Mode.StartsWith("fastboot", StringComparison.OrdinalIgnoreCase);

    public bool IsAdb =>
        ConnectionType.Equals("ADB", StringComparison.OrdinalIgnoreCase) &&
        Mode.Equals("device", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => DisplayName;
}
