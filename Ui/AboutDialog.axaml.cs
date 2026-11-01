using System;
using Avalonia.Controls;
using GKME.Core;
using GKME.Localization;

namespace GKME.Ui;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        UpdateVersion();

        Localizer.Instance.LanguageChanged += OnLanguageChanged;
        Closed += OnClosed;
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => UpdateVersion();

    private void OnClosed(object? sender, EventArgs e)
    {
        Localizer.Instance.LanguageChanged -= OnLanguageChanged;
    }

    private void UpdateVersion()
    {
        var version = typeof(AboutDialog).Assembly.GetName().Version?.ToString()
            ?? Localizer.Instance.Get("UnknownVersion");
        VersionTextBlock.Text = Localizer.Instance.Format("Version", version);
        LogPathTextBlock.Text = Localizer.Instance.Format("About_Log", Log.FilePath);
    }
}
