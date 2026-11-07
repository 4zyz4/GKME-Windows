using System;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace GKME.Core;

/// <summary>
/// Whether the current desktop can actually draw a tray icon.
///
/// <para>Windows always has a notification area. Linux only shows a
/// StatusNotifierItem when a host owns <c>org.kde.StatusNotifierWatcher</c>:
/// Avalonia accepts the registration even without a host and the icon then
/// simply never appears. Hiding the main window on close must not depend on
/// that, so the well-known name is checked on the session bus.</para>
/// </summary>
public static class TrayHost
{
    private const string StatusNotifierWatcher = "org.kde.StatusNotifierWatcher";

    /// <summary>True when the window may be hidden instead of closed.
    /// Blocking (bounded by a local D-Bus round trip). Any failure reports
    /// false, keeping the window reachable.</summary>
    public static bool IsAvailable()
    {
        if (PlatformInfo.IsWindows) return true;
        if (!PlatformInfo.IsLinux) return false;

        var address = DBusAddress.Session;
        if (string.IsNullOrEmpty(address))
        {
            Log.Warn("tray: no D-Bus session bus address, no StatusNotifier host can be reached");
            return false;
        }

        try
        {
            // Task.Run: the D-Bus awaits must not capture the UI thread's
            // synchronization context, or GetResult() below would deadlock.
            var found = Task.Run(() => HasWatcherAsync(address))
                .WaitAsync(TimeSpan.FromSeconds(3))
                .GetAwaiter().GetResult();
            if (!found)
                Log.Info("tray: no org.kde.StatusNotifierWatcher on the session bus");
            return found;
        }
        catch (Exception ex)
        {
            Log.Warn($"tray: StatusNotifier host check failed: {ex.Message}");
            return false;
        }
    }

    private static async Task<bool> HasWatcherAsync(string address)
    {
        using var connection = new DBusConnection(address);
        await connection.ConnectAsync().ConfigureAwait(false);
        var names = await connection.ListServicesAsync().ConfigureAwait(false);
        return Array.IndexOf(names, StatusNotifierWatcher) >= 0;
    }
}
