using System;
using Avalonia.Controls;

namespace GKME.Ui;

/// <summary>Prompt telling the user that the USBip transport must be
/// installed or upgraded. The caller performs the install when
/// <see cref="InstallRequested"/> fires; this window only drives the
/// text.</summary>
public partial class UsbipInstallDialog : Window
{
    public event Action? InstallRequested;

    public UsbipInstallDialog()
    {
        InitializeComponent();
        InstallButton.Click += (_, _) => InstallRequested?.Invoke();
        CancelButton.Click += (_, _) => Close();
    }

    public void SetMessage(string text) => MessageText.Text = text;

    public void SetStatus(string text) => StatusText.Text = text;

    /// <summary>Disable both buttons while the installer runs.</summary>
    public void SetBusy()
    {
        InstallButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
    }

    /// <summary>Final state: no more install, only close.</summary>
    public void SetCompleted(string text)
    {
        StatusText.Text = text;
        InstallButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        CancelButton.Content = "关闭";
    }
}
