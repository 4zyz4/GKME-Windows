using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using GKME.Core;
using GKME.Localization;

namespace GKME;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "?";
        Log.StartupBanner("GKME", version);

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
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

        // Linux needs root to attach vhci_hcd devices, and root's fontconfig
        // normally sees only system fonts. A minimal install (or WSLg) has no
        // CJK font there, so Chinese would render as missing-glyph boxes. Use
        // the bundled WQY Micro Hei for CJK codepoints when the process can
        // see no CJK font at all; otherwise keep the system font.
        if (PlatformInfo.IsLinux && !HasSystemCjkFont())
            builder = builder.With(CjkFallbackOptions());

        return builder;
    }

    /// <summary>Asks fontconfig whether any font covers Chinese. A missing
    /// <c>fc-list</c> (or a failure) is treated as "no CJK font", which only
    /// means the embedded fallback gets registered — never worse.</summary>
    private static bool HasSystemCjkFont()
    {
        try
        {
            var psi = new ProcessStartInfo("fc-list")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(":lang=zh");

            using var p = Process.Start(psi);
            if (p == null) return false;

            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(2000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return false;
            }

            return p.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
        }
        catch
        {
            return false;
        }
    }

    private static FontManagerOptions CjkFallbackOptions() => new()
    {
        FontFallbacks = new[]
        {
            new FontFallback
            {
                FontFamily = new FontFamily(
                    "avares://GKME/Assets/Fonts/wqy-microhei.ttf#WenQuanYi Micro Hei"),
                // CJK radicals, kana, unified ideographs (including extension
                // A and compatibility forms), CJK punctuation and fullwidth
                // forms.
                UnicodeRange = UnicodeRange.Parse(
                    "U+2E80-9FFF,U+F900-FAFF,U+FE30-FE4F,U+FF00-FFEF,U+20000-2FA1F"),
            },
        },
    };
}
