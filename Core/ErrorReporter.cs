using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using GKME.Localization;

namespace GKME.Core;

/// <summary>
/// Shows fatal errors that happen before (or instead of) an Avalonia window.
/// Windows uses the native message box; Linux has no equivalent, so the text
/// goes to stderr and to the first of <c>zenity</c> / <c>kdialog</c> that is
/// installed. The exception is always written to the log by
/// <see cref="ExceptionLogger"/> as well.
/// </summary>
public static class ErrorReporter
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    private const uint MB_ICONERROR = 0x00000010;
    private const uint MB_OK = 0x00000000;

    public static void Show(string title, string text)
    {
        if (PlatformInfo.IsWindows)
        {
            try
            {
                MessageBoxW(IntPtr.Zero, text, title, MB_ICONERROR | MB_OK);
                return;
            }
            catch { }
        }

        try { Console.Error.WriteLine($"{title}: {text}"); } catch { }
        ShowWithDesktopHelper(title, text);
    }

    private static void ShowWithDesktopHelper(string title, string text)
    {
        if (PlatformInfo.IsWindows) return;
        try
        {
            if (TryStart("zenity", "--error", $"--title={title}", $"--text={text}")) return;
            if (TryStart("kdialog", "--title", title, "--error", text)) return;
        }
        catch { }
    }

    private static bool TryStart(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            return p != null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Formats the standard "something went wrong" block used by both
    /// the exception dialog and the pre-Avalonia handlers.</summary>
    public static string Describe(string headerKey, Exception? ex)
    {
        var header = Localizer.Instance.Get(headerKey);
        var detail = ex?.ToString() ?? Localizer.Instance.Get("Error_Unknown");
        return header + "\n\n" + detail;
    }
}
