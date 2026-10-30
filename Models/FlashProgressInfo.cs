namespace FastTool.Models;

/// <summary>
/// Represents a progress update from a fastboot flash operation.
/// Carries both percentage and (optionally) transfer speed.
/// </summary>
/// <param name="Percent">0-100 for normal progress; -1 for failure.</param>
/// <param name="SpeedMBs">Transfer speed in MB/s, or null if unavailable.</param>
public sealed record FlashProgressInfo(int Percent, double? SpeedMBs = null)
{
    /// <summary>Format the speed for display, e.g. "37.05 MB/s" or "".</summary>
    public string SpeedText => SpeedMBs.HasValue
        ? $"{SpeedMBs.Value:F2} MB/s"
        : "";

    /// <summary>Format percent for display, e.g. "45%" or "" on failure.</summary>
    public string PercentText => Percent >= 0 ? $"{Percent}%" : "";

    /// <summary>Combined display, e.g. "45%  37.05 MB/s".</summary>
    public string DisplayText => SpeedMBs.HasValue && Percent >= 0
        ? $"{Percent}%  {SpeedMBs.Value:F2} MB/s"
        : PercentText;
}
