using System.Diagnostics;
using System.Text;

namespace SailTool.Services;

/// <summary>
/// Low-level process management for fastboot.exe and adb.exe.
/// Handles child process lifecycle, process tracking, and CLI execution.
/// All spawned processes are tracked and killed when the service is disposed.
/// </summary>
public sealed class FastbootProcessService(string? fastbootPath = null) : IDisposable
{
    // ── fastboot.exe / adb.exe location ──────────────────────────
    private readonly string _fastbootPath = fastbootPath
        ?? FindFastbootPath()
        ?? throw new FileNotFoundException(
            "fastboot.exe not found. Place platform-tools next to the app " +
            "or pass the path explicitly to the constructor.");

    private readonly string? _adbPath = FindAdbPath(fastbootPath);

    private readonly List<Process> _activeProcesses = [];
    private readonly object _lock = new();

    /// <summary>Whether adb.exe was found and is available.</summary>
    public bool HasAdb => _adbPath is not null;

    /// <summary>Full path to the fastboot.exe executable.</summary>
    public string FastbootPath => _fastbootPath;

    // ══════════════════════════════════════════════════════════════
    //  RAW COMMAND (for reboot, erase, etc.)
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Runs any fastboot command and returns combined stdout+stderr.
    /// </summary>
    public async Task<string> RunFastbootAsync(
        string arguments, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = _fastbootPath,
            Arguments              = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        using var process = TrackProcess(psi);

        process.Start();

        // Read stdout and stderr concurrently to avoid deadlocks
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
    }

    /// <summary>
    /// Runs any adb command and returns (exitCode, output).
    /// Returns (-1, "") if adb is not available.
    /// </summary>
    public async Task<(int exitCode, string output)> RunAdbAsync(
        string arguments, CancellationToken ct = default)
    {
        if (_adbPath is null)
            return (-1, "adb.exe not found");

        var psi = new ProcessStartInfo
        {
            FileName               = _adbPath,
            Arguments              = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        using var process = TrackProcess(psi);

        process.Start();

        // Read stdout and stderr concurrently to avoid deadlocks
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var combined = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;

        return (process.ExitCode, combined);
    }

    // ══════════════════════════════════════════════════════════════
    //  PROCESS TRACKING  (guaranteed cleanup on dispose)
    // ══════════════════════════════════════════════════════════════

    internal Process TrackProcess(ProcessStartInfo psi)
    {
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        lock (_lock) _activeProcesses.Add(proc);

        proc.Exited += (_, _) =>
        {
            lock (_lock) _activeProcesses.Remove(proc);
        };

        return proc;
    }

    /// <summary>Forces all tracked child processes to terminate.</summary>
    public void KillAll()
    {
        lock (_lock)
        {
            foreach (var p in _activeProcesses.ToList())
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
                catch { /* best effort */ }
            }
            _activeProcesses.Clear();
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  DISPOSE
    // ══════════════════════════════════════════════════════════════

    public void Dispose() => KillAll();

    // ══════════════════════════════════════════════════════════════
    //  STREAM READER  (supports \r-only progress lines)
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Reads a process stream and splits it on both '\r' and '\n'.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Splitting on '\r' as well as '\n' is what makes progress reporting work at
    /// all. A progress-capable fastboot overwrites the same line with a carriage
    /// return and emits no newline until the phase ends, so
    /// <c>BeginErrorReadLine()</c> — which waits for '\n' — delivers nothing until
    /// the transfer is already over, and the bar then jumps from empty to complete.
    /// </para>
    /// <para>
    /// The decoding goes through <see cref="StreamReader"/> rather than
    /// <c>Encoding.UTF8.GetString</c> on each raw chunk. A multi-byte UTF-8
    /// sequence can straddle a buffer boundary, and decoding the bytes directly
    /// would turn it into replacement characters — a bootloader INFO string or a
    /// non-ASCII partition name arriving in the wrong place would render as mojibake
    /// in the log. <c>StreamReader</c> carries the incomplete sequence over to the
    /// next read.
    /// </para>
    /// <para>
    /// Characters are accumulated in a <see cref="StringBuilder"/> and the callback
    /// fires once per line, not once per character: a multi-gigabyte transfer pushes
    /// gigabytes through this reader for a few thousand actual lines, and a
    /// per-character callback would mean millions of regex matches on fragments that
    /// are not lines yet.
    /// </para>
    /// </remarks>
    internal static async Task ReadStreamLinesAsync(
        Stream stream,
        Action<string>? onOutput,
        Action<string>? onParsed,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        // leaveOpen: the caller owns the stream and disposes it.
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var buffer = new char[4096];

        try
        {
            int charsRead;
            while ((charsRead = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
            {
                for (var i = 0; i < charsRead; i++)
                {
                    var c = buffer[i];
                    if (c is '\r' or '\n')
                    {
                        if (sb.Length > 0)
                        {
                            var line = sb.ToString();
                            sb.Clear();
                            onOutput?.Invoke(line);
                            onParsed?.Invoke(line);
                        }
                        // else: the second half of a \r\n pair, or a bare \r used to
                        // erase a line — nothing to report.
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
            }

            // Flush any trailing content with no final \r or \n.
            if (sb.Length > 0)
            {
                var line = sb.ToString();
                onOutput?.Invoke(line);
                onParsed?.Invoke(line);
            }
        }
        catch (OperationCanceledException) { /* expected on cancel */ }
        catch (IOException)                { /* pipe broken on process exit */ }
    }

    // ── Auto-detect fastboot.exe ─────────────────────────────────
    private static string? FindFastbootPath()
        => FindToolInPlatformTools("fastboot.exe");

    // ── Auto-detect adb.exe (same directory as fastboot) ─────────
    private static string? FindAdbPath(string? fbPath = null)
    {
        if (fbPath is not null)
        {
            var besideFb = Path.Combine(Path.GetDirectoryName(fbPath)!, "adb.exe");
            if (File.Exists(besideFb)) return besideFb;
        }
        return FindToolInPlatformTools("adb.exe");
    }

    internal static string? FindToolInPlatformTools(string toolName)
    {
        // 1. Next to the running executable (green-mode / portable)
        var exeDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var beside = Path.Combine(exeDir, "platform-tools", toolName);
        if (File.Exists(beside)) return beside;

        // 2. Common Desktop location (dev convenience)
        var desktop = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "platform-tools", toolName);
        if (File.Exists(desktop)) return desktop;

        // 3. System PATH
        return null;
    }
}
