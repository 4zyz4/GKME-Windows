using System;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GKME.Core;
using GKME.Localization;

namespace GKME.Ui;

/// <summary>Help → Show log window: live view of the in-memory log for the
/// current run. Nothing is read from or written to disk.</summary>
public partial class LogWindow : Window
{
    private static LogWindow? _instance;

    private long _lastSequence;
    private int _displayedLines;
    private bool _flushQueued;
    private bool _closed;

    public LogWindow()
    {
        InitializeComponent();
        _instance = this;

        Log.EntryAdded += OnEntryAdded;
        Log.Cleared += OnLogCleared;
        Localizer.Instance.LanguageChanged += OnLanguageChanged;
        Closed += OnClosed;

        Refresh();
    }

    /// <summary>Opens the window, or brings the existing one to the front.</summary>
    public static void ShowWindow()
    {
        if (_instance is { } existing)
        {
            existing.Show();
            existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        new LogWindow().Show();
    }

    private void OnEntryAdded(LogEntry entry) => QueueFlush();

    /// <summary>Coalesces bursts of log writes into one UI update.</summary>
    private void QueueFlush()
    {
        if (_flushQueued || _closed) return;
        _flushQueued = true;

        Dispatcher.UIThread.Post(() =>
        {
            _flushQueued = false;
            if (!_closed) Refresh();
        }, DispatcherPriority.Background);
    }

    private void Refresh()
    {
        var entries = Log.GetEntriesAfter(_lastSequence, out var reset);
        if (reset)
        {
            LogText.Text = string.Empty;
            _displayedLines = 0;
        }

        // Keep the visible text bounded the same way the in-memory buffer is:
        // once the old lines fall out of the log, rebuild from what remains.
        if (_displayedLines + entries.Count > Log.RetentionLimit)
        {
            entries = Log.GetEntriesAfter(0, out _);
            LogText.Text = string.Empty;
            _displayedLines = 0;
        }

        if (entries.Count == 0)
        {
            UpdateCount();
            return;
        }

        var sb = new StringBuilder(LogText.Text ?? string.Empty);
        foreach (var entry in entries)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(entry.ToString());
        }

        _lastSequence = entries[^1].Sequence;
        _displayedLines += entries.Count;

        var text = sb.ToString();
        LogText.Text = text;
        LogText.CaretIndex = text.Length;
        UpdateCount();
    }

    private void UpdateCount() =>
        CountText.Text = Localizer.Instance.Format("Log_Count", Log.Count);

    private void OnLanguageChanged(object? sender, EventArgs e) => UpdateCount();

    /// <summary>The buffer was emptied from somewhere; drop the shown text but
    /// keep the sequence so new lines continue to append.</summary>
    private void OnLogCleared() => Dispatcher.UIThread.Post(() =>
    {
        if (_closed) return;
        LogText.Text = string.Empty;
        _displayedLines = 0;
        UpdateCount();
    }, DispatcherPriority.Background);

    private async void OnCopyAllClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
                await clipboard.SetTextAsync(Log.FormatAll());
        }
        catch (Exception ex)
        {
            Log.Warn($"log: copy failed: {ex.Message}");
        }
    }

    private void OnClearClick(object? sender, RoutedEventArgs e) => Log.Clear();

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        Log.EntryAdded -= OnEntryAdded;
        Log.Cleared -= OnLogCleared;
        Localizer.Instance.LanguageChanged -= OnLanguageChanged;
        if (ReferenceEquals(_instance, this)) _instance = null;
    }
}
