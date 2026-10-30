namespace SailTool.Helpers;

/// <summary>
/// Formats byte counts the way the flash page has always displayed them.
/// </summary>
/// <remarks>
/// The cutoff is <em>greater than</em> 1 MiB, so exactly 1048576 bytes renders as
/// "1024.00 KB", not "1.00 MB". Values below 1 KiB still render in KB with two
/// decimals ("0.50 KB"). Both rules are load-bearing: five call sites used to
/// carry their own copy of this expression and all five agreed, so the pinned
/// behaviour is what users have been seeing.
/// </remarks>
public static class FileUtils
{
    /// <summary>Formats a byte count, e.g. <c>"15.23 MB"</c> or <c>"96.00 KB"</c>.</summary>
    public static string FormatFileSize(long bytes) =>
        bytes > 1024 * 1024
            ? $"{bytes / (1024.0 * 1024.0):F2} MB"
            : $"{bytes / 1024.0:F2} KB";

    /// <summary>
    /// Formats the size of the file at <paramref name="path"/>, or returns an
    /// empty string when the path is missing or the file does not exist — callers
    /// use that to fall back to showing the bare path.
    /// </summary>
    public static string FormatFileSize(string path)
    {
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return string.Empty;
        return FormatFileSize(new System.IO.FileInfo(path).Length);
    }
}
