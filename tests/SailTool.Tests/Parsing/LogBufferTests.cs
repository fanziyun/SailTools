using SailTool.Services.Parsing;
using Xunit;

namespace SailTool.Tests.Parsing;

/// <summary>
/// Covers the log buffer surgery behind the one-line progress display: appending a
/// line, overwriting the last one, and recognising when an overwrite is allowed.
/// </summary>
/// <remarks>
/// <para>
/// These are the rules the panels rely on to keep a transfer on a single line
/// instead of appending a frame several times a second. The strings below are
/// verbatim stderr captured from the shipped fastboot (36.0.0 + MeowArch bar) while
/// flashing a 19 MiB image over a TCP mock device.
/// </para>
/// </remarks>
public class LogBufferTests
{
    /// <summary>Bar frames as they arrive after the reader splits on \r and \n.</summary>
    private static readonly string[] BarFrames =
    [
        "Sending 'boot' (19531 KB)                           [#>----------------------]   5%  1.0 MiB/19.1 MiB",
        "Sending 'boot' (19531 KB)                           [#####>------------------]  21%  4.0 MiB/19.1 MiB 22.6 MiB/s ETA 0:01",
        "Sending 'boot' (19531 KB)                           [###############>--------]  63%  12.0 MiB/19.1 MiB 16.5 MiB/s ETA 0:00",
        "Sending 'boot' (19531 KB)                           [########################] 100%  19.1 MiB/19.1 MiB 14.0 MiB/s",
    ];

    /// <summary>
    /// A frame from the current build (upstream 952ca57), captured the same way.
    /// The prolog is trimmed to one space and the frame is newline-terminated; the
    /// overwrite rule must not depend on the old 50-column padding.
    /// </summary>
    private const string CurrentBarFrame =
        "Sending 'boot' (19456 KB) [############>---------------]   42%  8.0 MiB/19.0 MiB 17.5 MiB/s ETA 0:01";

    // ══════════════════════════════════════════════════════════════
    //  append
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Append_adds_the_line_and_a_newline()
    {
        Assert.Equal("a\n", LogBuffer.Append("", "a"));
        Assert.Equal("a\nb\n", LogBuffer.Append("a\n", "b"));
    }

    // ══════════════════════════════════════════════════════════════
    //  replace
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void ReplaceLastLine_swaps_only_the_final_line()
    {
        var text = "first\nsecond\n";

        Assert.Equal("first\nthird\n", LogBuffer.ReplaceLastLine(text, "third"));
    }

    [Fact]
    public void ReplaceLastLine_keeps_the_trailing_newline()
    {
        var text = LogBuffer.ReplaceLastLine("only\n", "replacement");

        Assert.EndsWith("\n", text);
        Assert.Equal("replacement\n", text);
    }

    [Fact]
    public void ReplaceLastLine_on_an_empty_buffer_appends()
    {
        // Nothing to replace: the first frame of a flash is an append.
        Assert.Equal("frame\n", LogBuffer.ReplaceLastLine("", "frame"));
    }

    [Fact]
    public void ReplaceLastLine_treats_a_lone_newline_as_empty()
    {
        // The buffer ends with "\n" after every append; that must not be read as an
        // empty final line, or each replacement would leave a blank line behind.
        Assert.Equal("frame\n", LogBuffer.ReplaceLastLine("\n", "frame"));
    }

    [Fact]
    public void ReplaceLastLine_normalises_a_missing_trailing_newline()
    {
        // Both helpers leave the buffer ending in a newline, which is what the
        // append path already guarantees. A buffer that lacks one is only reachable
        // if a caller built it by hand; normalising it here keeps the invariant.
        Assert.Equal("a\nc\n", LogBuffer.ReplaceLastLine("a\nb", "c"));
    }

    // ══════════════════════════════════════════════════════════════
    //  recognition
    // ══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void LastLineIsProgress_recognises_every_bar_frame(int index)
    {
        Assert.True(LogBuffer.LastLineIsProgress(BarFrames[index] + "\n"));
    }

    [Fact]
    public void LastLineIsProgress_recognises_a_current_build_frame()
    {
        Assert.True(LogBuffer.LastLineIsProgress(CurrentBarFrame + "\n"));
    }

    [Fact]
    public void A_current_build_frame_overwrites_the_previous_one_in_place()
    {
        var text = LogBuffer.ReplaceLastLine("", BarFrames[0]);
        text = LogBuffer.ReplaceLastLine(text, CurrentBarFrame);

        Assert.Equal(CurrentBarFrame + "\n", text);
        Assert.Single(text.TrimEnd('\n').Split('\n'));
    }

    [Fact]
    public void LastLineIsProgress_ignores_an_earlier_progress_line()
    {
        // Only the tail matters: a frame earlier in the buffer is history.
        var text = BarFrames[0] + "\nSending 'boot' (19531 KB)\n";

        Assert.False(LogBuffer.LastLineIsProgress(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("Sending 'boot' (19531 KB)\n")]
    [InlineData("Warning: skip copying boot image avb footer\n")]
    [InlineData("Sending 'boot' (19531 KB)                          OKAY [  1.272s]\n")]
    [InlineData("Writing 'boot'                                     OKAY [  0.200s]\n")]
    [InlineData("Finished. Total time: 1.475s\n")]
    public void LastLineIsProgress_rejects_non_frames(string text)
    {
        Assert.False(LogBuffer.LastLineIsProgress(text));
    }

    [Fact]
    public void LastLineIsProgress_accepts_a_timestamped_frame()
    {
        // The ROM page prefixes [HH:mm:ss]; the pattern is not anchored, so the
        // prefix does not hide the bar.
        var text = "[12:34:56] " + BarFrames[2] + "\n";

        Assert.True(LogBuffer.LastLineIsProgress(text));
    }

    // ══════════════════════════════════════════════════════════════
    //  end to end
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void A_bar_transfer_occupies_exactly_one_line()
    {
        // The shape both panels produce: warnings and the Sending prolog are kept,
        // the frames collapse into one line, and the terminal OKAY lands below it.
        var text = LogBuffer.Append("", "Warning: skip copying boot image avb footer");
        text = LogBuffer.Append(text, "Sending 'boot' (19531 KB)");
        foreach (var frame in BarFrames)
        {
            text = LogBuffer.LastLineIsProgress(text)
                ? LogBuffer.ReplaceLastLine(text, frame)
                : LogBuffer.Append(text, frame);
        }
        text = LogBuffer.Append(text, "Sending 'boot' (19531 KB)                          OKAY [  1.272s]");

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        Assert.Equal("Warning: skip copying boot image avb footer", lines[0]);
        Assert.Equal("Sending 'boot' (19531 KB)", lines[1]);
        Assert.Equal(BarFrames[^1], lines[2]);
        Assert.StartsWith("Sending 'boot' (19531 KB)", lines[3]);
    }
}
