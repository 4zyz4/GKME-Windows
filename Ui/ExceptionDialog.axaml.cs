using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Platform;
using GKME.Core;
using GKME.Localization;

namespace GKME.Ui;

public partial class ExceptionDialog : Window
{
    private readonly string _crashText;

    public ExceptionDialog() => _crashText = string.Empty;

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
        Window? GetOwner()
        {
            if (MainWindow.Instance is Window mw) return mw;
            var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            if (lifetime?.MainWindow is Window w) return w;
            return null;
        }

        try
        {
            var owner = GetOwner();
            var textBlock = new TextBlock { Text = Localizer.Instance.Get("Exception_Copied"), Margin = new Thickness(20) };
            var dlg = new Window
            {
                Content = textBlock,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Width = 250,
                Height = 100,
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1E1E1E")),
                CanResize = false
            };
            #pragma warning disable CS8604
            try
            {
                await dlg.ShowDialog(owner);
            }
            catch
            {
                dlg.Show();
            }
#pragma warning restore CS8604
        }
        catch
        {
            var textBlock = new TextBlock { Text = Localizer.Instance.Get("Exception_CopyFailed"), Margin = new Thickness(20) };
            var dlg = new Window
            {
                Content = textBlock,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Width = 250,
                Height = 100,
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1E1E1E"))
            };
            dlg.Show();
        }
    }
}