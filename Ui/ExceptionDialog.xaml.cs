using System.Windows;
using GKME.Core;

namespace GKME.Ui;

public partial class ExceptionDialog : Window
{
    private readonly string _crashText;

    public ExceptionDialog(string? crashText = null)
    {
        _crashText = crashText ?? ExceptionLogger.FormatForClipboard();
        InitializeComponent();
        ErrorText.Text = _crashText;
    }

    private void OnCopyAll(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_crashText);
            MessageBox.Show("已复制到剪贴板", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch
        {
            MessageBox.Show("复制到剪贴板失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        // 关闭时由 App 端调用 Shutdown，这里不需要额外操作
        base.OnClosed(e);
    }
}