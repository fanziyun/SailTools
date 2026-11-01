namespace SailTool.Services;

/// <summary>
/// Detects whether the fastboot.exe in use can emit transfer progress lines.
/// </summary>
/// <remarks>
/// <para>
/// Progress output is compiled into the binary, not enabled by a flag, but the
/// shipped build gates it on stderr being a terminal — which it is not when the app
/// redirects the pipe. The app therefore also sets <c>FORCE_PROGRESS=1</c> on the
/// child (see <see cref="FastbootService.FlashImageAsync"/>); this type answers the
/// separate question of whether the binary *can* draw a bar at all.
/// </para>
/// <para>
/// Two binaries are recognised. The shipped one is AOSP fastboot 36.0.0 carrying
/// MeowArch's bar, which formats its percentage with <c>"%3d%%"</c>. The build it
/// replaced was a 35.0.2-eng build emitting <c>"%s: %s/%s (%.1f%%) [%s] %.2f MB/s"</c>.
/// Google's signed releases contain neither format and report no percentages or
/// rates at all, so a user who swaps one in gets the fallback path and the UI says
/// so instead of looking broken.
/// </para>
/// <para>
/// Detection is a byte scan: read the file and look for either needle. fastboot.exe
/// is ~2.5 MB, so a single read is fine. The result is cached per path — the binary
/// cannot change while the app runs, and re-scanning on every flash would be wasted
/// I/O.
/// </para>
/// </remarks>
internal static class FastbootCapability
{
    /// <summary>
    /// ASCII needles that only a progress-capable fastboot build contains, taken
    /// verbatim from the format strings in the binaries. Any one of them is enough.
    /// </summary>
    private static readonly string[] ProgressFormatNeedles =
    [
        "%3d%%",      // MeowArch bar: snprintf(pct, …, "%3d%%", …)
        "(%.1f%%)",   // older 35.0.2-eng transfer line
    ];

    private static readonly Dictionary<string, bool> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _gate = new();

    /// <summary>
    /// Whether the fastboot at <paramref name="fastbootPath"/> can emit transfer
    /// progress lines. Returns false when the file cannot be read — a missing or
    /// locked binary is treated as no-progress rather than an error here, because
    /// the caller already handles the not-found case elsewhere.
    /// </summary>
    internal static bool SupportsTransferProgress(string? fastbootPath)
    {
        if (string.IsNullOrWhiteSpace(fastbootPath)) return false;

        lock (_gate)
        {
            if (_cache.TryGetValue(fastbootPath, out var cached)) return cached;
        }

        bool result;
        try
        {
            result = ScanForProgressFormat(fastbootPath);
        }
        catch (Exception)
        {
            // File missing, locked, or unreadable: assume no progress support.
            result = false;
        }

        lock (_gate)
        {
            _cache[fastbootPath] = result;
        }
        return result;
    }

    /// <summary>
    /// Reads the binary and searches for any of the progress format needles.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A single read of the whole file: fastboot.exe is ~2 MB, so this is cheap and
    /// cannot miss a needle that straddles a buffer boundary. The reference
    /// implementation caps its buffer at 8 MB for the same reason.
    /// </para>
    /// <para>
    /// The needle is ASCII, so the raw bytes are scanned directly rather than
    /// decoding the file. Note that fastboot.exe is 32-bit while this assembly may
    /// be x64 — <c>File.ReadAllBytes</c> does not care, it is just reading a file.
    /// </para>
    /// </remarks>
    private static bool ScanForProgressFormat(string path)
    {
        if (!File.Exists(path)) return false;

        var info = new FileInfo(path);
        if (info.Length == 0 || info.Length > int.MaxValue) return false;

        // FileShare.ReadWrite so this works even while something else holds the
        // binary open — a concurrent scan, or antivirus.
        using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        var buffer = new byte[(int)info.Length];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = fs.Read(buffer, total, buffer.Length - total);
            if (read == 0) break;
            total += read;
        }
        if (total == 0) return false;

        var bytes = buffer.AsSpan(0, total);
        foreach (var needle in ProgressFormatNeedles)
        {
            if (ContainsAscii(bytes, needle)) return true;
        }
        return false;
    }

    /// <summary>
    /// Searches a byte span for an ASCII needle without allocating a decoded copy
    /// of the whole file.
    /// </summary>
    private static bool ContainsAscii(ReadOnlySpan<byte> haystack, string needle)
    {
        if (needle.Length == 0) return true;
        if (haystack.Length < needle.Length) return false;

        Span<byte> needleBytes = stackalloc byte[needle.Length];
        for (int k = 0; k < needle.Length; k++) needleBytes[k] = (byte)needle[k];

        return haystack.IndexOf(needleBytes) >= 0;
    }
}
