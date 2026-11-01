namespace SailTool.Services.Parsing;

/// <summary>
/// String surgery on a log panel's buffer: append a line, or overwrite the last one.
/// </summary>
/// <remarks>
/// <para>
/// The panels bind a single <c>LogText</c> string, so "rendering a progress frame"
/// is nothing more than replacing the tail of that string. Keeping the operation
/// here — rather than in <c>LogWriter</c>, which has to hop onto the dispatcher —
/// is what makes it reachable from the test project, exactly like the rest of this
/// folder.
/// </para>
/// <para>
/// A progress frame is recognised from the buffer itself rather than from a flag the
/// caller keeps. Two stream readers (stdout and stderr) feed the same buffer, and a
/// flag maintained on their threads would race with the dispatcher hop; asking the
/// text what its last line is cannot.
/// </para>
/// </remarks>
internal static class LogBuffer
{
    /// <summary>Appends <paramref name="line"/> as a new line.</summary>
    internal static string Append(string text, string line) => text + line + "\n";

    /// <summary>
    /// Replaces the final line of <paramref name="text"/> with
    /// <paramref name="line"/>, keeping the trailing newline.
    /// </summary>
    /// <remarks>
    /// An empty buffer has no line to replace, so this degenerates to
    /// <see cref="Append"/>. A trailing newline is not treated as an empty final
    /// line — the caller's buffer always ends with one after an append, and
    /// replacing "the empty line after the last newline" would produce a blank line
    /// between the progress frame and whatever comes next.
    /// </remarks>
    internal static string ReplaceLastLine(string text, string line)
    {
        if (string.IsNullOrEmpty(text)) return line + "\n";

        var end = text.Length;
        if (text[end - 1] == '\n') end--;
        if (end == 0) return line + "\n";

        var start = text.LastIndexOf('\n', end - 1) + 1;
        return text[..start] + line + "\n";
    }

    /// <summary>
    /// Whether the buffer's final line is a transfer-progress frame, i.e. whether
    /// the next frame may overwrite it instead of being appended.
    /// </summary>
    /// <remarks>
    /// The first frame of a transfer follows either the <c>Sending 'name' (N KB)</c>
    /// prolog or an unrelated warning, and neither is a frame — replacing them would
    /// delete information the user needs. Only once a frame is the last line does the
    /// overwrite path apply.
    /// </remarks>
    internal static bool LastLineIsProgress(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        var end = text.Length;
        if (text[end - 1] == '\n') end--;
        if (end == 0) return false;

        var start = text.LastIndexOf('\n', end - 1) + 1;
        return FastbootOutputParser.IsProgressLine(text[start..end]);
    }
}
