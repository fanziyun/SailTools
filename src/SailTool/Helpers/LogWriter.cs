namespace SailTool.Helpers;

/// <summary>
/// Appends lines to a page's log buffer, marshalling onto the UI thread.
/// </summary>
/// <remarks>
/// <para>
/// Both flash pages accumulate a <c>LogText</c> string and append to it from a
/// background thread — fastboot output and patch progress arrive on the stream
/// reader's thread, so the write has to hop to the dispatcher. This is the one
/// place that hop is written down.
/// </para>
/// <para>
/// The two pages differ in exactly one way: the ROM flash log prefixes each line
/// with a <c>[HH:mm:ss]</c> timestamp and the custom-flash log does not. That
/// difference is the <paramref name="timestamped"/> flag, so sharing the helper
/// does not merge the two formats.
/// </para>
/// </remarks>
internal static class LogWriter
{
    /// <summary>
    /// Appends <paramref name="message"/> plus a newline to the buffer reached
    /// through <paramref name="get"/> / <paramref name="set"/>, on the UI thread.
    /// </summary>
    /// <param name="get">Reads the current buffer text.</param>
    /// <param name="set">Writes it back; expected to raise change notification.</param>
    /// <param name="message">The line to append.</param>
    /// <param name="timestamped">Whether to prefix <c>[HH:mm:ss]</c>.</param>
    internal static void Append(
        Func<string> get,
        Action<string> set,
        string message,
        bool timestamped = false)
    {
        App.DispatcherQueue.TryEnqueue(() =>
        {
            var line = timestamped ? $"[{DateTime.Now:HH:mm:ss}] {message}" : message;
            set(get() + line + "\n");
        });
    }
}
