using System.Diagnostics;
using Avalonia.Controls;
using GKME.Core;

namespace GKME.Ui;

public partial class RebootPromptDialog : Window
{
    public RebootPromptDialog()
    {
        InitializeComponent();
        LaterButton.Click += (_, _) => Close();
        RebootButton.Click += (_, _) =>
        {
            RebootNow();
            Close();
        };
    }

    private static void RebootNow()
    {
        // Only Windows can require a restart after a transport upgrade.
        if (!PlatformInfo.IsWindows) return;
        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 5")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch
        {
            // If shutdown could not be started the user can reboot manually;
            // there is nothing else to do here.
        }
    }
}
