using SailTool.Models;
using SailTool.Services;
using Xunit;

namespace SailTool.Tests.Services;

/// <summary>
/// Covers <see cref="FlashProgressTracker"/>: the state machine that turns
/// fastboot's stderr stream into the progress the UI draws.
/// </summary>
/// <remarks>
/// <para>
/// Feeding recorded lines through <see cref="FlashProgressTracker.HandleLine"/>
/// exercises the real sequence logic without spawning fastboot, which is what the
/// extracted class was for — it used to be a local function inside
/// <c>FlashImageAsync</c> and could not be tested at all.
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
    //  sparse chunking — the only real intermediate progress stock
    //  fastboot reports for a large image
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Sparse_chunks_report_percentage_of_the_total_chunk_count()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        // Real output from flashing a super partition split into 4 chunks.
        tracker.HandleLine("Sending sparse 'super' 1/4 (524288 KB)");
        tracker.HandleLine("Writing 'super'");
        tracker.HandleLine("OKAY [ 12.441s]");
        tracker.HandleLine("Sending sparse 'super' 2/4 (524288 KB)");

        Assert.Equal(25, progress[0].Percent);
        Assert.Equal(50, progress[^1].Percent);
    }

    [Fact]
    public void Sparse_percentage_never_reaches_100_before_OKAY()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        // The final chunk computes to exactly 100% but is clamped to 99, so the
        // bar does not read "done" while the write phase is still running.
        tracker.HandleLine("Sending sparse 'super' 4/4 (524288 KB)");

        Assert.Equal(99, progress[0].Percent);
    }

    [Fact]
    public void Sparse_progress_truncates_rather_than_rounds()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        // 2/3 is 66.67%; an integer cast gives 66. Rounding would show 67 and then
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

        // 1/13 truncates to 7. Zero would leave the bar visibly empty on the first
        // of thirteen chunks, which reads as "nothing happened".
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

    // ══════════════════════════════════════════════════════════════
    //  bootloader-supplied percentage
    //
    //  KNOWN DEFECT, pre-existing and preserved by the refactor.
    //
    //  ProgressPercentRegex is @"\((\d{1,3})%", i.e. an open paren immediately
    //  followed by digits. The XML doc on it says it matches "(bootloader) 5%",
    //  but it cannot: there is text between the paren and the digits. Verified
    //  against the shipped binary — tools/platform-tools/fastboot.exe contains
    //  exactly one bootloader-line format string, "(bootloader) %s", which is
    //  how fastboot relays whatever INFO text the bootloader sends. So a
    //  bootloader pushing "5%" arrives as "(bootloader) 5%" and is not matched.
    //
    //  These tests therefore pin what the code DOES, not what the doc claims.
    //  Changing the pattern would alter behaviour, which needs approval.
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Bootloader_percentage_is_matched_only_when_the_paren_is_immediately_before_the_digits()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine("(47%");

        Assert.Equal(47, progress[0].Percent);
    }

    [Fact]
    public void The_real_bootloader_relay_form_is_not_matched()
    {
        // Pins the defect above: this is the shape fastboot actually prints, and
        // it produces no callback, so such a device shows an indeterminate bar
        // for the whole transfer.
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine("(bootloader) 47%");

        Assert.Empty(progress);
        Assert.False(tracker.GotFinalPct);
    }

    [Theory]
    [InlineData("(0%", 0)]
    [InlineData("(5%", 5)]
    [InlineData("(47%", 47)]
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

    // ══════════════════════════════════════════════════════════════
    //  the vendor rate path — documented as unreachable on stock
    //  fastboot, but still exercised because vendor builds use it
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Vendor_rate_line_reports_both_percentage_and_megabytes_per_second()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine("Sending 'vendor_boot' (98304 KB)   45.2%  37.05 MB/s");

        var info = Assert.Single(progress);
        Assert.Equal(45, info.Percent);
        Assert.Equal(37.05, info.SpeedMBs!.Value, 2);
    }

    [Theory]
    [InlineData("Sending 'super' (2097152 KB)   12.3%  1024.00 KB/s", 1.0)]
    [InlineData("Sending 'super' (2097152 KB)   12.3%  1.50 GB/s", 1536.0)]
    [InlineData("Sending 'super' (2097152 KB)   12.3%  37.05 MB/s", 37.05)]
    public void Vendor_rate_units_are_normalised_to_megabytes_per_second(
        string line, double expectedMBs)
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine(line);

        Assert.Equal(expectedMBs, progress[0].SpeedMBs!.Value, 2);
    }

    [Fact]
    public void Vendor_rate_suppresses_the_OKAY_timing_fallback()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine("Sending 'boot' (98304 KB)   45.2%  37.05 MB/s");
        tracker.HandleLine("OKAY [  2.626s]");

        // The native rate is more accurate than size-over-elapsed, so the OKAY
        // line must not emit a second, contradicting speed.
        Assert.Single(progress);
        Assert.Equal(37.05, progress[0].SpeedMBs!.Value, 2);
    }

    [Fact]
    public void Stock_fastboot_output_never_matches_the_vendor_rate_pattern()
    {
        // Pins the investigation recorded on FastbootOutputParser.ProgressSpeedRegex:
        // stock fastboot emits no rate, so this path stays dormant. If a future
        // fastboot starts printing speeds, this fails and the comment gets updated
        // rather than the assumption quietly going stale.
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        foreach (var line in StockFastbootFlashOutput)
            tracker.HandleLine(line);

        Assert.DoesNotContain(progress, p => p.SpeedMBs.HasValue && p.Percent > 0 && p.Percent < 100);
    }

    // ══════════════════════════════════════════════════════════════
    //  the OKAY timing fallback
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Okay_timing_derives_speed_from_the_sending_size()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        // 98304 KB = 96 MiB; over 4 s that is 24 MB/s. This is the only speed a
        // stock-fastboot user ever sees on a single-chunk image.
        tracker.HandleLine("Sending 'boot' (98304 KB)");
        tracker.HandleLine("Writing 'boot'");
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

        Assert.Equal(-1, progress[0].Percent);
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
    public void Finished_line_is_treated_as_success()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine("finished. total time: 12.441s");

        Assert.Equal(100, progress[0].Percent);
    }

    [Fact]
    public void Success_after_real_progress_does_not_re_emit_100()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        // Uses the matched paren form rather than "(bootloader) 50%", which the
        // pattern does not accept — see the defect note above.
        tracker.HandleLine("(50%");
        tracker.HandleLine("(100%");
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
    [InlineData("Writing 'boot'")]
    public void Lines_that_carry_no_progress_emit_nothing(string line)
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine(line);

        Assert.Empty(progress);
    }

    [Fact]
    public void GotFinalPct_stays_false_when_only_a_terminal_status_arrived()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine("OKAY [  0.202s]");

        // The flag is what distinguishes "we saw real progress" from "we inferred
        // completion from the exit status".
        Assert.False(tracker.GotFinalPct);
    }

    [Fact]
    public void GotFinalPct_becomes_true_once_any_percentage_is_seen()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        Assert.False(tracker.GotFinalPct);
        tracker.HandleLine("(5%");
        Assert.True(tracker.GotFinalPct);
    }

    [Fact]
    public void A_null_callback_is_tolerated()
    {
        // The flash page passes no progress handler when only the log is shown.
        var tracker = new FlashProgressTracker(null);

        foreach (var line in StockFastbootFlashOutput)
            tracker.HandleLine(line);

        Assert.True(tracker.GotFinalPct);
    }

    [Fact]
    public void A_full_stock_flash_sequence_ends_at_100_percent()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        foreach (var line in StockFastbootFlashOutput)
            tracker.HandleLine(line);

        Assert.Equal(100, progress[^1].Percent);
        Assert.DoesNotContain(progress, p => p.Percent == -1);
    }

    /// <summary>
    /// Verbatim stderr from <c>fastboot flash boot boot.img</c> on a device whose
    /// bootloader reports no percentage — the common case, and the reason the OKAY
    /// timing fallback exists.
    /// </summary>
    private static readonly string[] StockFastbootFlashOutput =
    [
        "Sending 'boot' (98304 KB)",
        "OKAY [  2.626s]",
        "Writing 'boot'",
        "OKAY [  0.412s]",
        "Finished. Total time: 3.076s",
    ];
}
