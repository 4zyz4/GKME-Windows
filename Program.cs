using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using GKME.Localization;

namespace GKME;

class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);

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

            var text = Localizer.Instance.Get("Error_UnhandledException") + "\n\n"
                + (ex?.ToString() ?? Localizer.Instance.Get("Error_Unknown"));
            MessageBoxW(IntPtr.Zero, text, Localizer.Instance.Get("Error_Title"), 0x00000010 | 0x00000000);

            var lifetime = Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            lifetime?.Shutdown(-1);

            if (e.IsTerminating)
            {
                Thread.Sleep(Timeout.Infinite);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            GKME.Core.ExceptionLogger.Record(e.Exception, "UnobservedTaskException");
            var text = Localizer.Instance.Get("Error_UnobservedTask") + "\n\n"
                + (e.Exception.ToString() ?? Localizer.Instance.Get("Error_Unknown"));
            MessageBoxW(IntPtr.Zero, text, Localizer.Instance.Get("Error_Title"), 0x00000010 | 0x00000000);
            e.SetObserved();

            try
            {
                var lifetime = Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
                if (lifetime != null)
                    lifetime.Shutdown(-1);
            }
            catch { }
        };

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}