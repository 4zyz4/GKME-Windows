using System;
using Avalonia;

namespace GKME;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            if (ex != null)
            {
                GKME.Core.ExceptionLogger.Record(ex, "AppDomain.CurrentDomain.UnhandledException");
            }

            try
            {
                var crashText = ex?.ToString();
                var dialog = new GKME.Ui.ExceptionDialog(crashText);
                dialog.Show();
            }
            catch { }

            if (e.IsTerminating)
            {
                Thread.Sleep(Timeout.Infinite);
            }
        };

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}