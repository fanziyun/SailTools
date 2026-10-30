namespace SailTool.Enums;

/// <summary>
/// Which host-side tool reported the device.
/// </summary>
/// <remarks>
/// Member names are chosen so <see cref="object.ToString"/> reproduces the exact
/// text the UI has always displayed: <c>Fastboot</c> and <c>ADB</c> (uppercase
/// "DB", matching adb.exe's own branding). Anything that renders this enum into a
/// label relies on that — renaming a member is a visible UI change.
/// </remarks>
public enum ConnectionType
{
    /// <summary>Reported by <c>fastboot devices</c>.</summary>
    Fastboot,

    /// <summary>Reported by <c>adb devices</c>.</summary>
    ADB
}
