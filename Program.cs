using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using GKME.Core;
using GKME.Localization;

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
                ExceptionLogger.Record(ex, "AppDomain.CurrentDomain.UnhandledException");
            }

            ErrorReporter.Show(
                Localizer.Instance.Get("Error_Title"),
                ErrorReporter.Describe("Error_UnhandledException", ex));

            var lifetime = Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            lifetime?.Shutdown(-1);

            if (e.IsTerminating)
            {
                Thread.Sleep(Timeout.Infinite);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ExceptionLogger.Record(e.Exception, "UnobservedTaskException");
            ErrorReporter.Show(
                Localizer.Instance.Get("Error_Title"),
                ErrorReporter.Describe("Error_UnobservedTask", e.Exception));
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
