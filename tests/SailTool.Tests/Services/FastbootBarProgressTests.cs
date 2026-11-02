using SailTool.Models;
using SailTool.Services;
using SailTool.Services.Parsing;
using Xunit;

namespace SailTool.Tests.Services;

/// <summary>
/// Covers the bar frames the shipped fastboot draws when <c>FORCE_PROGRESS=1</c> is
/// set: parsing them into progress updates, the log gate that samples them, and the
/// classification the panels use to decide whether to append or overwrite.
/// </summary>
/// <remarks>
/// <para>
/// The sample lines are verbatim stderr captured from the shipped binary
/// (AOSP fastboot 36.0.0 + MeowArch's bar) flashing a 19,531 KB image to a mock
/// device over TCP, including the prolog it reprints on every frame.
/// </para>
/// </remarks>
public class FastbootBarProgressTests
{
    /// <summary>
    /// One recorded transfer. Note that every frame repeats the <c>Sending</c>
    /// prolog, the first frame carries no rate (the bar needs two samples for one),
    /// and the percentage is printed in a three-character field.
    /// </summary>
    private static readonly string[] BootFlash =
    [
        "Warning: skip copying boot image avb footer (boot partition size: 0, boot image size: 20000000).",
        "Sending 'boot' (19531 KB)",
        "Sending 'boot' (19531 KB)                           [#>----------------------]   5%  1.0 MiB/19.1 MiB",
        "Sending 'boot' (19531 KB)                           [#####>------------------]  21%  4.0 MiB/19.1 MiB 22.6 MiB/s ETA 0:01",
        "Sending 'boot' (19531 KB)                           [########>---------------]  31%  6.0 MiB/19.1 MiB 18.8 MiB/s ETA 0:01",
        "Sending 'boot' (19531 KB)                           [##########>-------------]  42%  8.0 MiB/19.1 MiB 17.5 MiB/s ETA 0:01",
        "Sending 'boot' (19531 KB)                           [#############>----------]  52%  10.0 MiB/19.1 MiB 16.9 MiB/s ETA 0:01",
        "Sending 'boot' (19531 KB)                           [###############>--------]  63%  12.0 MiB/19.1 MiB 16.5 MiB/s ETA 0:00",
        "Sending 'boot' (19531 KB)                           [##################>-----]  73%  14.0 MiB/19.1 MiB 16.3 MiB/s ETA 0:00",
        "Sending 'boot' (19531 KB)                           [####################>---]  84%  16.0 MiB/19.1 MiB 16.1 MiB/s ETA 0:00",
        "Sending 'boot' (19531 KB)                           [#######################>]  94%  18.0 MiB/19.1 MiB 15.0 MiB/s ETA 0:00",
        "Sending 'boot' (19531 KB)                           [########################] 100%  19.1 MiB/19.1 MiB 14.0 MiB/s",
        "Sending 'boot' (19531 KB)                          OKAY [  1.272s]",
        "Writing 'boot'                                     OKAY [  0.200s]",
        "Finished. Total time: 1.475s",
    ];

    /// <summary>
    /// The same kind of transfer captured from the current build (MeowArch's
    /// single-line bar, upstream 952ca57). The frame shape changed: the prolog is
    /// trimmed to one separating space instead of being padded to 50 columns, the
    /// bar width is budgeted from the terminal width, and every frame now ends
    /// with a newline because the pipe path cannot erase the line with <c>\033[K</c>.
    /// </summary>
    /// <remarks>
    /// Captured with <c>FORCE_PROGRESS=1</c>, stderr redirected to a file (the same
    /// non-tty path the app reads). The old padded frames above are kept: a build
    /// that still pads must parse too, and the pattern is deliberately indifferent
    /// to the spacing between prolog and bar.
    /// </remarks>
    private static readonly string[] BootFlashCurrent =
    [
        "Warning: skip copying boot image avb footer (boot partition size: 0, boot image size: 19922944).",
        "Sending 'boot' (19456 KB)",
        "Sending 'boot' (19456 KB) [#>--------------------------]    5%  1.0 MiB/19.0 MiB",
        "Sending 'boot' (19456 KB) [####>-----------------------]   16%  3.0 MiB/19.0 MiB 29.8 MiB/s ETA 0:01",
        "Sending 'boot' (19456 KB) [############>---------------]   42%  8.0 MiB/19.0 MiB 17.5 MiB/s ETA 0:01",
        "Sending 'boot' (19456 KB) [###########################>]   95%  18.0 MiB/19.0 MiB 15.0 MiB/s ETA 0:00",
        "Sending 'boot' (19456 KB) [############################]  100%  19.0 MiB/19.0 MiB 15.0 MiB/s",
        "OKAY [  1.267s]",
        "Writing 'boot'                                     OKAY [  0.200s]",
        "Finished. Total time: 1.470s",
    ];

    private static FlashProgressTracker CreateTracker(List<FlashProgressInfo> sink)
        => new(info => sink.Add(info));

    // ══════════════════════════════════════════════════════════════
    //  parsing a frame
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Bar_frame_yields_percent_speed_and_both_byte_counts()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine(
            "Sending 'boot' (19531 KB)                           [#####>------------------]  21%  4.0 MiB/19.1 MiB 22.6 MiB/s ETA 0:01");

        var info = Assert.Single(progress);
        Assert.Equal(21, info.Percent);
        Assert.Equal(22.6, info.SpeedMBs!.Value, 2);
        Assert.Equal((long)(4.0 * 1024 * 1024), info.TransferredBytes);
        Assert.Equal((long)(19.1 * 1024 * 1024), info.TotalBytes);
        Assert.Equal(FlashPhase.Sending, info.Phase);
    }

    [Fact]
    public void The_first_frame_has_no_rate_and_still_reports_progress()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine(
            "Sending 'boot' (19531 KB)                           [#>----------------------]   5%  1.0 MiB/19.1 MiB");

        var info = Assert.Single(progress);
        Assert.Equal(5, info.Percent);
        Assert.Null(info.SpeedMBs);
        Assert.Equal((long)(1.0 * 1024 * 1024), info.TransferredBytes);
    }

    [Fact]
    public void A_completed_bar_reports_100_percent()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine(
            "Sending 'boot' (19531 KB)                           [########################] 100%  19.1 MiB/19.1 MiB 14.0 MiB/s");

        var info = Assert.Single(progress);
        Assert.Equal(100, info.Percent);
        Assert.Equal(14.0, info.SpeedMBs!.Value, 2);
    }

    [Theory]
    [InlineData("Sending 'dtbo' (1024 KB)                            [##>---------------------]   8%  1.0 MiB/12.0 MiB 9.0 MiB/s", 1L * 1024 * 1024, 12L * 1024 * 1024)]
    [InlineData("Sending 'super' (2097152 KB)                        [#>----------------------]   1%  1.0 GiB/2.0 GiB 40.0 MiB/s", 1024L * 1024 * 1024, 2048L * 1024 * 1024)]
    [InlineData("Sending 'boot' (19531 KB)                           [#>----------------------]   5%  512.0 KiB/19.1 MiB", 512L * 1024, (long)(19.1 * 1024 * 1024))]
    public void Binary_size_units_are_converted_to_bytes(string line, long expectedCur, long expectedTotal)
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine(line);

        Assert.Equal(expectedCur, progress[0].TransferredBytes);
        Assert.Equal(expectedTotal, progress[0].TotalBytes);
    }

    // ══════════════════════════════════════════════════════════════
    //  the repeated prolog
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void A_repeated_prolog_does_not_wipe_the_native_rate()
    {
        // Every frame reprints "Sending 'boot' (19531 KB)", which is also the
        // header that resets per-transfer state. Clearing the rate unconditionally
        // there would drop it on the very next frame — the bar would flicker between
        // a speed and nothing.
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine("Sending 'boot' (19531 KB)                           [#####>------------------]  21%  4.0 MiB/19.1 MiB 22.6 MiB/s ETA 0:01");
        tracker.HandleLine("Sending 'boot' (19531 KB)                           [######>-----------------]  25%  5.0 MiB/19.1 MiB");

        Assert.Equal(22.6, progress[^1].SpeedMBs!.Value, 2);
    }

    [Fact]
    public void A_new_partition_does_reset_the_rate()
    {
        // The dedupe keys on partition and size, so the next partition still starts
        // with a clean slate and may derive its own average from OKAY timing.
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine("Sending 'boot' (19531 KB)                           [#####>------------------]  21%  4.0 MiB/19.1 MiB 22.6 MiB/s ETA 0:01");
        tracker.HandleLine("Sending 'dtbo' (1024 KB)");
        tracker.HandleLine("OKAY [  1.000s]");

        Assert.Equal(1.0, progress[^1].SpeedMBs!.Value, 2);   // 1 MiB / 1 s
    }

    // ══════════════════════════════════════════════════════════════
    //  a whole recorded transfer
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void A_recorded_bar_transfer_is_monotonic_and_ends_finished()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        foreach (var line in BootFlash) tracker.HandleLine(line);

        Assert.True(tracker.SawIntermediateProgress);
        for (var i = 1; i < progress.Count; i++)
            Assert.True(progress[i].Percent >= progress[i - 1].Percent,
                $"percent regressed at {i}: {progress[i - 1].Percent} -> {progress[i].Percent}");

        Assert.Equal(100, progress[^1].Percent);
        Assert.Equal(FlashPhase.Finished, progress[^1].Phase);
        Assert.DoesNotContain(progress, p => p.Percent == -1);
    }

    [Fact]
    public void The_recorded_transfer_reaches_every_frame_percentage()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        foreach (var line in BootFlash) tracker.HandleLine(line);

        var percents = progress.Select(p => p.Percent).Distinct().ToList();
        foreach (var expected in new[] { 5, 21, 31, 42, 52, 63, 73, 84, 94, 100 })
            Assert.Contains(expected, percents);
    }

    [Fact]
    public void The_current_build_frame_shape_yields_all_four_numbers()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine(BootFlashCurrent[3]);

        var info = Assert.Single(progress);
        Assert.Equal(16, info.Percent);
        Assert.Equal(29.8, info.SpeedMBs!.Value, 2);
        Assert.Equal((long)(3.0 * 1024 * 1024), info.TransferredBytes);
        Assert.Equal((long)(19.0 * 1024 * 1024), info.TotalBytes);
        Assert.Equal(FlashPhase.Sending, info.Phase);
    }

    [Fact]
    public void The_current_build_recorded_transfer_is_monotonic_and_ends_finished()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        foreach (var line in BootFlashCurrent) tracker.HandleLine(line);

        Assert.True(tracker.SawIntermediateProgress);
        for (var i = 1; i < progress.Count; i++)
            Assert.True(progress[i].Percent >= progress[i - 1].Percent,
                $"percent regressed at {i}: {progress[i - 1].Percent} -> {progress[i].Percent}");

        var percents = progress.Select(p => p.Percent).Distinct().ToList();
        foreach (var expected in new[] { 5, 16, 42, 95, 100 })
            Assert.Contains(expected, percents);

        Assert.Equal(100, progress[^1].Percent);
        Assert.Equal(FlashPhase.Finished, progress[^1].Phase);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Current_build_bar_frames_are_progress_lines(int index)
    {
        Assert.True(FastbootOutputParser.IsProgressLine(BootFlashCurrent[index]));
    }

    [Fact]
    public void A_current_build_frame_still_strips_to_the_sampled_shape()
    {
        var stripped = FastbootOutputParser.RemoveProgressBar(BootFlashCurrent[3]);

        Assert.DoesNotContain("[", stripped);
        Assert.Contains("16%", stripped);
        Assert.Contains("3.0 MiB/19.0 MiB", stripped);
        Assert.True(FastbootOutputParser.IsProgressLine(stripped));
    }

    // ══════════════════════════════════════════════════════════════
    //  classification and the log gate
    // ══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(11)]
    public void Bar_frames_are_progress_lines(int index)
    {
        Assert.True(FastbootOutputParser.IsProgressLine(BootFlash[index]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    public void Everything_else_is_not_a_progress_line(int index)
    {
        // The bare "Sending" prolog and the OKAY/Writing/Finished lines must be
        // appended, never overwritten — they are what the user reads back later.
        Assert.False(FastbootOutputParser.IsProgressLine(BootFlash[index]));
    }

    [Fact]
    public void The_log_gate_samples_bar_frames_instead_of_logging_every_one()
    {
        long now = 0;
        var gate = new ProgressLogGate(() => now);

        var logged = 0;
        foreach (var line in BootFlash)
        {
            now += 25;   // one frame per 1 MiB chunk at ~40 MiB/s
            if (gate.ShouldLog(line)) logged++;
        }

        // Ten bar frames plus a handful of ordinary lines; the ordinary ones all pass.
        var barFrames = BootFlash.Count(FastbootOutputParser.IsProgressLine);
        Assert.Equal(10, barFrames);
        Assert.True(logged < BootFlash.Length,
            $"expected the frames to be sampled, got {logged} of {BootFlash.Length}");
        Assert.True(logged >= BootFlash.Length - barFrames,
            "the non-progress lines must all survive");
    }

    [Fact]
    public void An_identical_bar_frame_is_dropped_even_after_the_interval()
    {
        long now = 0;
        var gate = new ProgressLogGate(() => now);
        var frame = BootFlash[5];

        Assert.True(gate.ShouldLog(frame));
        now = 60_000;
        Assert.False(gate.ShouldLog(frame));
    }

    [Fact]
    public void A_bar_frame_is_not_mistaken_for_a_sending_header()
    {
        // The frame repeats the prolog, so the gate must classify it as a bar before
        // it looks at Sending headers. If it did not, the bar content — which is what
        // changes — would be ignored and every frame after the first would compare
        // equal to the last one and be dropped.
        long now = 0;
        var gate = new ProgressLogGate(() => now);

        Assert.True(gate.ShouldLog(BootFlash[2]));
        now = ProgressLogGate.IntervalMs;
        Assert.True(gate.ShouldLog(BootFlash[3]));
    }

    [Fact]
    public void The_completed_frame_is_not_sampled_away()
    {
        // The last frame is where the single log line comes to rest. Reaching 100%
        // from 94% on a 192 MiB image is a few MiB — ~70 ms at 40 MiB/s — so the
        // sampling interval would discard exactly the frame that proves the transfer
        // finished. A real flash was left reading "98%  189.0 MiB/192.0 MiB" beside
        // a successful result.
        long now = 0;
        var gate = new ProgressLogGate(() => now);

        Assert.True(gate.ShouldLog(BootFlash[10]));   // 94%
        now += 70;
        Assert.True(gate.ShouldLog(BootFlash[11]));   // 100%
    }

    [Fact]
    public void The_result_line_that_repeats_the_prolog_still_passes()
    {
        // "Sending 'boot' (19531 KB)   OKAY [  1.272s]" is the prolog with the result
        // appended: fastboot writes OKAY onto the line it was drawing on. Matching it
        // as a Sending header dropped the confirmation of the transfer, so the log
        // jumped from the last frame straight to Writing.
        long now = 0;
        var gate = new ProgressLogGate(() => now);

        Assert.True(gate.ShouldLog(BootFlash[11]));   // the 100% frame
        now += 5;
        Assert.True(gate.ShouldLog(BootFlash[12]));   // Sending 'boot' ... OKAY
    }

    [Fact]
    public void A_sparse_chunk_result_is_not_swallowed_by_its_own_header()
    {
        // The same trap in the sparse path, where the test for it is worse than the
        // interval: the OKAY line parses to the *identical* state as the chunk header
        // it was appended to, so the equality rule drops it even minutes later.
        long now = 0;
        var gate = new ProgressLogGate(() => now);

        Assert.True(gate.ShouldLog("Sending sparse 'super' 13/13 (262040 KB)"));
        now += 5;
        Assert.True(gate.ShouldLog("Sending sparse 'super' 13/13 (262040 KB)          OKAY [  3.100s]"));
    }

    [Fact]
    public void Back_to_back_sending_headers_all_pass()
    {
        // One header per partition; there is no flood of them, and the bar frame that
        // repeats a prolog is classified as a bar before it reaches this branch.
        // Sampling them only ever hid the message that the next partition had begun.
        var gate = new ProgressLogGate(() => 0);

        Assert.True(gate.ShouldLog("Sending 'boot_a' (196608 KB)"));
        Assert.True(gate.ShouldLog("Sending 'dtbo' (1024 KB)"));
        Assert.True(gate.ShouldLog("Sending 'system' (2097152 KB)"));
    }

    // ══════════════════════════════════════════════════════════════
    //  the form the log panel stores
    // ══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(3, "Sending 'boot' (19531 KB)  21%  4.0 MiB/19.1 MiB 22.6 MiB/s ETA 0:01")]
    [InlineData(2, "Sending 'boot' (19531 KB)  5%  1.0 MiB/19.1 MiB")]
    [InlineData(11, "Sending 'boot' (19531 KB)  100%  19.1 MiB/19.1 MiB 14.0 MiB/s")]
    public void RemoveProgressBar_keeps_the_numbers_and_drops_the_graphic(int index, string expected)
    {
        // The page has a real progress bar right above the log; drawing a second one
        // in characters is the same information twice, and the widest thing on the
        // line. The padding in front of the bar goes with it.
        Assert.Equal(expected, FastbootOutputParser.RemoveProgressBar(BootFlash[index]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    public void RemoveProgressBar_leaves_everything_else_untouched(int index)
    {
        Assert.Equal(BootFlash[index], FastbootOutputParser.RemoveProgressBar(BootFlash[index]));
    }

    [Fact]
    public void RemoveProgressBar_leaves_the_older_transfer_line_untouched()
    {
        // "[raw]" is a word, not a bar; the old build's line must still read as it
        // always did if that binary is swapped back in.
        const string line = "recovery:  65.0 MB/100.0 MB ( 65.0%) [raw] 40.22 MB/s";

        Assert.Equal(line, FastbootOutputParser.RemoveProgressBar(line));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(11)]
    public void A_stripped_frame_is_still_a_progress_line(int index)
    {
        // LogBuffer re-reads the buffer's own last line to decide whether the next
        // frame overwrites it. If the stripped text stopped classifying as progress,
        // every frame after the first would be appended again — the flood would come
        // straight back, just without the bar.
        var stripped = FastbootOutputParser.RemoveProgressBar(BootFlash[index]);

        Assert.True(FastbootOutputParser.IsProgressLine(stripped));
    }

    [Fact]
    public void Stripped_frames_still_collapse_to_one_line()
    {
        var text = string.Empty;
        foreach (var line in BootFlash)
        {
            if (!FastbootOutputParser.IsProgressLine(line))
            {
                text = LogBuffer.Append(text, line);
                continue;
            }

            var frame = FastbootOutputParser.RemoveProgressBar(line);
            text = LogBuffer.LastLineIsProgress(text)
                ? LogBuffer.ReplaceLastLine(text, frame)
                : LogBuffer.Append(text, frame);
        }

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // 15 recorded lines: ten frames collapse into one, five ordinary lines stay.
        Assert.Equal(6, lines.Length);
        Assert.Equal("Sending 'boot' (19531 KB)  100%  19.1 MiB/19.1 MiB 14.0 MiB/s", lines[2]);
        Assert.DoesNotContain("[#", text);
    }
}
