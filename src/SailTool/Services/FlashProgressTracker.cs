using System.Text.RegularExpressions;
using SailTool.Models;
using SailTool.Services.Parsing;

namespace SailTool.Services;

/// <summary>
/// Stateful interpreter for one <c>fastboot flash</c> session's output stream.
/// </summary>
/// <remarks>
/// <para>
/// Turns each output line into at most one <see cref="FlashProgressInfo"/>
/// callback, holding the cross-line state that needs it: the phase fastboot is in,
/// the authoritative total byte count, and monotonic cursors for percent and bytes.
/// </para>
/// <para>
/// <strong>Progress is monotonic by construction.</strong> Both the percentage and
/// the byte cursor only ever move forward, because fastboot legitimately reports
/// backwards: a sparse image sends several chunks and each one restarts from 0%, and
/// the same percentage is emitted repeatedly. Assigning the reported value directly
/// makes the bar jump around, which reads as the flash having stalled.
/// </para>
/// <para>
/// Three producers are supported. MeowArch's build (the one shipped) draws a
/// single-line bar that carries percent, both byte counts and an instantaneous
/// rate all at once; the older 35.0.2-eng build emitted the same numbers in a
/// <c>name: 5.0 MB/100.0 MB (5.0%)</c> line. Both are the primary path and share
/// one handler. An official build emits neither, so the tracker falls back to
/// sparse-chunk accounting and derives an average speed from the OKAY timing. Which build is in use is decided by
/// <see cref="FastbootCapability.SupportsTransferProgress"/>, not inferred from the
/// stream, so the UI can tell the user up front.
/// </para>
/// <para>
/// Extracted from the local function inside <see cref="FastbootService.FlashImageAsync"/>
/// so the sequence logic can be driven from tests without spawning fastboot.
/// Not thread-safe: one instance per flash operation, fed from a single stream reader.
/// </para>
/// </remarks>
internal sealed class FlashProgressTracker
{
    private readonly Action<FlashProgressInfo>? _onProgress;

    /// <summary>
    /// The clock, resolved once in the constructor so the field is non-nullable and
    /// call sites do not need a null check on a hot path.
    /// </summary>
    private readonly Func<DateTime> _now;

    // ── Monotonic cursors ────────────────────────────────────────
    // Percent truncates to an integer for the bar; the fractional part is kept so
    // that a long transfer with many small steps still advances smoothly rather
    // than sitting on each whole number for several callbacks.
    private double _percent;
    private long   _transferredBytes;

    // ── Authoritative totals ─────────────────────────────────────
    // Taken from the "Sending" header, which fastboot prints once per partition
    // before any transfer line. Progress lines also carry a total, and when both
    // are present the larger wins: the header counts the whole image while a
    // transfer line during sparse chunking counts one chunk.
    private long _totalBytes;

    // ── Phase ────────────────────────────────────────────────────
    private FlashPhase _phase = FlashPhase.Sending;

    // ── Native rate ──────────────────────────────────────────────
    private double? _speedMBs;

    // ── Sparse chunk accounting (fallback path) ──────────────────
    private long     _sparseBytesTotal;   // bytes announced across chunks so far
    private DateTime _lastChunkTime = DateTime.MinValue;
    private long     _lastChunkBytes;     // cumulative bytes at the previous boundary

    // ── OKAY-timing fallback (official builds) ───────────────────
    private long _pendingSendKB;          // KB announced by the last "Sending" header
    private bool _inSendPhase;            // true between "Sending" and its "OKAY"
    private bool _sparseActive;           // true while a multi-chunk transfer runs

    // ── Sending-header dedupe ────────────────────────────────────
    // "partition|sizeKB" of the last Sending header seen. MeowArch's bar reprints
    // that prolog on every frame it redraws, so the header block in HandleLine runs
    // once per frame for a single transfer; only a value that actually changes is a
    // new transfer window.
    private string? _lastSendKey;

    private bool _sawIntermediateProgress;
    private bool _emittedCompletion;

    /// <summary>
    /// Creates a tracker.
    /// </summary>
    /// <param name="onProgress">Receives each update; may be null when only the
    /// log is wanted.</param>
    /// <param name="now">Clock used for the sparse-chunk speed estimate. Injectable
    /// so tests can control elapsed time instead of racing the wall clock.</param>
    internal FlashProgressTracker(
        Action<FlashProgressInfo>? onProgress,
        Func<DateTime>? now = null)
    {
        _onProgress = onProgress;
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Whether any intermediate percentage has been reported — from a transfer line,
    /// a bootloader percentage, or a sparse chunk header.
    /// </summary>
    /// <remarks>
    /// Exposed so a caller can distinguish "we only ever saw a terminal OKAY and
    /// inferred completion" from "the device actually reported progress". A bare
    /// OKAY leaves this false, which is the whole point of the distinction.
    /// </remarks>
    internal bool SawIntermediateProgress => _sawIntermediateProgress;

    /// <summary>Feeds one output line and emits progress if the line warrants it.</summary>
    internal void HandleLine(string line)
    {
        // ── 0. "Sending" header: partition name and authoritative total ──
        //
        // Captured on every Sending line, sparse or not, because it is the only
        // place the whole-image size is stated. It also opens the OKAY-timing
        // window used by the fallback path.
        var sendMatch = FastbootOutputParser.SendingSizeRegex().Match(line);
        if (sendMatch.Success &&
            long.TryParse(sendMatch.Groups[2].Value, out long sendKB))
        {
            _pendingSendKB = sendKB;
            _inSendPhase   = true;
            _totalBytes    = Math.Max(_totalBytes, sendKB * 1024);

            // A new Sending header opens a new transfer window, so the previous
            // partition's rate is stale. Clearing it here is what lets the OKAY
            // timing fallback derive a fresh average for this partition instead of
            // being suppressed by the last one's figure.
            //
            // Only a *new* header, though: MeowArch's bar repeats the prolog on every
            // frame it redraws, so clearing unconditionally would wipe the native
            // rate on each frame and only the frames that carry their own speed
            // would ever show one.
            var sendKey = $"{sendMatch.Groups[1].Value}|{sendKB}";
            if (!string.Equals(sendKey, _lastSendKey, StringComparison.Ordinal))
            {
                _lastSendKey = sendKey;
                _speedMBs    = null;
            }
        }

        // ── 1. Transfer progress line (progress-capable build) ────────────
        //
        // "recovery:  65.0 MB/100.0 MB ( 65.0%) [raw] 40.22 MB/s"
        //
        // This single line carries everything: percent, both byte counts and the
        // instantaneous USB rate. It is strictly better than anything the fallback
        // path can derive, so when it matches the timing fallback is switched off
        // for this partition — an average over the whole transfer would contradict
        // the rate fastboot just reported.
        var transfer = FastbootOutputParser.TransferProgressRegex().Match(line);
        if (transfer.Success)
        {
            HandleTransferLine(transfer);
            return;
        }

        // ── 1b. Bar frame (MeowArch build with FORCE_PROGRESS=1) ──────────
        //
        // "Sending 'boot' (19531 KB)  [####>---]  45%  8.0 MiB/19.1 MiB 17.5 MiB/s"
        //
        // Same numbers as the transfer line above, so it goes through the same
        // handler: the group names were kept identical on purpose.
        var bar = FastbootOutputParser.BarProgressRegex().Match(line);
        if (bar.Success)
        {
            HandleTransferLine(bar);
            return;
        }

        // ── 2. Bare percentage: "(bootloader) 47%" ────────────────────────
        //
        // Some bootloaders push their own percentage as INFO text, which fastboot
        // relays prefixed. No byte counts and no rate come with it.
        var pctMatch = FastbootOutputParser.ProgressPercentRegex().Match(line);
        if (pctMatch.Success &&
            FastbootOutputParser.TryParseInvariant(pctMatch.Groups["percent"].Value, out double barePct))
        {
            _sawIntermediateProgress = true;
            Advance(barePct, bytes: null, total: null);
            Emit();
            return;
        }

        // ── 3. Sparse chunks: "Sending sparse 'super' 1/13 (721288 KB)" ───
        //
        // The only intermediate progress an official build yields, and only when
        // the image exceeds the device's max-download-size so fastboot splits it.
        var sparseMatch = FastbootOutputParser.SparseChunkRegex().Match(line);
        if (sparseMatch.Success &&
            int.TryParse(sparseMatch.Groups[1].Value, out int current) &&
            int.TryParse(sparseMatch.Groups[2].Value, out int total) &&
            total > 0)
        {
            HandleSparseChunk(line, current, total);
            return;
        }

        // ── 4. Write phase: "Writing 'recovery'" ──────────────────────────
        //
        // fastboot reports no intermediate progress while committing to flash, so
        // this line is the signal to take the bar to 100% and label the phase.
        if (FastbootOutputParser.WritingRegex().IsMatch(line))
        {
            _phase = FlashPhase.Writing;
            _percent = 100;
            if (_totalBytes > 0) _transferredBytes = _totalBytes;
            // The transfer is over, so the rate no longer describes what is
            // happening. Keeping it displayed would show a USB figure next to a
            // write-to-flash phase, and there is no rate for that phase at all.
            _speedMBs = null;
            Emit();
            return;
        }

        // ── 5. Terminal status: OKAY / FAILED / Finished / error ──────────
        var statusMatch = FastbootOutputParser.StatusRegex().Match(line);
        if (!statusMatch.Success)
        {
            // "Finished." alone carries no status word in some builds.
            if (FastbootOutputParser.FinishedRegex().IsMatch(line))
                Complete();
            return;
        }

        var status = statusMatch.Groups[1].Value;
        var isOkay = status.Equals("OKAY", StringComparison.OrdinalIgnoreCase)
                  || status.Equals("finished", StringComparison.OrdinalIgnoreCase);

        if (isOkay)
        {
            HandleOkay(line);
        }
        else if (status.Equals("FAILED", StringComparison.OrdinalIgnoreCase) ||
                 status.Equals("error", StringComparison.OrdinalIgnoreCase))
        {
            _phase = FlashPhase.Failed;
            Emit(new FlashProgressInfo(
                -1, _speedMBs, _transferredBytes > 0 ? _transferredBytes : null,
                _totalBytes > 0 ? _totalBytes : null, FlashPhase.Failed));
            _inSendPhase = false;
        }
    }

    /// <summary>
    /// Handles a transfer progress line from a progress-capable fastboot build.
    /// </summary>
    /// <remarks>
    /// Takes the native rate verbatim rather than recomputing it from byte deltas.
    /// fastboot measures the USB transfer itself, so its figure excludes handshake
    /// and write time and matches what the user would see running the same command
    /// in a terminal — a locally derived rate would jitter, because the stream
    /// reader hands lines over in bursts rather than at a steady cadence.
    /// </remarks>
    private void HandleTransferLine(System.Text.RegularExpressions.Match transfer)
    {
        var groups = transfer.Groups;

        long? cur = null, total = null;
        if (FastbootOutputParser.TryParseInvariant(groups["cur"].Value, out double curVal))
            cur = FastbootOutputParser.ToBytes(curVal, groups["curUnit"].Value);
        if (FastbootOutputParser.TryParseInvariant(groups["total"].Value, out double totalVal))
            total = FastbootOutputParser.ToBytes(totalVal, groups["totalUnit"].Value);

        if (FastbootOutputParser.TryParseInvariant(groups["speed"].Value, out double speedVal))
            _speedMBs = FastbootOutputParser.ToMegabytesPerSecond(speedVal, groups["speedUnit"].Value);

        if (FastbootOutputParser.TryParseInvariant(groups["percent"].Value, out double pct))
        {
            _sawIntermediateProgress = true;
            _phase = FlashPhase.Sending;
            // A native rate supersedes the OKAY-timing estimate for this partition.
            _inSendPhase = false;
            Advance(pct, cur, total);
            Emit();
        }
    }

    /// <summary>
    /// Handles a sparse-chunk header: accounts bytes, estimates a rate from the
    /// interval between chunks, and reports chunk-based progress.
    /// </summary>
    private void HandleSparseChunk(string line, int current, int total)
    {
        _sawIntermediateProgress = true;
        _phase = FlashPhase.Sending;
        // More chunks follow, so an OKAY here closes only this chunk. The last
        // chunk's OKAY is the end of the transfer and is handled normally.
        _sparseActive = current < total;
        double? speedMBs = null;

        var sizeMatch = FastbootOutputParser.ChunkSizeKbRegex().Match(line);
        if (sizeMatch.Success && long.TryParse(sizeMatch.Groups[1].Value, out long chunkKB))
        {
            _sparseBytesTotal += chunkKB * 1024;

            if (_lastChunkTime != DateTime.MinValue)
            {
                var elapsed = (_now() - _lastChunkTime).TotalSeconds;
                if (elapsed > 0.1)   // avoid dividing by a near-zero interval
                {
                    var bytesSent = _sparseBytesTotal - _lastChunkBytes;
                    speedMBs = bytesSent / elapsed / (1024.0 * 1024.0);
                }
            }

            _lastChunkTime  = _now();
            _lastChunkBytes = _sparseBytesTotal;
        }

        if (speedMBs.HasValue) _speedMBs = speedMBs;

        // Chunk i of N: i chunks are announced and the i-th is in flight, so the bar
        // reads i/N. Capped at 99 because the chunk still has to transfer and the
        // write phase has not started; without the cap a 1/1 sparse image would sit
        // at 100% for the whole transfer and read as finished.
        var chunkPct = (long)current * 100 / total;
        Advance(Math.Min(chunkPct, 99),
            _sparseBytesTotal > 0 ? _sparseBytesTotal : null,
            _totalBytes > 0 ? _totalBytes : null);
        Emit();
    }

    /// <summary>
    /// Handles an OKAY line: either derives an average rate from the transfer
    /// window, or closes out the flash.
    /// </summary>
    private void HandleOkay(string line)
    {
        // An OKAY that closes one sparse chunk is not the end of the transfer —
        // several more chunks may follow. Treating it as completion would pin the
        // bar at 100% for the rest of the flash, and because the cursor is
        // monotonic it could never come back down.
        if (_sparseActive)
        {
            _inSendPhase   = false;
            _pendingSendKB = 0;
            return;
        }

        // Derive an average rate only when fastboot gave no native one. This is the
        // only speed an official-build user ever sees, computed as the announced
        // size over the elapsed time fastboot itself reported for that phase.
        if (_inSendPhase && _pendingSendKB > 0 && _speedMBs is null)
        {
            var timeMatch = FastbootOutputParser.OkayTimeRegex().Match(line);
            if (timeMatch.Success &&
                FastbootOutputParser.TryParseInvariant(timeMatch.Groups[1].Value, out double elapsed) &&
                elapsed > 0.05)
            {
                _speedMBs = _pendingSendKB / 1024.0 / elapsed;
                _phase = FlashPhase.Sending;
                // The transfer is over, so take the bar to the end and report the
                // average. Each partition gets its own Sending/OKAY pair, and this
                // runs once per pair because both guards below are cleared — a
                // multi-partition script therefore reports a fresh figure for each.
                _percent = 100;
                if (_totalBytes > 0) _transferredBytes = _totalBytes;
                _emittedCompletion = true;
                Emit();
                _inSendPhase   = false;
                _pendingSendKB = 0;
                return;
            }
        }

        // Nothing ever reported progress, so completion is inferred from the status
        // line. When progress WAS reported, the bar is already where the device put
        // it and re-emitting 100 here would only redraw an unchanged bar.
        if (!_sawIntermediateProgress)
            EmitCompletionOnce();

        _inSendPhase = false;

        if (FastbootOutputParser.FinishedRegex().IsMatch(line))
            Complete();
    }

    /// <summary>
    /// Takes the bar to 100% exactly once.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="SawIntermediateProgress"/>, which records whether
    /// the device ever reported real progress. Conflating the two is what made a
    /// bare terminal OKAY look like intermediate progress: completion is inferred
    /// from status in that case, and a caller has to be able to tell the difference
    /// between "the device told us 65%" and "we assumed it finished".
    /// </remarks>
    private void EmitCompletionOnce()
    {
        if (_emittedCompletion) return;
        _emittedCompletion = true;
        _percent = 100;
        if (_totalBytes > 0) _transferredBytes = _totalBytes;
        Emit();
    }

    /// <summary>Marks the flash finished and reports the terminal state.</summary>
    private void Complete()
    {
        if (_phase == FlashPhase.Finished) return;
        _phase = FlashPhase.Finished;
        _percent = 100;
        if (_totalBytes > 0) _transferredBytes = _totalBytes;
        Emit();
    }

    /// <summary>
    /// Moves the cursors forward, never backward.
    /// </summary>
    /// <remarks>
    /// A reported value lower than the cursor is ignored rather than clamped up, so
    /// a sparse chunk restarting from 0% does not nudge the bar and a repeated
    /// percentage does not churn the UI.
    /// </remarks>
    private void Advance(double? percent, long? bytes, long? total)
    {
        if (percent is { } p && p > _percent) _percent = Math.Min(p, 100);
        if (bytes   is { } b && b > _transferredBytes) _transferredBytes = b;
        if (total   is { } t && t > _totalBytes) _totalBytes = t;
    }

    /// <summary>Emits the current state.</summary>
    private void Emit()
    {
        _onProgress?.Invoke(new FlashProgressInfo(
            Percent:          (int)_percent,
            SpeedMBs:         _speedMBs,
            TransferredBytes: _transferredBytes > 0 ? _transferredBytes : null,
            TotalBytes:       _totalBytes > 0 ? _totalBytes : null,
            Phase:            _phase));
    }

    /// <summary>Emits an explicit update, used for the failure path.</summary>
    private void Emit(FlashProgressInfo info) => _onProgress?.Invoke(info);
}
