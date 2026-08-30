using System.Globalization;
using System.Text;

namespace GKME.Core;

public static class ExceptionLogger
{
    private static readonly List<ExceptionRecord> _logs = new();
    private const int MaxRecords = 100;

    public static event Action? OnNewException;

    public static IReadOnlyCollection<ExceptionRecord> Logs => _logs;

    public static void Record(Exception ex, string? source = null)
    {
        var record = new ExceptionRecord
        {
            Timestamp = DateTime.UtcNow,
            ThreadName = Thread.CurrentThread.Name ?? Thread.CurrentThread.ManagedThreadId.ToString(CultureInfo.InvariantCulture),
            ThreadId = Thread.CurrentThread.ManagedThreadId,
            Type = ex.GetType().FullName ?? ex.GetType().Name,
            Message = ex.Message,
            Source = ex.Source,
            StackTrace = ex.ToString(),
            SourceLocation = source ?? "Unknown"
        };

        _logs.Add(record);
        while (_logs.Count > MaxRecords)
            _logs.RemoveAt(0);

        OnNewException?.Invoke();
    }

    public static string FormatForClipboard()
    {
        var sb = new StringBuilder();
        sb.AppendLine("GKME - Crash Report");
        sb.AppendLine($"Collected at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss UTC}");
        sb.AppendLine($"Total {_logs.Count} exception(s)");
        sb.AppendLine(new string('─', 80));
        sb.AppendLine();

        for (int i = _logs.Count - 1; i >= 0; i--)
        {
            var r = _logs[i];
            var num = _logs.Count - i;
            sb.AppendLine($"##[{num:00}] {r.Type}");
            sb.AppendLine($"  Time   : {r.Timestamp:yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"  Thread : {r.ThreadName} (ID={r.ThreadId})");
            sb.AppendLine($"  Source : {r.SourceLocation}");
            sb.AppendLine($"  Message: {r.Message}");
            if (!string.IsNullOrEmpty(r.Source))
                sb.AppendLine($"  Source .: {r.Source}");
            sb.AppendLine();

            sb.AppendLine("  Stack Trace:");
            var stackLines = (r.StackTrace ?? "").Split('\n');
            foreach (var line in stackLines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;

                if (trimmed.StartsWith("   at "))
                {
                    sb.AppendLine($"    ▼ {trimmed}");
                }
                else if (trimmed.StartsWith("---"))
                {
                    sb.AppendLine($"    ... {trimmed}");
                }
                else
                {
                    sb.AppendLine($"      {trimmed}");
                }
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static void Clear()
    {
        _logs.Clear();
    }

    public static int Count => _logs.Count;
}

public class ExceptionRecord
{
    public DateTime Timestamp { get; set; }
    public string? ThreadName { get; set; }
    public int ThreadId { get; set; }
    public string? Type { get; set; }
    public string? Message { get; set; }
    public string? Source { get; set; }
    public string? StackTrace { get; set; }
    public string? SourceLocation { get; set; }
}