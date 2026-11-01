namespace SailTool.Models;

/// <summary>
/// One progress update from a fastboot flash operation.
/// </summary>
/// <remarks>
/// <para>
/// Carries the raw numbers rather than pre-formatted text, so the throttle can
/// compare against the previous update to decide whether anything actually changed,
/// and the tracker can keep its cursors monotonic.
/// </para>
/// <para>
/// <see cref="Percent"/> uses -1 to mean failure, which is distinct from 0 —
/// a transfer that has genuinely not started reports 0.
/// </para>
/// </remarks>
/// <param name="Percent">0-100 for normal progress; -1 for failure.</param>
/// <param name="SpeedMBs">Transfer speed in MB/s, or null if unavailable.</param>
/// <param name="TransferredBytes">Bytes sent so far, or null if unavailable.</param>
/// <param name="TotalBytes">Total bytes to send, or null if unavailable.</param>
/// <param name="Phase">Which fastboot stage this update came from.</param>
public sealed record FlashProgressInfo(
    int Percent,
    double? SpeedMBs = null,
    long? TransferredBytes = null,
    long? TotalBytes = null,
    FlashPhase Phase = FlashPhase.Sending)
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

/// <summary>
/// Which stage of a <c>fastboot flash</c> a progress update describes.
/// </summary>
/// <remarks>
/// fastboot reports differently per stage. <see cref="Sending"/> is the USB
/// transfer and is the only stage that yields a real percentage and speed;
/// <see cref="Writing"/> is the write to flash, which fastboot completes without
/// intermediate progress. The distinction exists so the throttle can pass terminal
/// states through immediately instead of holding them back for up to an interval.
/// </remarks>
public enum FlashPhase
{
    /// <summary>Transferring the image over USB.</summary>
    Sending,

    /// <summary>Image received; fastboot is writing it to flash.</summary>
    Writing,

    /// <summary>fastboot reported the command finished.</summary>
    Finished,

    /// <summary>fastboot reported FAILED or an error.</summary>
    Failed
}
