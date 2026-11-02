using System.Globalization;
using SailTool.Services.Parsing;

namespace SailTool.Services;

/// <summary>
/// Decides which fastboot output lines reach the log panel.
/// </summary>
/// <remarks>
/// <para>
/// A progress-capable fastboot rewrites the same progress line tens of times per
/// second — once per megabyte of a sparse download, and no faster than the 25 ms
/// the bar itself allows. Appending every one would grow the log to thousands of
/// lines during a single flash and spend a string concatenation, a property
/// notification and a TextBlock assignment on each. This gate keeps the log
/// readable and the UI responsive with three rules:
/// </para>
/// <list type="number">
///   <item>A progress line whose parsed state equals the last one logged is dropped.
///   Nothing in the panel would change.</item>
///   <item>Otherwise at most one progress line per <see cref="IntervalMs"/>. Frames
///   are dropped, not queued, so the panel never catches up on a backlog.</item>
///   <item>The frame that reports a completed transfer is exempt from that interval,
///   because it is where the one line comes to rest. It arrives tens of milliseconds
///   after the last sampled frame, so the interval would otherwise discard it and
///   leave a finished flash reading <c>98%</c>.</item>
/// </list>
/// <para>
/// Every other line — <c>Sending</c>, <c>OKAY</c>, <c>Writing</c>, <c>Finished</c>,
/// errors — always passes through unchanged. Those are the lines worth reading back
/// afterwards, and they arrive at human pace. The only shapes sampled are the ones
/// fastboot redraws in bulk: bar frames, transfer lines, and sparse chunk headers.
/// </para>
/// <para>
/// Thread-safe: stdout and stderr are read by separate tasks, so both can reach
/// <see cref="ShouldLog"/> concurrently. The lock is short and uncontended in
/// practice — it guards two field reads and two writes.
/// </para>
/// <para>
/// The clock is injectable so tests can advance time deterministically.
/// </para>
/// </remarks>
internal sealed class ProgressLogGate
{
    /// <summary>Minimum spacing between two logged progress lines, in milliseconds.</summary>
    internal const int IntervalMs = 100;

    private readonly Func<long> _elapsedMs;
    private readonly object _gate = new();

    /// <summary>
    /// When the last progress line was logged, or null if none has been yet.
    /// Nullable rather than a <c>long.MinValue</c> sentinel: subtracting
    /// <c>long.MinValue</c> overflows, and an injectable clock starting at zero —
    /// which is what tests use — would see a negative elapsed time and drop the
    /// first line.
    /// </summary>
    private long? _lastProgressLogMs;
    private string? _lastProgressState;

    /// <summary>
    /// Creates a gate.
    /// </summary>
    /// <param name="elapsedMs">
    /// Monotonic milliseconds. Defaults to <see cref="Environment.TickCount64"/>, so
    /// a system clock adjustment mid-flash cannot stall or flood the panel.
    /// </param>
    internal ProgressLogGate(Func<long>? elapsedMs = null)
    {
        _elapsedMs = elapsedMs ?? (() => Environment.TickCount64);
    }

    /// <summary>
    /// Whether <paramref name="line"/> should be appended to the log.
    /// </summary>
    /// <remarks>
    /// Three line shapes are sampled: the bar frame the shipped build draws, the
    /// transfer line the older 35.0.2-eng build emitted, and the two shapes that
    /// appear in bulk during a sparse flash. The last matter because a 4 GB
    /// <c>super</c> image split into 40 chunks produces 80 such lines in a few
    /// seconds, and line flash runs several partitions back to back.
    /// </remarks>
    internal bool ShouldLog(string line)
    {
        // Parsing is done outside the lock: it is the expensive part and is pure.
        var state = DescribeProgressState(line, out var isTerminal);
        if (state is null) return true;      // not a progress line: always log

        lock (_gate)
        {
            // Same state as the last logged progress line — nothing new to show.
            if (state == _lastProgressState) return false;

            var now = _elapsedMs();

            // The completed frame is exempt from the interval. It is the frame the
            // panel is left showing, and it always arrives immediately after the last
            // sampled one — 98% -> 100% is a few MiB, tens of milliseconds at USB
            // speed. Sampling it away left a successful flash reading "98%" in the
            // log beside a finished progress bar.
            if (!isTerminal && _lastProgressLogMs is { } last && now - last < IntervalMs)
                return false;

            _lastProgressState = state;
            _lastProgressLogMs = now;
            return true;
        }
    }

    /// <summary>
    /// Returns a comparable description of a progress line's state, or null when the
    /// line is not a progress line at all.
    /// </summary>
    /// <param name="isTerminal">
    /// True for a frame that reports a completed transfer. Only the two shapes that
    /// carry a percentage can set it.
    /// </param>
    /// <remarks>
    /// <para>
    /// Built from the parsed numbers rather than the raw text, because the raw text
    /// differs on padding alone. In the recorded output the size field is
    /// right-aligned (<c>"recovery:   5.0 MB"</c> versus <c>"recovery: 100.0 MB"</c>),
    /// so a text comparison would log a "change" that is only whitespace.
    /// </para>
    /// <para>
    /// A result line is never sampled, even when it repeats a progress line's prefix:
    /// fastboot appends <c>OKAY</c> to the very line it was drawing on, so
    /// <c>"Sending 'boot' (19531 KB)   OKAY [  1.272s]"</c> and the sparse
    /// <c>"Sending sparse 'super' 13/13 (262040 KB)   OKAY [  3.100s]"</c> both parse
    /// as the frame they overwrote. Dropping either hides the line that says the
    /// transfer worked.
    /// </para>
    /// </remarks>
    private static string? DescribeProgressState(string line, out bool isTerminal)
    {
        isTerminal = false;

        // Results end a command and arrive once; they are never frames.
        if (FastbootOutputParser.OkayTimeRegex().IsMatch(line)) return null;

        var m = FastbootOutputParser.TransferProgressRegex().Match(line);
        if (m.Success)
        {
            var g = m.Groups;
            isTerminal = IsComplete(g["percent"].Value);
            return string.Join('|',
                "transfer",
                g["partition"].Value.Trim(),
                g["percent"].Value,
                g["cur"].Value, g["curUnit"].Value,
                g["total"].Value, g["totalUnit"].Value,
                g["speed"].Value, g["speedUnit"].Value);
        }

        // Bar frame: "Sending 'boot' (19456 KB) [##>-]  45%  8.0 MiB/19.0 MiB 17.5 MiB/s".
        // Checked before the Sparse header below, because the frame repeats that
        // prolog too.
        var bar = FastbootOutputParser.BarProgressRegex().Match(line);
        if (bar.Success)
        {
            var g = bar.Groups;
            isTerminal = IsComplete(g["percent"].Value);
            return string.Join('|',
                "bar",
                g["percent"].Value,
                g["cur"].Value, g["curUnit"].Value,
                g["total"].Value, g["totalUnit"].Value,
                g["speed"].Value, g["speedUnit"].Value);
        }

        // Sparse chunk header: "Sending sparse 'super' 1/13 (721288 KB)".
        // Sampled: a 4 GB image split into 40 chunks produces 80 of these in a few
        // seconds, and a line-flash script runs several partitions back to back.
        var sparse = FastbootOutputParser.SparseChunkRegex().Match(line);
        if (sparse.Success)
            return string.Join('|', "sparse", sparse.Groups[1].Value, sparse.Groups[2].Value);

        // A plain "Sending 'x' (N KB)" header is deliberately *not* sampled. It is
        // emitted once per partition, and the bar frame that repeats it is matched
        // above, so sampling it only ever dropped a real event — the message that the
        // next partition started — when two partitions ran back to back.
        return null;
    }

    /// <summary>Whether a percentage extracted from a frame means the transfer ended.</summary>
    private static bool IsComplete(string percentText)
    {
        // The old transfer format writes "65.0%" and may use a comma decimal
        // separator; the bar writes an integer.
        var text = percentText.Replace(',', '.');
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
               && percent >= 100;
    }
}
