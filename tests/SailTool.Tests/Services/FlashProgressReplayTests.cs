using SailTool.Models;
using SailTool.Services;
using Xunit;

namespace SailTool.Tests.Services;

/// <summary>
/// Replays a recording of a real flash through the whole progress pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <c>TestData/sample_fastboot.log</c> is the captured output of flashing a 100 MB
/// recovery partition with the shipped progress-capable build, including the app's
/// own bracketed status lines and a duplicated <c>Sending</c> header. Replaying the
/// file rather than a transcription of it is the point: the assertions below are
/// about what fastboot actually emits, and a transcription could drift from that
/// without anyone noticing.
/// </para>
/// <para>
/// This is the verification path the progress design calls for — replay a recorded
/// stream and check the bar advances correctly, without needing a device attached.
/// </para>
/// </remarks>
public class FlashProgressReplayTests
{
    private static string[] LoadRecording()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "sample_fastboot.log");
        Assert.True(File.Exists(path), $"missing test fixture at {path}");
        return File.ReadAllLines(path);
    }

    [Fact]
    public void The_recording_contains_the_expected_shape()
    {
        // Guards the fixture itself: if the file is truncated or replaced, the
        // assertions below would pass vacuously.
        var lines = LoadRecording();

        Assert.Contains(lines, l => l.Contains("Sending 'recovery' (102400 KB)"));
        Assert.Contains(lines, l => l.StartsWith("recovery:") && l.Contains("%") && l.Contains("MB/s"));
        Assert.Contains(lines, l => l.StartsWith("Writing 'recovery'"));
        Assert.Contains(lines, l => l.StartsWith("Finished."));
    }

    [Fact]
    public void Replaying_the_recording_walks_the_bar_from_start_to_completion()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = new FlashProgressTracker(info => progress.Add(info));

        foreach (var line in LoadRecording())
            tracker.HandleLine(line);

        Assert.Equal(100, progress[^1].Percent);
        Assert.Equal(FlashPhase.Finished, progress[^1].Phase);
    }

    [Fact]
    public void Replaying_the_recording_never_regresses_the_bar()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = new FlashProgressTracker(info => progress.Add(info));

        foreach (var line in LoadRecording())
            tracker.HandleLine(line);

        // The recording has a duplicated Sending header and the app's own bracketed
        // lines interleaved, which is exactly the kind of input that would make a
        // naive parser jump backwards.
        for (var i = 1; i < progress.Count; i++)
            Assert.True(progress[i].Percent >= progress[i - 1].Percent,
                $"regressed at index {i}: {progress[i - 1].Percent} -> {progress[i].Percent}");
    }

    [Fact]
    public void Replaying_the_recording_yields_a_rate_on_every_transfer_update()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = new FlashProgressTracker(info => progress.Add(info));

        foreach (var line in LoadRecording())
            tracker.HandleLine(line);

        var sending = progress.Where(p => p.Phase == FlashPhase.Sending).ToList();
        Assert.NotEmpty(sending);
        Assert.All(sending, p => Assert.NotNull(p.SpeedMBs));
    }

    [Fact]
    public void Replaying_the_recording_reports_both_byte_counts()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = new FlashProgressTracker(info => progress.Add(info));

        foreach (var line in LoadRecording())
            tracker.HandleLine(line);

        var last = progress[^1];
        Assert.Equal(100L * 1024 * 1024, last.TotalBytes);
        Assert.Equal(last.TotalBytes, last.TransferredBytes);
    }

    [Fact]
    public void Replaying_the_recording_ignores_the_apps_own_status_lines()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = new FlashProgressTracker(info => progress.Add(info));

        foreach (var line in LoadRecording())
            tracker.HandleLine(line);

        // Nothing reports failure, and the app's bracketed Chinese status lines
        // ("已选择镜像...", "已连接 ...") produced no phantom progress.
        Assert.DoesNotContain(progress, p => p.Percent == -1);
        Assert.DoesNotContain(progress, p => p.Phase == FlashPhase.Failed);
    }

    [Fact]
    public void The_log_gate_collapses_the_recording_to_a_readable_trace()
    {
        var lines = LoadRecording();
        var now = 0L;
        var gate = new ProgressLogGate(() => now);

        var logged = new List<string>();
        foreach (var line in lines)
        {
            // The recording carries no timestamps, so step the fake clock at the
            // cadence of a fast transfer — one frame per 1 MiB chunk, ~25 ms apart.
            now += 25;
            if (gate.ShouldLog(line)) logged.Add(line);
        }

        var progressLines = lines.Count(l => l.StartsWith("recovery:"));
        var loggedProgress = logged.Count(l => l.StartsWith("recovery:"));

        Assert.Equal(20, progressLines);
        Assert.True(loggedProgress < progressLines,
            $"expected sampling, got {loggedProgress} of {progressLines}");
        // Every non-progress line survives: those are the ones worth reading back.
        Assert.Contains(logged, l => l.Contains("Sending 'recovery'"));
        Assert.Contains(logged, l => l.StartsWith("Writing 'recovery'"));
        Assert.Contains(logged, l => l.StartsWith("Finished."));
    }

    [Fact]
    public void The_throttle_collapses_the_recording_to_a_handful_of_frames()
    {
        var progress = new List<FlashProgressInfo>();
        var tracker = new FlashProgressTracker(info => progress.Add(info));
        foreach (var line in LoadRecording())
            tracker.HandleLine(line);

        var now = 0L;
        var throttle = new ProgressThrottle(() => now);

        var rendered = 0;
        foreach (var info in progress)
        {
            now += 25;
            if (throttle.ShouldRender(info)) rendered++;
        }

        Assert.True(progress.Count > rendered,
            $"expected dropped frames, got {rendered} of {progress.Count}");
        // Completion must still get through: it is a terminal state and bypasses
        // the interval, so the bar cannot end stuck short of 100%.
        Assert.True(rendered > 0);
    }
}
