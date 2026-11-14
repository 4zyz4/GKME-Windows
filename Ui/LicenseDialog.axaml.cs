using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using GKME.Core;
using GKME.Localization;

namespace GKME.Ui;

/// <summary>关于页的 GPLv3 徽章：在对话框内展示仓库自带的协议全文。</summary>
public partial class LicenseDialog : Window
{
    private static readonly Uri LicenseUri = new("avares://GKME/Assets/GPL-3.0.txt");

    public LicenseDialog()
    {
        InitializeComponent();
        LoadLicense();
    }

    private void LoadLicense()
    {
        try
        {
            using var stream = AssetLoader.Open(LicenseUri);
            using var reader = new StreamReader(stream);
            LicenseText.Text = reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            Log.Warn($"license: could not load GPL text: {ex.Message}");
            LicenseText.Text = Localizer.Instance.Get("About_GplUnavailable");
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
