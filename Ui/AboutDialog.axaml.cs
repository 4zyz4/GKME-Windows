using System;
using System.Reflection;
using Avalonia.Controls;

namespace GKME.Ui;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "未知版本";
        VersionTextBlock.Text = $"版本: {version}";
    }
}