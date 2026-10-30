namespace FastTool.Helpers;

public static class FileUtils
{
    public static string FormatFileSize(long bytes) =>
        bytes > 1024 * 1024
            ? $"{bytes / (1024.0 * 1024.0):F2} MB"
            : $"{bytes / 1024.0:F2} KB";

    public static string FormatFileSize(string path)
    {
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return string.Empty;
        return FormatFileSize(new System.IO.FileInfo(path).Length);
    }
}
