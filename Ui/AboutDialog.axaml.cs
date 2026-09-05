using System;
using System.Diagnostics;
using Avalonia.Controls;

namespace GKME.Ui;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        
        var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        var version = !string.IsNullOrEmpty(exePath) && System.IO.File.Exists(exePath)
            ? FileVersionInfo.GetVersionInfo(exePath).FileVersion ?? "未知版本"
            : "未知版本";
        VersionTextBlock.Text = $"版本: {version}";
    }
}