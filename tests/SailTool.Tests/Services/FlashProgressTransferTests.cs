using SailTool.Models;
using SailTool.Services;
using Xunit;

namespace SailTool.Tests.Services;

/// <summary>
/// Covers the transfer-progress path: the lines a progress-capable fastboot build
/// emits, the two throttles that sit in front of the UI, and the byte-scan that
/// decides whether that path is available at all.
/// </summary>
/// <remarks>
/// <para>
/// The sample lines are copied from real output recorded against the previous
/// shipped binary, the 35.0.2-eng build (see <c>tools/platform-tools/PROVENANCE.md</c>),
/// including its right-aligned percentage padding. That padding is the reason the
/// percent pattern tolerates whitespace: without it every value below 10% fails to
/// match and the bar sits still through the first tenth of every transfer. The
/// build shipped today draws a bar instead and is covered by
/// <see cref="FastbootBarProgressTests"/>; these cases pin the older format so
/// swapping that binary back in still parses.
/// </para>
/// </remarks>
public class FlashProgressTransferTests
{
    /// <summary>
    /// Verbatim stderr from flashing a 100 MB recovery partition with the
    /// progress-capable build. Percentages are right-aligned in a five-character
    /// field, so the padding varies from line to line.
    /// </summary>
    private static readonly string[] RecoveryFlash =
    [
        "Sending 'recovery' (102400 KB)",
        "recovery:   5.0 MB/100.0 MB (  5.0%) [raw] 26.40 MB/s",
        "recovery:  10.0 MB/100.0 MB ( 10.0%) [raw] 33.10 MB/s",
        "recovery:  45.0 MB/100.0 MB ( 45.0%) [raw] 40.10 MB/s",
        "recovery:  65.0 MB/100.0 MB ( 65.0%) [raw] 40.22 MB/s",
        "recovery:  95.0 MB/100.0 MB ( 95.0%) [raw] 40.09 MB/s",
        "recovery: 100.0 MB/100.0 MB (100.0%) [raw] 40.10 MB/s",
        "OKAY [  2.496s]",
        "Writing 'recovery'                                 OKAY [  0.030s]",
        "Finished. Total time: 2.573s",
    ];

    private static FlashProgressTracker CreateTracker(List<FlashProgressInfo> sink)
        => new(info => sink.Add(info));

    // ══════════════════════════════════════════════════════════════
    //  the transfer progress line
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Transfer_line_yields_percent_speed_and_both_byte_counts()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine("recovery:  65.0 MB/100.0 MB ( 65.0%) [raw] 40.22 MB/s");

        var info = Assert.Single(progress);
        Assert.Equal(65, info.Percent);
        Assert.Equal(40.22, info.SpeedMBs!.Value, 2);
        Assert.Equal((long)(65.0 * 1024 * 1024), info.TransferredBytes);
        Assert.Equal(100L * 1024 * 1024, info.TotalBytes);
        Assert.Equal(FlashPhase.Sending, info.Phase);
    }

    [Theory]
    [InlineData("recovery:   5.0 MB/100.0 MB (  5.0%) [raw] 26.40 MB/s", 5)]
    [InlineData("recovery:  65.0 MB/100.0 MB ( 65.0%) [raw] 40.22 MB/s", 65)]
    [InlineData("recovery: 100.0 MB/100.0 MB (100.0%) [raw] 40.10 MB/s", 100)]
    public void Right_aligned_percentage_padding_is_tolerated(string line, int expectedPercent)
    {
        // fastboot right-aligns the percentage, so sub-10% values arrive with two
        // leading spaces and sub-100% with one. A pattern without \s* inside the
        // parens matches nothing until the transfer is nearly over.
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine(line);

        Assert.Equal(expectedPercent, progress[0].Percent);
    }

    [Fact]
    public void Transfer_line_without_a_rate_still_reports_percent_and_bytes()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine("boot: 32.0 MB/64.0 MB ( 50.0%) [raw]");

        var info = Assert.Single(progress);
        Assert.Equal(50, info.Percent);
        Assert.Null(info.SpeedMBs);
        Assert.Equal((long)(32.0 * 1024 * 1024), info.TransferredBytes);
    }

    [Fact]
    public void Sparse_kind_marker_is_accepted()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine("super: 512.0 MB/2048.0 MB ( 25.0%) [sparse] 38.00 MB/s");

        var info = Assert.Single(progress);
        Assert.Equal(25, info.Percent);
        Assert.Equal(38.00, info.SpeedMBs!.Value, 2);
    }

    [Theory]
    [InlineData("boot: 1.0 GB/2.0 GB ( 50.0%) [raw] 30.00 MB/s", 1024L * 1024 * 1024)]
    [InlineData("boot: 512.0 KB/1024.0 KB ( 50.0%) [raw] 1.00 MB/s", 512L * 1024)]
    [InlineData("boot: 500.0 B/1000.0 B ( 50.0%) [raw] 1.00 MB/s", 500L)]
    public void Size_units_are_converted_to_bytes(string line, long expectedCur)
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine(line);

        Assert.Equal(expectedCur, progress[0].TransferredBytes);
    }

    [Theory]
    [InlineData("boot: 10.0 MB/100.0 MB ( 10.0%) [raw] 1.00 GB/s", 1024.0)]
    [InlineData("boot: 10.0 MB/100.0 MB ( 10.0%) [raw] 1024.00 KB/s", 1.0)]
    [InlineData("boot: 10.0 MB/100.0 MB ( 10.0%) [raw] 1048576.00 B/s", 1.0)]
    [InlineData("boot: 10.0 MB/100.0 MB ( 10.0%) [raw] 37.05 MB/s", 37.05)]
    public void Rate_units_are_normalised_to_megabytes_per_second(string line, double expected)
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine(line);

        Assert.Equal(expected, progress[0].SpeedMBs!.Value, 2);
    }

    [Fact]
    public void Comma_decimal_separator_is_accepted()
    {
        // fastboot formats with %.1f under the C locale, but a build that honours
        // the user's locale would emit a comma. Parsing with the current culture
        // would either fail or read "100,0" as 1000.
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine("boot: 65,0 MB/100,0 MB ( 65,0%) [raw] 40,22 MB/s");

        var info = Assert.Single(progress);
        Assert.Equal(65, info.Percent);
        Assert.Equal(40.22, info.SpeedMBs!.Value, 2);
    }

    [Fact]
    public void Partition_name_containing_a_colon_is_captured_whole()
    {
        // vendor_boot:RAMDISK is a real flash target; a greedy partition group
        // would stop at the first colon and fail to match the line at all.
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine("vendor_boot:RAMDISK: 16.0 MB/32.0 MB ( 50.0%) [raw] 20.00 MB/s");

        var info = Assert.Single(progress);
        Assert.Equal(50, info.Percent);
        Assert.Equal(20.00, info.SpeedMBs!.Value, 2);
    }

    // ══════════════════════════════════════════════════════════════
    //  monotonic cursors
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Percent_never_goes_backwards()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine("boot: 80.0 MB/100.0 MB ( 80.0%) [raw] 40.00 MB/s");
        tracker.HandleLine("boot: 10.0 MB/100.0 MB ( 10.0%) [raw] 40.00 MB/s");

        Assert.Equal(80, progress[0].Percent);
        // The second line is reported as-is to the callback (the tracker emits on
        // every state change) but the bar value must not have regressed.
        Assert.Equal(80, progress[^1].Percent);
    }

    [Fact]
    public void Byte_cursor_never_goes_backwards()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine("super: 900.0 MB/1000.0 MB ( 90.0%) [sparse] 40.00 MB/s");
        // A sparse chunk restarts its own counter from zero.
        tracker.HandleLine("super: 10.0 MB/1000.0 MB ( 1.0%) [sparse] 40.00 MB/s");

        Assert.True(progress[^1].TransferredBytes >= progress[0].TransferredBytes);
    }

    [Fact]
    public void Repeated_identical_line_still_emits_so_the_throttle_is_what_suppresses_it()
    {
        // Separation of concerns: the tracker reports every line it can parse and
        // the throttle decides what the UI renders. Keeping the tracker unconditional
        // means its cursors stay correct even when the UI drops frames.
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        const string line = "boot: 50.0 MB/100.0 MB ( 50.0%) [raw] 40.00 MB/s";
        tracker.HandleLine(line);
        tracker.HandleLine(line);
        tracker.HandleLine(line);

        Assert.Equal(3, progress.Count);
    }

    [Fact]
    public void The_sending_header_total_wins_over_a_smaller_sparse_chunk_total()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine("Sending 'super' (2097152 KB)");          // 2 GiB whole image
        tracker.HandleLine("super: 100.0 MB/512.0 MB ( 19.5%) [sparse] 40.00 MB/s");

        // 512 MB is one chunk, not the image; reporting it as the total would make
        // the ETA wrong by a factor of four.
        Assert.Equal(2097152L * 1024, progress[^1].TotalBytes);
    }

    // ══════════════════════════════════════════════════════════════
    //  phases
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Writing_line_moves_to_the_write_phase_at_100_percent()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine("Sending 'recovery' (102400 KB)");
        tracker.HandleLine("Writing 'recovery'                                 OKAY [  0.030s]");

        var info = progress[^1];
        Assert.Equal(FlashPhase.Writing, info.Phase);
        Assert.Equal(100, info.Percent);
        // The byte cursor is filled to the total, because the data has been received.
        Assert.Equal(info.TotalBytes, info.TransferredBytes);
    }

    [Fact]
    public void Finished_line_closes_the_flash()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        foreach (var line in RecoveryFlash) tracker.HandleLine(line);

        Assert.Equal(FlashPhase.Finished, progress[^1].Phase);
        Assert.Equal(100, progress[^1].Percent);
    }

    [Fact]
    public void Finished_is_emitted_once()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine("Writing 'recovery'                                 OKAY [  0.030s]");
        tracker.HandleLine("Finished. Total time: 2.573s");
        tracker.HandleLine("Finished. Total time: 2.573s");

        Assert.Equal(1, progress.Count(p => p.Phase == FlashPhase.Finished));
    }

    [Fact]
    public void A_native_rate_suppresses_the_okay_timing_estimate()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        // 102400 KB over 2.496 s would derive to ~40.05 MB/s; the reported 40.22 is
        // what must survive, because it is the instantaneous USB rate.
        tracker.HandleLine("Sending 'recovery' (102400 KB)");
        tracker.HandleLine("recovery:  65.0 MB/100.0 MB ( 65.0%) [raw] 40.22 MB/s");
        tracker.HandleLine("OKAY [  2.496s]");

        Assert.DoesNotContain(progress, p =>
            p.SpeedMBs is { } s && Math.Abs(s - 40.22) > 0.01 && p.Percent == 100);
    }

    [Fact]
    public void A_second_partition_derives_its_own_okay_timing_estimate()
    {
        // The rate is cleared on each Sending header, so the first partition's
        // figure does not suppress the second partition's estimate.
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        tracker.HandleLine("Sending 'boot' (98304 KB)");
        tracker.HandleLine("OKAY [  4.000s]");
        tracker.HandleLine("Sending 'dtbo' (1024 KB)");
        tracker.HandleLine("OKAY [  1.000s]");

        var withSpeed = progress.Where(p => p.SpeedMBs.HasValue).ToList();
        Assert.Equal(2, withSpeed.Count);
        Assert.Equal(24.0, withSpeed[0].SpeedMBs!.Value, 2);   // 96 MiB / 4 s
        Assert.Equal(1.0, withSpeed[1].SpeedMBs!.Value, 2);    // 1 MiB / 1 s
    }

    [Fact]
    public void A_recorded_recovery_flash_ends_at_100_percent_with_no_failure()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);

        foreach (var line in RecoveryFlash) tracker.HandleLine(line);

        Assert.Equal(100, progress[^1].Percent);
        Assert.DoesNotContain(progress, p => p.Percent == -1);
        Assert.Contains(progress, p => p.Phase == FlashPhase.Writing);
        Assert.Contains(progress, p => p.Phase == FlashPhase.Finished);
    }

    // ══════════════════════════════════════════════════════════════
    //  the progress throttle
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Throttle_passes_the_first_update()
    {
        long now = 0;
        var throttle = new ProgressThrottle(() => now);

        Assert.True(throttle.ShouldRender(new FlashProgressInfo(5, 40.0)));
    }

    [Fact]
    public void Throttle_drops_an_identical_repeat_regardless_of_elapsed_time()
    {
        long now = 0;
        var throttle = new ProgressThrottle(() => now);
        var info = new FlashProgressInfo(5, 40.0);

        Assert.True(throttle.ShouldRender(info));
        now = 60_000;
        Assert.False(throttle.ShouldRender(info));
    }

    [Fact]
    public void Throttle_drops_a_changed_update_inside_the_interval()
    {
        long now = 0;
        var throttle = new ProgressThrottle(() => now);

        Assert.True(throttle.ShouldRender(new FlashProgressInfo(5, 40.0)));
        now = ProgressThrottle.IntervalMs - 1;
        Assert.False(throttle.ShouldRender(new FlashProgressInfo(6, 40.0)));
    }

    [Fact]
    public void Throttle_passes_a_changed_update_once_the_interval_has_elapsed()
    {
        long now = 0;
        var throttle = new ProgressThrottle(() => now);

        Assert.True(throttle.ShouldRender(new FlashProgressInfo(5, 40.0)));
        now = ProgressThrottle.IntervalMs;
        Assert.True(throttle.ShouldRender(new FlashProgressInfo(6, 40.0)));
    }

    [Fact]
    public void Throttle_interval_is_100ms()
    {
        // Pinned because the value is a product decision, not an accident: 100 ms
        // keeps the bar and the one-line log moving at ten updates a second, which
        // a fast transfer fills, while a slow one still collapses to a few lines.
        Assert.Equal(100, ProgressThrottle.IntervalMs);
        Assert.Equal(100, ProgressLogGate.IntervalMs);
    }

    [Theory]
    [InlineData(FlashPhase.Writing)]
    [InlineData(FlashPhase.Finished)]
    [InlineData(FlashPhase.Failed)]
    public void Terminal_phases_bypass_the_interval(FlashPhase phase)
    {
        long now = 0;
        var throttle = new ProgressThrottle(() => now);

        Assert.True(throttle.ShouldRender(new FlashProgressInfo(50, 40.0, Phase: FlashPhase.Sending)));
        // Immediately afterwards: a completion or failure must not wait up to 100 ms,
        // which would read as the flash having hung at the end.
        Assert.True(throttle.ShouldRender(new FlashProgressInfo(50, 40.0, Phase: phase)));
    }

    [Fact]
    public void Failure_bypasses_the_interval_even_at_the_same_percent()
    {
        long now = 0;
        var throttle = new ProgressThrottle(() => now);

        Assert.True(throttle.ShouldRender(new FlashProgressInfo(50, 40.0)));
        Assert.True(throttle.ShouldRender(new FlashProgressInfo(-1, Phase: FlashPhase.Failed)));
    }

    [Fact]
    public void Completion_at_100_percent_bypasses_the_interval()
    {
        long now = 0;
        var throttle = new ProgressThrottle(() => now);

        Assert.True(throttle.ShouldRender(new FlashProgressInfo(99, 40.0)));
        Assert.True(throttle.ShouldRender(new FlashProgressInfo(100, 40.0)));
    }

    [Fact]
    public void A_bypass_resets_the_window_so_the_next_update_is_not_delayed_by_it()
    {
        long now = 0;
        var throttle = new ProgressThrottle(() => now);

        throttle.ShouldRender(new FlashProgressInfo(50, 40.0, Phase: FlashPhase.Sending));
        // Terminal bypass at t=0.
        throttle.ShouldRender(new FlashProgressInfo(100, 40.0, Phase: FlashPhase.Finished));
        // A later non-terminal update must still respect the interval measured from
        // the bypass, not sail through because the bypass did not count as a render.
        now = ProgressThrottle.IntervalMs - 1;
        Assert.False(throttle.ShouldRender(new FlashProgressInfo(51, 41.0, Phase: FlashPhase.Sending)));
        now = ProgressThrottle.IntervalMs;
        Assert.True(throttle.ShouldRender(new FlashProgressInfo(51, 41.0, Phase: FlashPhase.Sending)));
    }

    [Fact]
    public void Throttle_over_a_recorded_flash_reduces_updates_by_an_order_of_magnitude()
    {
        // The point of the throttle: a fast transfer produces one progress callback
        // per 1 MiB chunk — 25 ms apart at 40 MiB/s — and a multi-GB super partition
        // produces thousands of them.
        var progress = new List<FlashProgressInfo>();
        var tracker = CreateTracker(progress);
        foreach (var line in RecoveryFlash) tracker.HandleLine(line);

        long now = 0;
        var throttle = new ProgressThrottle(() => now);
        var rendered = 0;
        foreach (var info in progress)
        {
            // Simulate that cadence: each line ~25 ms apart.
            now += 25;
            if (throttle.ShouldRender(info)) rendered++;
        }

        Assert.True(progress.Count >= 6, "expected several parsed progress lines");
        Assert.True(rendered < progress.Count,
            $"throttle should drop frames: {rendered} rendered of {progress.Count}");
    }

    // ══════════════════════════════════════════════════════════════
    //  the log gate
    // ══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("OKAY [  2.496s]")]
    [InlineData("Writing 'recovery'                                 OKAY [  0.030s]")]
    [InlineData("Finished. Total time: 2.573s")]
    [InlineData("FAILED (remote: 'Partition not found')")]
    [InlineData("< waiting for any device >")]
    [InlineData("Target: shennong")]
    [InlineData("(bootloader) version: 0.5")]
    public void Non_progress_lines_always_pass(string line)
    {
        long now = 0;
        var gate = new ProgressLogGate(() => now);

        // Repeated, and inside the interval: these are the lines worth reading back
        // and they arrive at human pace, so none of them may be dropped.
        Assert.True(gate.ShouldLog(line));
        Assert.True(gate.ShouldLog(line));
        Assert.True(gate.ShouldLog(line));
    }

    [Fact]
    public void The_first_sending_and_sparse_headers_pass_immediately()
    {
        long now = 0;
        var gate = new ProgressLogGate(() => now);

        // A single-partition flash emits one Sending header, and it must reach the
        // log — that is the line telling the user what is being written.
        Assert.True(gate.ShouldLog("Sending 'recovery' (102400 KB)"));
    }

    [Fact]
    public void Sending_headers_are_sampled_during_a_sparse_flash()
    {
        // A 4 GB super image split into 40 chunks produces 80 of these lines in a
        // few seconds, and a line-flash script runs several partitions back to back.
        // Sampling them is the reason the panel stays readable.
        long now = 0;
        var gate = new ProgressLogGate(() => now);

        var logged = 0;
        for (var i = 1; i <= 40; i++)
        {
            now += 10;
            if (gate.ShouldLog($"Sending sparse 'super' {i}/40 (102400 KB)")) logged++;
        }

        Assert.True(logged < 40, $"expected sampling, got {logged} of 40");
        Assert.True(logged > 0);
    }

    [Fact]
    public void Progress_lines_are_sampled_at_the_interval()
    {
        long now = 0;
        var gate = new ProgressLogGate(() => now);

        Assert.True(gate.ShouldLog("recovery:   5.0 MB/100.0 MB (  5.0%) [raw] 26.40 MB/s"));
        now = ProgressLogGate.IntervalMs - 1;
        Assert.False(gate.ShouldLog("recovery:  10.0 MB/100.0 MB ( 10.0%) [raw] 33.10 MB/s"));
        now = ProgressLogGate.IntervalMs;
        Assert.True(gate.ShouldLog("recovery:  15.0 MB/100.0 MB ( 15.0%) [raw] 36.80 MB/s"));
    }

    [Fact]
    public void An_identical_progress_line_is_dropped_even_after_the_interval()
    {
        long now = 0;
        var gate = new ProgressLogGate(() => now);
        const string line = "recovery:  65.0 MB/100.0 MB ( 65.0%) [raw] 40.22 MB/s";

        Assert.True(gate.ShouldLog(line));
        now = 60_000;
        Assert.False(gate.ShouldLog(line));
    }

    [Fact]
    public void Padding_differences_are_not_treated_as_a_state_change()
    {
        // fastboot re-emits the same progress with different alignment as the
        // percentage widens. Comparing raw text would log a "change" that is only
        // whitespace and defeat the gate entirely during the first tenth.
        long now = 0;
        var gate = new ProgressLogGate(() => now);

        Assert.True(gate.ShouldLog("recovery:   5.0 MB/100.0 MB ( 5.0%) [raw] 26.40 MB/s"));
        now = 60_000;
        Assert.False(gate.ShouldLog("recovery:  5.0 MB/100.0 MB (5.0%) [raw] 26.40 MB/s"));
    }

    [Fact]
    public void Gate_collapses_a_dense_progress_stream()
    {
        // 100 distinct progress lines 25 ms apart span 2.5 s — the cadence of a fast
        // transfer; a 100 ms gate lets through roughly one line per four, i.e. ~25
        // instead of 100. The panel ends up with a readable trace instead of every frame.
        long now = 0;
        var gate = new ProgressLogGate(() => now);

        var logged = 0;
        for (var i = 1; i <= 100; i++)
        {
            now += 25;
            var line = $"recovery: {i}.0 MB/100.0 MB ({i,3}.0%) [raw] 40.00 MB/s";
            if (gate.ShouldLog(line)) logged++;
        }

        Assert.InRange(logged, 22, 30);
    }

    // ══════════════════════════════════════════════════════════════
    //  capability detection
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Capability_scan_recognises_the_shipped_progress_build()
    {
        var path = Path.Combine(RepoRoot, "artifacts", "platform-tools", "fastboot.exe");
        Assert.True(File.Exists(path), $"expected the source-built fastboot at {path}");

        Assert.True(FastbootCapability.SupportsTransferProgress(path));
    }

    [Fact]
    public void Capability_scan_rejects_a_binary_without_the_format_string()
    {
        // Google's signed release contains no "(%.1f%%)" and no "MB/s" at all, which
        // is what makes the UI hint necessary rather than cosmetic.
        var tmp = Path.Combine(Path.GetTempPath(), $"fb-official-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(tmp, "Sending '%s' (%zu KB)\nOKAY [%7.3fs]\n"u8.ToArray());
        try
        {
            Assert.False(FastbootCapability.SupportsTransferProgress(tmp));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Capability_scan_recognises_the_needle_anywhere_in_the_file()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"fb-needle-{Guid.NewGuid():N}.bin");
        var payload = new byte[9 * 1024 * 1024];
        // Place the needle past an 8 MB offset, so a bounded single read would miss
        // it. The scan reads the whole file for exactly this reason.
        var needle = "(%.1f%%)"u8.ToArray();
        Array.Copy(needle, 0, payload, 8 * 1024 * 1024 + 123, needle.Length);
        File.WriteAllBytes(tmp, payload);
        try
        {
            Assert.True(FastbootCapability.SupportsTransferProgress(tmp));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Capability_scan_reports_false_for_no_path(string? path)
    {
        Assert.False(FastbootCapability.SupportsTransferProgress(path));
    }

    [Fact]
    public void Capability_scan_reports_false_for_a_missing_file()
    {
        Assert.False(FastbootCapability.SupportsTransferProgress(
            Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.exe")));
    }

    [Fact]
    public void Capability_scan_is_cached_per_path()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"fb-cache-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(tmp, "no needle here"u8.ToArray());
        try
        {
            Assert.False(FastbootCapability.SupportsTransferProgress(tmp));
            // Rewrite the file with the needle present. A cached answer must still
            // be false: the binary cannot change under a running app, and re-scanning
            // on every flash would be wasted I/O.
            File.WriteAllBytes(tmp, "(%.1f%%)"u8.ToArray());
            Assert.False(FastbootCapability.SupportsTransferProgress(tmp));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    /// <summary>
    /// Walks up from the test assembly to the repository root, which contains both
    /// <c>tools/</c> and <c>src/</c>.
    /// </summary>
    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tools")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
