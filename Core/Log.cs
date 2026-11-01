using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GKME.Core;

/// <summary>
/// Process-wide diagnostic log. Unlike the old <c>AppContext.BaseDirectory</c>
/// file it never writes next to the executable: an AppImage mounts its payload
/// read-only, and on Windows the program directory is under Program Files.
///
/// <para>Linux: <c>/var/log/gkme/gkme.log</c> when running as root (the normal
/// AppImage/pkexec path), otherwise
/// <c>$XDG_STATE_HOME/GKME/logs/gkme.log</c> (usually
/// <c>~/.local/state/GKME/logs/gkme.log</c>). Windows:
/// <c>%LOCALAPPDATA%\GKME\logs\gkme.log</c>. Every line is also mirrored to
/// stderr so a terminal launch shows it immediately.</para>
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static string? _filePath;

    /// <summary>Absolute path of the active log file. Resolved on first use
    /// and never throws: when no candidate directory is writable the file
    /// falls back to the system temporary directory.</summary>
    public static string FilePath => _filePath ??= ResolvePath();

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? exception = null)
    {
        if (exception != null)
            message = message + Environment.NewLine + exception;
        Write("ERROR", message);
    }

    /// <summary>One-time startup banner: enough context to reproduce a bug from
    /// the log alone (build, OS/architecture, elevation and runtime paths).</summary>
    public static void StartupBanner(string appName, string version)
    {
        Info($"=== {appName} {version} starting ===");
        Info($"OS      : {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        Info($"Arch    : OS={System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}, " +
             $"process={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        Info($"Elevated: {PlatformInfo.IsElevated}");
        Info($"BaseDir : {AppContext.BaseDirectory}");
        Info($"LogFile : {FilePath}");
    }

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";

        try { Console.Error.WriteLine(line); } catch { }

        try
        {
            lock (Gate)
            {
                var path = FilePath;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(path, line + Environment.NewLine, Utf8NoBom);
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    private static string ResolvePath()
    {
        foreach (var candidate in Candidates())
        {
            try
            {
                var dir = Path.GetDirectoryName(candidate);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                Rotate(candidate);
                // Probe write access now, so a later write cannot pick a
                // directory the user cannot use.
                using (new FileStream(candidate, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { }
                return candidate;
            }
            catch
            {
                // Try the next location.
            }
        }

        return Path.Combine(Path.GetTempPath(), "gkme.log");
    }

    private static IEnumerable<string> Candidates()
    {
        if (PlatformInfo.IsWindows)
        {
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GKME", "logs", "gkme.log");
            yield break;
        }

        // Root writes a system-wide, world-readable log so the user can simply
        // `tail` it without sudo. If /var/log is not writable (containers,
        // unprivileged user namespaces) fall through to the per-user path.
        if (PlatformInfo.IsElevated)
            yield return "/var/log/gkme/gkme.log";

        var stateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (string.IsNullOrWhiteSpace(stateHome))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
                stateHome = Path.Combine(home, ".local", "state");
        }

        if (!string.IsNullOrWhiteSpace(stateHome))
            yield return Path.Combine(stateHome, "GKME", "logs", "gkme.log");

        yield return Path.Combine(Path.GetTempPath(), "gkme.log");
    }

    private static void Rotate(string path)
    {
        try
        {
            const long maxBytes = 4 * 1024 * 1024;
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length < maxBytes) return;

            var previous = path + ".1";
            try { File.Delete(previous); } catch { }
            File.Move(path, previous);
        }
        catch
        {
            // Rotation is best-effort; keep appending if it fails.
        }
    }
}
