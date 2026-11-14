using Avalonia.Controls;
using Avalonia.Interactivity;

namespace GKME.Ui;

/// <summary>关于页的「赞助」按钮：展示赞助二维码图片。</summary>
public partial class SponsorDialog : Window
{
    public SponsorDialog()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
