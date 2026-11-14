using Avalonia.Controls;
using Avalonia.Interactivity;

namespace GKME.Ui;

/// <summary>关于页的 GPLv3 徽章：在对话框内展示仓库自带的协议全文。</summary>
public partial class LicenseDialog : Window
{
    public LicenseDialog()
    {
        InitializeComponent();
        LicenseText.Text = GplLicenseText.Load();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
