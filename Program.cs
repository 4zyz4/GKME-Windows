using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

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

            var text = "程序遇到未处理的异常。\n\n" + (ex?.ToString() ?? "未知错误");
            MessageBoxW(IntPtr.Zero, text, "程序错误", 0x00000010 | 0x00000000);

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
            var text = "未观察到的任务异常。\n\n" + (e.Exception.ToString() ?? "未知错误");
            MessageBoxW(IntPtr.Zero, text, "程序错误", 0x00000010 | 0x00000000);
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