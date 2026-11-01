using System.Text;
using SailTool.Services;
using Xunit;

namespace SailTool.Tests.Services;

/// <summary>
/// Covers <see cref="FastbootProcessService.ReadStreamLinesAsync"/>: the stream
/// reader that turns a child process's output into lines.
/// </summary>
/// <remarks>
/// This is the piece that decides whether progress reporting works at all. A
/// progress-capable fastboot overwrites one line with carriage returns and emits no
/// newline until the phase ends, so a reader that waits for '\n' delivers nothing
/// until the transfer is already over.
/// </remarks>
public class StreamLineReaderTests
{
    private static async Task<List<string>> ReadAsync(
        byte[] bytes, int? chunkSize = null, CancellationToken ct = default)
    {
        var lines = new List<string>();
        Stream stream = chunkSize is { } size
            ? new ChunkedStream(bytes, size)
            : new MemoryStream(bytes);
        await using (stream)
        {
            await FastbootProcessService.ReadStreamLinesAsync(
                stream, line => lines.Add(line), onParsed: null, ct);
        }
        return lines;
    }

    [Fact]
    public async Task Splits_on_newline()
    {
        var lines = await ReadAsync(Encoding.UTF8.GetBytes("Sending 'boot' (98304 KB)\nOKAY [  2.626s]\n"));

        Assert.Equal(
            ["Sending 'boot' (98304 KB)", "OKAY [  2.626s]"],
            lines);
    }

    [Fact]
    public async Task Splits_on_bare_carriage_return()
    {
        // The case that matters: fastboot rewrites the progress line in place using
        // \r alone, with no \n until the phase completes. A reader that only splits
        // on \n would deliver nothing until the transfer was already over.
        var bytes = Encoding.UTF8.GetBytes(
            "boot:   5.0 MB/100.0 MB (5.0%) [raw] 26.40 MB/s\r" +
            "boot:  65.0 MB/100.0 MB (65.0%) [raw] 40.22 MB/s\r" +
            "boot: 100.0 MB/100.0 MB (100.0%) [raw] 40.10 MB/s\r");

        var lines = await ReadAsync(bytes);

        Assert.Equal(3, lines.Count);
        Assert.Contains("26.40 MB/s", lines[0]);
        Assert.Contains("40.22 MB/s", lines[1]);
        Assert.Contains("100.0%", lines[2]);
    }

    [Fact]
    public async Task A_crlf_pair_yields_one_line_not_two()
    {
        var lines = await ReadAsync(Encoding.UTF8.GetBytes("first\r\nsecond\r\n"));

        Assert.Equal(["first", "second"], lines);
    }

    [Fact]
    public async Task Empty_segments_are_skipped()
    {
        // A trailing \r\n plus blank lines in between: neither should surface as an
        // empty entry, or the log panel would show phantom blank rows.
        var lines = await ReadAsync(Encoding.UTF8.GetBytes("a\n\n\nb\r\n"));

        Assert.Equal(["a", "b"], lines);
    }

    [Fact]
    public async Task Trailing_content_without_a_terminator_is_still_delivered()
    {
        var lines = await ReadAsync(Encoding.UTF8.GetBytes("Sending 'boot' (98304 KB)"));

        Assert.Equal(["Sending 'boot' (98304 KB)"], lines);
    }

    [Fact]
    public async Task An_empty_stream_yields_no_lines()
    {
        Assert.Empty(await ReadAsync([]));
    }

    [Fact]
    public async Task A_multibyte_character_split_across_reads_survives()
    {
        // The reader must decode through a StreamReader rather than per raw chunk.
        // Reading one byte at a time guarantees every multi-byte character is split
        // across reads, which is what the previous byte-chunk implementation got
        // wrong: it decoded each buffer independently, turning one three-byte
        // character into three replacement characters.
        var payload = "(bootloader) 分区大小: 100 MB\n";
        var lines = await ReadAsync(Encoding.UTF8.GetBytes(payload), chunkSize: 1);

        var line = Assert.Single(lines);
        Assert.Equal("(bootloader) 分区大小: 100 MB", line);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    public async Task Multibyte_text_survives_every_chunk_alignment(int chunkSize)
    {
        // Not just a pathological one-byte split: any alignment can land inside a
        // character, so each is checked rather than one convenient case.
        var payload = "vendor_boot:RAMDISK 分区 100.0 MB 已传输\n";

        var lines = await ReadAsync(Encoding.UTF8.GetBytes(payload), chunkSize);

        var line = Assert.Single(lines);
        Assert.Equal(payload.TrimEnd('\n'), line);
    }

    [Fact]
    public async Task Progress_lines_split_correctly_at_a_one_byte_chunk_size()
    {
        // The realistic shape: \r-separated progress frames arriving in tiny pieces.
        var bytes = Encoding.UTF8.GetBytes(
            "boot:   5.0 MB/100.0 MB (5.0%) [raw] 26.40 MB/s\r" +
            "boot: 100.0 MB/100.0 MB (100.0%) [raw] 40.10 MB/s\r");

        var lines = await ReadAsync(bytes, chunkSize: 1);

        Assert.Equal(2, lines.Count);
        Assert.Contains("5.0%", lines[0]);
        Assert.Contains("100.0%", lines[1]);
    }

    [Fact]
    public async Task Both_callbacks_receive_every_line()
    {
        // onOutput feeds the log, onParsed feeds the tracker. The tracker must see
        // every line — gating it would corrupt its cursors — while the log is
        // sampled by a separate gate before it reaches the ViewModel.
        var output = new List<string>();
        var parsed = new List<string>();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("a\nb\r"));

        await FastbootProcessService.ReadStreamLinesAsync(
            stream, output.Add, parsed.Add, CancellationToken.None);

        Assert.Equal(["a", "b"], output);
        Assert.Equal(["a", "b"], parsed);
    }

    [Fact]
    public async Task Null_callbacks_are_tolerated()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("a\nb\n"));

        await FastbootProcessService.ReadStreamLinesAsync(
            stream, onOutput: null, onParsed: null, CancellationToken.None);
    }

    [Fact]
    public async Task Cancellation_stops_the_read_without_throwing()
    {
        var cts = new CancellationTokenSource();
        var received = new List<string>();

        // A stream that cancels on its first read, simulating the user pressing stop
        // while fastboot is still running.
        await using var endless = new CancellingStream(cts);

        await FastbootProcessService.ReadStreamLinesAsync(
            endless, received.Add, onParsed: null, cts.Token);

        // Cancelled is an expected path, not an error: the method swallows
        // OperationCanceledException so the caller's finally block runs normally.
        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task A_broken_pipe_stops_the_read_without_throwing()
    {
        // fastboot exiting while we are mid-read surfaces as IOException. The reader
        // must not let that escape, or a normal completion would look like a crash.
        await using var stream = new ThrowingStream();

        await FastbootProcessService.ReadStreamLinesAsync(
            stream, onOutput: null, onParsed: null, CancellationToken.None);
    }

    /// <summary>Hands out at most <paramref name="chunkSize"/> bytes per read.</summary>
    private sealed class ChunkedStream(byte[] data, int chunkSize) : Stream
    {
        private int _pos;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= data.Length) return 0;
            var n = Math.Min(Math.Min(count, chunkSize), data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
    }

    /// <summary>Cancels its token on the first read, then returns end-of-stream.</summary>
    private sealed class CancellingStream(CancellationTokenSource cts) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            cts.Cancel();
            return 0;
        }
    }

    /// <summary>Fails on the first read, like a pipe closed by process exit.</summary>
    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
            => throw new IOException("pipe broken");
    }
}
