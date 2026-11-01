using SailTool.Models;

namespace SailTool.Services;

/// <summary>
/// Rate-limits progress updates so a fast stream cannot flood the UI.
/// </summary>
/// <remarks>
/// <para>
/// A progress-capable fastboot redraws its transfer line per chunk it sends and no
/// faster than the bar's own 25 ms gate — up to 40 frames a second on a large image.
/// Each one would otherwise cost a dispatcher hop, a property change notification and
/// a TextBlock assignment. Two rules keep that bounded:
/// </para>
/// <list type="number">
///   <item>An update identical to the last one shown is dropped outright. Nothing
///   on screen would change, so the work is pure waste.</item>
///   <item>Otherwise at most one update per <see cref="IntervalMs"/>. Frames are
///   dropped, never state: <see cref="FlashProgressTracker"/> carries the full
///   current state in every update, so the next one that gets through renders the
///   true position rather than the one that was skipped.</item>
/// </list>
/// <para>
/// Terminal states bypass the throttle. Completion, the transition into the write
/// phase and failure are each shown immediately — holding one of those back for up
/// to a third of a second makes the flash look like it hung at the end.
/// </para>
/// <para>
/// The clock is injectable so tests can advance time deterministically instead of
/// sleeping. Not thread-safe: one instance per flash operation, used from the
/// progress callback.
/// </para>
/// </remarks>
internal sealed class ProgressThrottle
{
    /// <summary>Minimum spacing between two rendered updates, in milliseconds.</summary>
    internal const int IntervalMs = 100;

    private readonly Func<long> _elapsedMs;

    /// <summary>
    /// When the last update was rendered, or null if none has been yet.
    /// </summary>
    /// <remarks>
    /// Nullable rather than a <c>long.MinValue</c> sentinel: subtracting
    /// <c>long.MinValue</c> overflows, and an injectable clock that starts at zero —
    /// which is what tests use — would then see a negative elapsed time and drop the
    /// very first update.
    /// </remarks>
    private long? _lastEmitMs;

    // Signature of the last update that was allowed through. Compared field by
    // field rather than by reference, because every tracker emit allocates a new
    // record instance and reference equality would never match.
    private (int Percent, double? Speed, long? Cur, long? Total, FlashPhase Phase)? _lastShown;

    /// <summary>Creates a throttle reading the given clock.</summary>
    /// <param name="elapsedMs">
    /// Monotonic milliseconds. Defaults to <see cref="Environment.TickCount64"/>,
    /// which is unaffected by wall-clock adjustments — a user changing the system
    /// time mid-flash must not stall or flood the progress bar.
    /// </param>
    internal ProgressThrottle(Func<long>? elapsedMs = null)
    {
        _elapsedMs = elapsedMs ?? (() => Environment.TickCount64);
    }

    /// <summary>
    /// Whether <paramref name="info"/> should be rendered now.
    /// </summary>
    /// <returns>
    /// True when the update differs from the last rendered one and enough time has
    /// passed, or when it is a terminal state that must not be delayed.
    /// </returns>
    internal bool ShouldRender(FlashProgressInfo info)
    {
        var signature = (info.Percent, info.SpeedMBs, info.TransferredBytes,
                         info.TotalBytes, info.Phase);

        // Identical to what is already on screen: nothing to do.
        if (_lastShown == signature) return false;

        var now = _elapsedMs();

        // Terminal states always go through, and they reset the window so a
        // subsequent update is not delayed by the bypass.
        if (IsTerminal(info))
        {
            _lastShown   = signature;
            _lastEmitMs  = now;
            return true;
        }

        if (_lastEmitMs is { } last && now - last < IntervalMs) return false;

        _lastShown  = signature;
        _lastEmitMs = now;
        return true;
    }

    /// <summary>
    /// States that must reach the user immediately regardless of the interval.
    /// </summary>
    /// <remarks>
    /// A negative percent is the failure signal. Percent at or above 100 is
    /// completion. The write and finished phases are transitions the user is
    /// waiting on: "data received, now committing" and "done".
    /// </remarks>
    private static bool IsTerminal(FlashProgressInfo info) =>
        info.Percent < 0 ||
        info.Percent >= 100 ||
        info.Phase is FlashPhase.Writing or FlashPhase.Finished or FlashPhase.Failed;
}
