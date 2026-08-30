using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Platform;
using GKME.Core;

namespace GKME.Ui;

public partial class ExceptionDialog : Window
{
    private readonly string _crashText;

    public ExceptionDialog(string? crashText = null)
    {
        _crashText = crashText ?? ExceptionLogger.FormatForClipboard();
        InitializeComponent();
        if (ErrorText is not null)
        {
            ErrorText.Text = _crashText;
        }

        Closed += OnDialogClosed;
    }

    private void OnDialogClosed(object? sender, EventArgs e)
    {
        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
            {
                lifetime.Shutdown();
            }
        }
        catch { }
    }

    private async void OnCopyAll(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard is not null)
            {
                await topLevel.Clipboard.SetTextAsync(_crashText);
            }
            
            var textBlock = new TextBlock { Text = "已复制到剪贴板", Margin = new Thickness(20) };
            var dlg = new Window
            {
                Content = textBlock,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Width = 250,
                Height = 100
            };
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime && lifetime.MainWindow != null)
            {
                await dlg.ShowDialog(lifetime.MainWindow);
            }
            else
            {
                await dlg.ShowDialog(null);
            }
        }
        catch
        {
            var textBlock = new TextBlock { Text = "复制到剪贴板失败", Margin = new Thickness(20) };
            var dlg = new Window
            {
                Content = textBlock,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Width = 250,
                Height = 100
            };
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime && lifetime.MainWindow != null)
            {
                await dlg.ShowDialog(lifetime.MainWindow);
            }
            else
            {
                await dlg.ShowDialog(null);
            }
        }
    }
}