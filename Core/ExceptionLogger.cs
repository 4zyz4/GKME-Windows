using System.Globalization;
using System.Text;

namespace GamepadEmuHost.Core;

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
        sb.AppendLine("Gamepad Emu Host - 异常日志");
        sb.AppendLine($"收集于 {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss UTC}");
        sb.AppendLine($"共 {_logs.Count} 条记录");
        sb.AppendLine(new string('-', 80));
        sb.AppendLine();

        for (int i = _logs.Count - 1; i >= 0; i--)
        {
            var r = _logs[i];
            sb.AppendLine($"[{r.Timestamp:yyyy-MM-dd HH:mm:ss}] #{_logs.Count - i}");
            sb.AppendLine($"来源: {r.SourceLocation}");
            sb.AppendLine($"线程: {r.ThreadName} (ID={r.ThreadId})");
            sb.AppendLine($"类型: {r.Type}");

            if (!string.IsNullOrEmpty(r.Source))
                sb.AppendLine($"Source: {r.Source}");

            if (!string.IsNullOrEmpty(r.Message))
                sb.AppendLine($"消息: {r.Message}");

            sb.AppendLine("堆栈:");
            sb.AppendLine(r.StackTrace);
            sb.AppendLine(new string('-', 60));
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