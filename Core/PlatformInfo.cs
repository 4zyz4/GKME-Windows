using System;
using System.Runtime.InteropServices;

namespace GKME.Core;

/// <summary>
/// The desktop GKME runs on and the few facts the rest of the app has to
/// branch on. Everything that is not a genuine platform difference belongs
/// somewhere else: the transport, the personas and the GKMD engine are the
/// same on Windows and Linux.
/// </summary>
public static class PlatformInfo
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsLinux => OperatingSystem.IsLinux();

    /// <summary>Name of the adb executable shipped next to the app. The
    /// Android SDK uses <c>adb.exe</c> on Windows and <c>adb</c> elsewhere.</summary>
    public static string AdbFileName => IsWindows ? "adb.exe" : "adb";

    /// <summary>
    /// Subfolder of the bundled <c>platform-tools</c> directory holding this
    /// platform's adb. The Google packages are split per OS only: the Windows
    /// build is 32-bit x86 (it also runs on ARM64 through emulation) and the
    /// Linux build is x86_64, so neither OS needs an architecture subfolder.
    /// </summary>
    public static string AdbPlatformDirName => IsWindows ? "windows" : "linux";

    /// <summary>Only Windows can require a reboot after a transport upgrade;
    /// Linux loads the in-kernel module and is ready immediately.</summary>
    public static bool SupportsRebootPrompt => IsWindows;

    /// <summary>
    /// True on Linux when the process runs as root. Attaching a
    /// <c>vhci_hcd</c> device writes its sysfs attributes, which only root may
    /// do, so controller creation fails without it. Windows gets the same
    /// privilege through the manifest's <c>requireAdministrator</c>, so the
    /// check is only meaningful on Linux.
    /// </summary>
    public static bool IsElevated
    {
        get
        {
            if (!IsLinux) return true;
            try { return GetEuid() == 0; }
            catch { return false; }
        }
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEuid();
}
