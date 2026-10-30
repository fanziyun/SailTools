using System.Globalization;
using SailTool.Models;
using SailTool.Services.Parsing;

namespace SailTool.Services;

/// <summary>
/// Stateful interpreter for one <c>fastboot flash</c> session's output stream.
/// </summary>
/// <remarks>
/// <para>
/// Holds the cross-line state that progress reporting needs — which sparse chunk
/// we are on, when the current "Sending" phase started, whether any percentage has
/// been seen yet — and turns each line into at most one
/// <see cref="FlashProgressInfo"/> callback.
/// </para>
/// <para>
/// Extracted from the local function inside <see cref="FastbootService.FlashImageAsync"/>
/// so the sequence logic can be driven from tests without spawning fastboot.
/// Not thread-safe: one instance per flash operation, fed from a single stream reader.
/// </para>
/// </remarks>
internal sealed class FlashProgressTracker(Action<FlashProgressInfo>? onProgress)
{
    private readonly Action<FlashProgressInfo>? _onProgress = onProgress;

    // ── Sparse chunk speed tracking ──────────────────────────────
    private long _sparseBytesTotal;   // total bytes announced across chunks so far
    private DateTime _lastChunkTime = DateTime.MinValue;
    private long _lastChunkBytes;     // cumulative bytes at previous chunk boundary

    // ── Fallback speed tracking: "Sending" phase timing ──────────
    // When fastboot doesn't output % or MB/s (sparse or old protocol),
    // we compute speed from Sending size / OKAY elapsed time.
    private long _pendingSendKB;      // KB announced by the last "Sending" header
    private bool _inSendPhase;        // true between "Sending" and "OKAY"

    private bool _gotFinalPct;

    /// <summary>
    /// Whether any intermediate percentage has been reported. Exposed so a caller
    /// can tell "we only ever saw a terminal OKAY" from real progress.
    /// </summary>
    internal bool GotFinalPct => _gotFinalPct;

    /// <summary>Feeds one output line and emits progress if the line warrants it.</summary>
    internal void HandleLine(string line)
    {
        // ── 0. Track "Sending" phase start (fallback speed) ───
        // On any "Sending" line (with or without % or MB/s), capture
        // the file size in KB.  When OKAY [Xs] arrives later, we can
        // derive speed = size / elapsed_time even if fastboot doesn't
        // output native speed data (common on many devices).
        var sendMatch = FastbootOutputParser.SendingSizeRegex().Match(line);
        if (sendMatch.Success &&
            long.TryParse(sendMatch.Groups[2].Value, out long sendKB))
        {
            _pendingSendKB = sendKB;
            _inSendPhase   = true;
        }

        // 1. Vendor builds: "Sending 'vendor_boot' (98304 KB)  45.2%  37.05 MB/s"
        //    Never emitted by stock fastboot — see FastbootOutputParser.ProgressSpeedRegex.
        var speedMatch = FastbootOutputParser.ProgressSpeedRegex().Match(line);
        if (speedMatch.Success &&
            double.TryParse(speedMatch.Groups[2].Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double pctVal) &&
            double.TryParse(speedMatch.Groups[3].Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double speedVal))
        {
            var unit = speedMatch.Groups[4].Value;
            double speedMBs = unit switch
            {
                "KB/s" => speedVal / 1024.0,
                "GB/s" => speedVal * 1024.0,
                _      => speedVal,   // already MB/s
            };
            _gotFinalPct = true;
            // Suppress fallback speed calc (native speed is more accurate)
            _inSendPhase = false;
            Emit(new FlashProgressInfo(Math.Clamp((int)pctVal, 0, 100), speedMBs));
            return;
        }

        // 2. Bootloader explicit %: "(bootloader) 5%"
        var pctMatch = FastbootOutputParser.ProgressPercentRegex().Match(line);
        if (pctMatch.Success &&
            int.TryParse(pctMatch.Groups[1].Value, out int pct))
        {
            _gotFinalPct = true;
            Emit(new FlashProgressInfo(Math.Clamp(pct, 0, 100)));
            return;
        }

        // 3. Sparse chunks: "Sending sparse 'super' 1/13 (721288 KB)"
        var sparseMatch = FastbootOutputParser.SparseChunkRegex().Match(line);
        if (sparseMatch.Success &&
            int.TryParse(sparseMatch.Groups[1].Value, out int current) &&
            int.TryParse(sparseMatch.Groups[2].Value, out int total) &&
            total > 0)
        {
            _gotFinalPct = true;
            var chunkPct = (int)((long)current * 100 / total);

            // Extract chunk size in KB for speed tracking
            double? speedMBs = null;
            var sizeMatch = FastbootOutputParser.ChunkSizeKbRegex().Match(line);
            if (sizeMatch.Success &&
                long.TryParse(sizeMatch.Groups[1].Value, out long chunkKB))
            {
                _sparseBytesTotal += chunkKB * 1024;

                if (_lastChunkTime != DateTime.MinValue)
                {
                    var elapsed = (DateTime.UtcNow - _lastChunkTime).TotalSeconds;
                    if (elapsed > 0.1) // avoid div-by-zero for instant chunks
                    {
                        var bytesSent = _sparseBytesTotal - _lastChunkBytes;
                        speedMBs = bytesSent / elapsed / (1024.0 * 1024.0);
                    }
                }

                _lastChunkTime  = DateTime.UtcNow;
                _lastChunkBytes = _sparseBytesTotal;
            }

            // Capped at 99 so the bar does not read "complete" before OKAY arrives.
            Emit(new FlashProgressInfo(Math.Clamp(chunkPct, 0, 99), speedMBs));
            return;
        }

        // 4. Terminal status: OKAY/FAILED/finished/error
        //    Also derives transfer speed from OKAY timing as a fallback
        //    when the protocol didn't output native speed data.
        var statusMatch = FastbootOutputParser.StatusRegex().Match(line);
        if (!statusMatch.Success) return;

        var status = statusMatch.Groups[1].Value;
        var isOkay = status.Equals("OKAY", StringComparison.OrdinalIgnoreCase)
                  || status.Equals("finished", StringComparison.OrdinalIgnoreCase);

        if (isOkay)
        {
            // ── Fallback speed: derive from Sending size / OKAY time ──
            // Only if we were in a send phase and fastboot didn't already
            // provide speed natively (step 1 would have cleared _inSendPhase).
            if (_inSendPhase && _pendingSendKB > 0)
            {
                var timeMatch = FastbootOutputParser.OkayTimeRegex().Match(line);
                if (timeMatch.Success &&
                    double.TryParse(timeMatch.Groups[1].Value, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double elapsed) && elapsed > 0.05)
                {
                    var derivedMBs = _pendingSendKB / 1024.0 / elapsed;
                    Emit(new FlashProgressInfo(100, derivedMBs));
                    _inSendPhase   = false;
                    _pendingSendKB = 0;
                    _gotFinalPct   = true;
                    return;
                }
            }

            if (!_gotFinalPct) Emit(new FlashProgressInfo(100));
            _inSendPhase = false;
        }
        else if (status.Equals("FAILED", StringComparison.OrdinalIgnoreCase) ||
                 status.Equals("error", StringComparison.OrdinalIgnoreCase))
        {
            Emit(new FlashProgressInfo(-1));
            _inSendPhase = false;
        }
    }

    private void Emit(FlashProgressInfo info) => _onProgress?.Invoke(info);
}
