using SailTool.Models;
using SailTool.Services;
using Xunit;

namespace SailTool.Tests.Services;

/// <summary>
/// Covers <see cref="FlashProgressTracker"/> on the path an official fastboot build
/// takes: sparse-chunk accounting, bootloader percentages, the OKAY-timing speed
/// estimate, and terminal status.
/// </summary>
/// <remarks>
/// <para>
/// The transfer-progress path a patched build takes is covered by
/// <c>FlashProgressTransferTests</c>. Both feed recorded lines through
/// <see cref="FlashProgressTracker.HandleLine"/> rather than spawning fastboot,
/// which is what extracting the class from <c>FlashImageAsync</c> was for.
/// </para>
/// <para>
/// Percentages are asserted exactly. The bar the user watches is derived only from
/// these callbacks, so an off-by-one here is visible as a stuck or jumping bar.
/// </para>
/// </remarks>
public class FlashProgressTrackerTests
{
    private static FlashProgressTracker CreateTracker(List<FlashProgressInfo> sink)
        => new(info => sink.Add(info));

    // ══════════════════════════════════════════════════════════════
    //  sparse chunking — the only intermediate progress an official
    //  build reports, and only for an image above max-download-size
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Sparse_chunks_report_percentage_of_the_total_chunk_count()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        // Real sequence: each chunk has its own Sending header and OKAY.
        tracker.HandleLine("Sending sparse 'super' 1/4 (524288 KB)");
        tracker.HandleLine("OKAY [ 12.441s]");
        tracker.HandleLine("Sending sparse 'super' 2/4 (524288 KB)");

        Assert.Equal(25, progress[0].Percent);
        Assert.Equal(50, progress[^1].Percent);
    }

    [Fact]
    public void An_OKAY_between_sparse_chunks_does_not_complete_the_transfer()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine("Sending sparse 'super' 1/3 (524288 KB)");
        tracker.HandleLine("OKAY [ 12.441s]");
        tracker.HandleLine("Sending sparse 'super' 2/3 (524288 KB)");

        // The cursor is monotonic, so completing at the first chunk's OKAY would pin
        // the bar at 100% for the rest of a multi-gigabyte transfer with no way back.
        Assert.DoesNotContain(progress, p => p.Percent == 100);
        Assert.Equal(33, progress[0].Percent);
        Assert.Equal(66, progress[^1].Percent);
        // The intermediate OKAY added nothing of its own.
        Assert.Equal(2, progress.Count);
    }

    [Fact]
    public void The_rate_is_cleared_when_the_write_phase_begins()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine("Sending 'boot' (98304 KB)");
        tracker.HandleLine("OKAY [  4.000s]");
        tracker.HandleLine("Writing 'boot'");

        // The 24 MB/s figure describes the USB transfer that just ended. Showing it
        // next to "writing to flash" would be a number about something that is no
        // longer happening.
        Assert.Equal(24.0, progress[0].SpeedMBs!.Value, 2);
        Assert.Null(progress[^1].SpeedMBs);
        Assert.Equal(FlashPhase.Writing, progress[^1].Phase);
    }

    [Fact]
    public void Sparse_percentage_never_reaches_100_before_OKAY()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        // The final chunk computes to exactly 100% but is capped at 99, so the bar
        // does not read "done" while the write phase is still running.
        tracker.HandleLine("Sending sparse 'super' 4/4 (524288 KB)");

        Assert.Equal(99, progress[0].Percent);
    }

    [Fact]
    public void Sparse_progress_truncates_rather_than_rounds()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        // 2/3 is 66.67%; integer division gives 66. Rounding would show 67 and then
        // sit there while chunk 3 transfers.
        tracker.HandleLine("Sending sparse 'super' 2/3 (524288 KB)");

        Assert.Equal(66, progress[0].Percent);
    }

    [Fact]
    public void Sparse_first_chunk_reports_a_small_percentage_not_zero()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine("Sending sparse 'super' 1/13 (721288 KB)");

        // 1/13 truncates to 7. Zero would leave the bar visibly empty on the first of
        // thirteen chunks, which reads as "nothing happened".
        Assert.Equal(7, progress[0].Percent);
    }

    [Fact]
    public void Sparse_total_of_zero_is_ignored_instead_of_dividing()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine("Sending sparse 'super' 1/0 (1024 KB)");

        Assert.Empty(progress);
    }

    [Fact]
    public void Sparse_chunks_without_a_size_still_report_percentage()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine("Sending sparse 'super' 2/4");

        var info = Assert.Single(progress);
        Assert.Equal(50, info.Percent);
        Assert.Null(info.SpeedMBs);
    }

    [Fact]
    public void Sparse_chunk_bytes_accumulate_across_chunks()
    {
        var progress = new List<FlashProgressInfo>();
        var clock = new DateTime(2026, 10, 31, 12, 0, 0);
        var tracker = new FlashProgressTracker(info => progress.Add(info), () => clock);

        tracker.HandleLine("Sending sparse 'super' 1/4 (524288 KB)");
        clock = clock.AddSeconds(13);
        tracker.HandleLine("Sending sparse 'super' 2/4 (524288 KB)");

        // 512 MiB per chunk, so two chunks is 1 GiB announced.
        Assert.Equal(2L * 524288 * 1024, progress[^1].TransferredBytes);
    }

    [Fact]
    public void Sparse_chunk_interval_yields_an_estimated_rate()
    {
        var progress = new List<FlashProgressInfo>();
        var clock = new DateTime(2026, 10, 31, 12, 0, 0);
        var tracker = new FlashProgressTracker(info => progress.Add(info), () => clock);

        tracker.HandleLine("Sending sparse 'super' 1/4 (102400 KB)");
        clock = clock.AddSeconds(4);
        tracker.HandleLine("Sending sparse 'super' 2/4 (102400 KB)");

        // 100 MiB over 4 s. This is a wall-clock estimate, so the injectable clock
        // is what makes it testable at all.
        Assert.Equal(25.0, progress[^1].SpeedMBs!.Value, 2);
    }

    [Fact]
    public void Sparse_chunks_arriving_too_fast_yield_no_rate()
    {
        var progress = new List<FlashProgressInfo>();
        var clock = new DateTime(2026, 10, 31, 12, 0, 0);
        var tracker = new FlashProgressTracker(info => progress.Add(info), () => clock);

        tracker.HandleLine("Sending sparse 'super' 1/4 (102400 KB)");
        clock = clock.AddMilliseconds(50);
        tracker.HandleLine("Sending sparse 'super' 2/4 (102400 KB)");

        // A sub-100 ms interval would divide into an absurd rate.
        Assert.Null(progress[^1].SpeedMBs);
    }

    // ══════════════════════════════════════════════════════════════
    //  bootloader-supplied percentage
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void The_bootloader_relay_form_is_now_matched()
    {
        // fastboot relays bootloader INFO text as "(bootloader) %s" — verified as the
        // only such format string in the shipped binary. A bootloader pushing "47%"
        // therefore arrives in exactly this shape.
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine("(bootloader) 47%");

        Assert.Equal(47, progress[0].Percent);
        Assert.True(tracker.SawIntermediateProgress);
    }

    [Theory]
    [InlineData("(47%", 47)]
    [InlineData("(bootloader) 47%", 47)]
    [InlineData("(  5.0%)", 5)]
    [InlineData("( 65.0%)", 65)]
    [InlineData("(100.0%)", 100)]
    public void Percentage_forms_are_all_accepted(string line, int expected)
    {
        // Right-alignment padding matters: fastboot pads below three digits, so a
        // pattern without \s* inside the parens matches nothing for most of the
        // transfer.
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine(line);

        Assert.Equal(expected, progress[0].Percent);
    }

    [Theory]
    [InlineData("(0%", 0)]
    [InlineData("(5%", 5)]
    [InlineData("(100%", 100)]
    public void A_matched_percentage_is_reported_verbatim(string line, int expected)
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine(line);

        Assert.Equal(expected, progress[0].Percent);
    }

    [Fact]
    public void A_percentage_out_of_range_is_clamped()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine("(150%");
        tracker.HandleLine("(-3%");

        Assert.Equal(100, progress[0].Percent);
        // "-3%" has no paren before the digits, so the pattern does not fire and
        // nothing is emitted rather than a spurious 0% reaching the bar.
        Assert.Single(progress);
    }

    [Fact]
    public void A_bootloader_percentage_does_not_suppress_the_okay_estimate()
    {
        // The percentage path gives no rate, so the timing estimate is still the only
        // speed available. Both are worth showing.
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine("Sending 'boot' (98304 KB)");
        tracker.HandleLine("(bootloader) 50%");
        tracker.HandleLine("OKAY [  4.000s]");

        Assert.Contains(progress, p => p.SpeedMBs is { } s && Math.Abs(s - 24.0) < 0.01);
    }

    // ══════════════════════════════════════════════════════════════
    //  the OKAY timing fallback
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Okay_timing_derives_speed_from_the_sending_size()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        // 98304 KB = 96 MiB; over 4 s that is 24 MB/s. This is the only speed an
        // official-build user ever sees on a single-chunk image.
        tracker.HandleLine("Sending 'boot' (98304 KB)");
        tracker.HandleLine("OKAY [  4.000s]");

        var info = progress[^1];
        Assert.Equal(100, info.Percent);
        Assert.Equal(24.0, info.SpeedMBs!.Value, 2);
    }

    [Fact]
    public void Okay_without_a_preceding_sending_line_reports_100_percent_and_no_speed()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine("OKAY [  0.202s]");

        var info = Assert.Single(progress);
        Assert.Equal(100, info.Percent);
        Assert.Null(info.SpeedMBs);
    }

    [Fact]
    public void Okay_timing_below_the_threshold_reports_no_speed()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        // A 20 ms write yields a wildly inflated rate; it is suppressed instead.
        tracker.HandleLine("Sending 'vbmeta' (8 KB)");
        tracker.HandleLine("OKAY [  0.020s]");

        var info = progress[^1];
        Assert.Equal(100, info.Percent);
        Assert.Null(info.SpeedMBs);
    }

    [Fact]
    public void Okay_timing_requires_a_real_elapsed_value()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine("Sending 'boot' (98304 KB)");
        tracker.HandleLine("OKAY");

        var info = progress[^1];
        Assert.Equal(100, info.Percent);
        Assert.Null(info.SpeedMBs);
    }

    [Fact]
    public void Derived_speed_fires_once_per_sending_phase()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        // Only a "Sending" header opens a phase, so the OKAY that closes the write
        // step must not re-derive a second speed from the stale byte count.
        tracker.HandleLine("Sending 'boot' (98304 KB)");
        tracker.HandleLine("OKAY [  4.000s]");
        tracker.HandleLine("Writing 'boot'");
        tracker.HandleLine("OKAY [  1.000s]");

        var withSpeed = progress.Where(p => p.SpeedMBs.HasValue).ToList();
        Assert.Single(withSpeed);
        Assert.Equal(24.0, withSpeed[0].SpeedMBs!.Value, 2);
        Assert.Equal(100, progress[^1].Percent);
    }

    [Fact]
    public void A_second_sending_phase_derives_its_own_speed()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine("Sending 'boot' (98304 KB)");
        tracker.HandleLine("OKAY [  4.000s]");
        tracker.HandleLine("Sending 'dtbo' (1024 KB)");
        tracker.HandleLine("OKAY [  1.000s]");

        var withSpeed = progress.Where(p => p.SpeedMBs.HasValue).ToList();
        Assert.Equal(2, withSpeed.Count);
        Assert.Equal(24.0, withSpeed[0].SpeedMBs!.Value, 2);
        Assert.Equal(1.0, withSpeed[1].SpeedMBs!.Value, 2);
    }

    // ══════════════════════════════════════════════════════════════
    //  the write phase
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void A_writing_line_alone_moves_to_the_write_phase()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine("Sending 'boot' (98304 KB)");
        tracker.HandleLine("Writing 'boot'");

        var info = progress[^1];
        Assert.Equal(FlashPhase.Writing, info.Phase);
        Assert.Equal(100, info.Percent);
    }

    [Fact]
    public void The_write_phase_is_reported_once_per_writing_line()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine("Writing 'boot'");
        tracker.HandleLine("Writing 'dtbo'");

        Assert.Equal(2, progress.Count(p => p.Phase == FlashPhase.Writing));
    }

    // ══════════════════════════════════════════════════════════════
    //  terminal status
    // ══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("FAILED (remote: 'Partition not found')")]
    [InlineData("error: unknown partition")]
    public void Failure_reports_minus_one(string line)
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine(line);

        var info = progress[0];
        Assert.Equal(-1, info.Percent);
        Assert.Equal(FlashPhase.Failed, info.Phase);
    }

    [Fact]
    public void Failure_after_partial_progress_still_reports_minus_one()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine("Sending sparse 'super' 2/4 (524288 KB)");
        tracker.HandleLine("FAILED (remote: 'Write failed')");

        Assert.Equal(50, progress[0].Percent);
        Assert.Equal(-1, progress[^1].Percent);
    }

    [Fact]
    public void Failure_preserves_the_byte_counts_seen_so_far()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine("Sending 'boot' (98304 KB)");
        tracker.HandleLine("FAILED (remote: 'Write failed')");

        // Useful in a bug report: how far the transfer got before it died.
        Assert.Equal(98304L * 1024, progress[^1].TotalBytes);
    }

    [Fact]
    public void Finished_line_is_treated_as_success()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine("finished. total time: 12.441s");

        Assert.Equal(100, progress[^1].Percent);
        Assert.Equal(FlashPhase.Finished, progress[^1].Phase);
    }

    [Fact]
    public void Success_after_real_progress_does_not_re_emit_100()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine("(bootloader) 50%");
        tracker.HandleLine("(bootloader) 100%");
        tracker.HandleLine("OKAY [  1.000s]");

        // Three lines, three callbacks — the OKAY does not append a fourth, which
        // would have made the bar visibly re-draw at completion.
        Assert.Equal(2, progress.Count);
        Assert.All(progress, p => Assert.True(p.Percent > 0));
    }

    // ══════════════════════════════════════════════════════════════
    //  noise and ordering
    // ══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("")]
    [InlineData("< waiting for any device >")]
    [InlineData("Target: shennong")]
    [InlineData("Rewriting vbmeta struct...")]
    [InlineData("fastboot: warning: skipping 0-byte partition")]
    public void Lines_that_carry_no_progress_emit_nothing(string line)
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine(line);

        Assert.Empty(progress);
    }

    [Fact]
    public void SawIntermediateProgress_stays_false_when_only_a_terminal_status_arrived()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine("OKAY [  0.202s]");

        // The flag distinguishes "the device reported progress" from "we inferred
        // completion from the status line". Both end at 100%, so this is the only way
        // a caller can tell them apart.
        Assert.False(tracker.SawIntermediateProgress);
        Assert.Equal(100, progress[0].Percent);
    }

    [Fact]
    public void SawIntermediateProgress_becomes_true_once_any_percentage_is_seen()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        Assert.False(tracker.SawIntermediateProgress);
        tracker.HandleLine("(5%");
        Assert.True(tracker.SawIntermediateProgress);
    }

    [Fact]
    public void A_null_callback_is_tolerated()
    {
        // The flash page passes no progress handler when only the log is shown.
        var tracker = new FlashProgressTracker(null);

        foreach (var line in OfficialFastbootFlashOutput)
            tracker.HandleLine(line);

        // The sequence contains no intermediate percentage, so nothing was seen —
        // and more importantly, the tracker never threw on the null callback.
        Assert.False(tracker.SawIntermediateProgress);
    }

    [Fact]
    public void A_full_official_build_sequence_ends_at_100_percent()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        foreach (var line in OfficialFastbootFlashOutput)
            tracker.HandleLine(line);

        Assert.Equal(100, progress[^1].Percent);
        Assert.Equal(FlashPhase.Finished, progress[^1].Phase);
        Assert.DoesNotContain(progress, p => p.Percent == -1);
    }

    [Fact]
    public void An_official_build_reports_an_average_rate_at_the_end()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        foreach (var line in OfficialFastbootFlashOutput)
            tracker.HandleLine(line);

        // 98304 KB over 2.626 s. Nothing intermediate is available from this build, so
        // the average over the whole transfer is the honest figure.
        var withSpeed = progress.Where(p => p.SpeedMBs.HasValue).ToList();
        Assert.NotEmpty(withSpeed);
        Assert.Equal(98304 / 1024.0 / 2.626, withSpeed[0].SpeedMBs!.Value, 2);
    }

    /// <summary>
    /// Verbatim stderr from <c>fastboot flash boot boot.img</c> on an official
    /// Google build against a bootloader that reports no percentage — the case the
    /// OKAY-timing fallback exists for.
    /// </summary>
    private static readonly string[] OfficialFastbootFlashOutput =
    [
        "Sending 'boot' (98304 KB)",
        "OKAY [  2.626s]",
        "Writing 'boot'",
        "OKAY [  0.412s]",
        "Finished. Total time: 3.076s",
    ];
}
