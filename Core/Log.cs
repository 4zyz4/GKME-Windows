using System;
using System.Collections.Generic;
using System.Text;

namespace GKME.Core;

/// <summary>
/// Process-wide diagnostic log. Everything stays in memory for the current
/// run and is shown by the Help → Show log window; nothing is written to
/// disk, so a read-only install directory (AppImage, Program Files) is never
/// a problem. Every line is also mirrored to stderr so a terminal launch
/// shows it immediately.
/// </summary>
public static class Log
{
    /// <summary>Upper bound on retained entries. Older lines are dropped so a
    /// long-running session cannot grow without limit.</summary>
    public const int RetentionLimit = 5000;

    private static readonly object Gate = new();
    private static readonly List<LogEntry> Buffer = new();
    private static long _sequence;

    /// <summary>Raised after an entry was appended. Fires on the caller's
    /// thread; UI subscribers must marshal to the UI thread themselves.</summary>
    public static event Action<LogEntry>? EntryAdded;

    /// <summary>Raised after the buffer was emptied, so a viewer can reset.</summary>
    public static event Action? Cleared;

    public static int Count
    {
        get { lock (Gate) return Buffer.Count; }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? exception = null)
    {
        if (exception != null)
            message = message + Environment.NewLine + exception;
        Write("ERROR", message);
    }

    /// <summary>One-time startup banner: enough context to reproduce a bug from
    /// the log alone (build, OS/architecture, elevation and runtime paths).</summary>
    public static void StartupBanner(string appName, string version)
    {
        Info($"=== {appName} {version} starting ===");
        Info($"OS      : {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        Info($"Arch    : OS={System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}, " +
             $"process={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        Info($"Elevated: {PlatformInfo.IsElevated}");
        Info($"BaseDir : {AppContext.BaseDirectory}");
    }

    /// <summary>Returns the entries newer than <paramref name="sequence"/>.
    /// <paramref name="reset"/> is true when older entries were dropped
    /// (size cap or <see cref="Clear"/>), meaning the caller's accumulated
    /// text no longer matches the buffer and must be rebuilt from scratch.</summary>
    public static IReadOnlyList<LogEntry> GetEntriesAfter(long sequence, out bool reset)
    {
        lock (Gate)
        {
            reset = Buffer.Count > 0 && Buffer[0].Sequence > sequence + 1;
            if (reset) sequence = 0;

            var result = new List<LogEntry>();
            foreach (var entry in Buffer)
                if (entry.Sequence > sequence) result.Add(entry);
            return result;
        }
    }

    public static void Clear()
    {
        lock (Gate) Buffer.Clear();
        try { Cleared?.Invoke(); } catch { }
    }

    /// <summary>Full log as plain text, used by the window's copy button.</summary>
    public static string FormatAll()
    {
        lock (Gate)
        {
            var sb = new StringBuilder();
            foreach (var entry in Buffer)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(entry.ToString());
            }
            return sb.ToString();
        }
    }

    private static void Write(string level, string message)
    {
        var entry = new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = level,
            Message = message,
        };
        var line = entry.ToString();

        try { Console.Error.WriteLine(line); } catch { }

        lock (Gate)
        {
            entry.Sequence = ++_sequence;
            Buffer.Add(entry);
            if (Buffer.Count > RetentionLimit)
                Buffer.RemoveRange(0, Buffer.Count - RetentionLimit);
        }

        // Raised outside the lock: a subscriber may touch the UI, which must
        // never be able to deadlock a diagnostic write.
        try { EntryAdded?.Invoke(entry); } catch { }
    }
}

/// <summary>A single log line, timestamped when it was written.</summary>
public sealed class LogEntry
{
    /// <summary>Monotonic write order, used by viewers to request only the
    /// lines they have not seen yet.</summary>
    public long Sequence { get; internal set; }
    public DateTime Timestamp { get; init; }
    public string Level { get; init; } = "INFO";
    public string Message { get; init; } = string.Empty;

    public override string ToString() => $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level}] {Message}";
}
