using SailTool.Enums;

namespace SailTool.Models;

/// <summary>
/// Device details gathered through <c>fastboot getvar</c> or <c>adb getprop</c>.
/// </summary>
/// <remarks>
/// <para>
/// A display-ready DTO: every string field is what the detail grid renders, and
/// any field that could not be read comes back as
/// <see cref="SailTool.Services.Parsing.DeviceDetailFormatter.Dash"/>.
/// </para>
/// <para>
/// <see cref="Bootloader"/> is the one typed field, because three call sites need
/// to branch on lock state rather than show it. <see cref="BootloaderDisplay"/>
/// carries the label for the grid.
/// </para>
/// <para>
/// <see cref="DetectionType"/> stays a string: it is a short user-facing label
/// (one of five, already localized) computed by
/// <c>DeviceDetailFormatter.MapDetectionType</c> from connection and mode, and it
/// has no closed vocabulary of its own.
/// </para>
/// </remarks>
/// <param name="Serial">Device serial.</param>
/// <param name="DetectionType">Label for the detection-method column.</param>
/// <param name="DeviceCodename">ro.product.device / <c>product</c>.</param>
/// <param name="Brand">ro.product.brand / <c>brand</c>.</param>
/// <param name="Model">ro.product.model / <c>model</c>.</param>
/// <param name="Bootloader">Normalised lock state.</param>
/// <param name="Slot">A/B slot, normalised to <c>A</c> / <c>B</c>.</param>
/// <param name="AndroidVersion">ro.build.version.release.</param>
/// <param name="SystemVersion">Formatted ROM version, see the formatter.</param>
/// <param name="KernelVersion"><c>uname -r</c>.</param>
public sealed record DetailedDeviceInfo(
    string Serial,
    string DetectionType,
    string DeviceCodename,
    string Brand,
    string Model,
    BootloaderStatus Bootloader,
    string Slot,
    string AndroidVersion,
    string SystemVersion,
    string KernelVersion
)
{
    private const string Dash = "--";

    /// <summary>Lock state rendered for the grid.</summary>
    public string BootloaderDisplay => Bootloader.ToDisplayText();

    /// <summary>An instance whose every display field is the placeholder.</summary>
    public static DetailedDeviceInfo Empty(string serial, string detectionType) => new(
        Serial:          serial,
        DetectionType:   detectionType,
        DeviceCodename:  Dash,
        Brand:           Dash,
        Model:           Dash,
        Bootloader:      BootloaderStatus.Unknown,
        Slot:            Dash,
        AndroidVersion:  Dash,
        SystemVersion:   Dash,
        KernelVersion:   Dash
    );

    public string DisplayName => $"{Serial} [{DeviceCodename}] ({DetectionType})";
}
