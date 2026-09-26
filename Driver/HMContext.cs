using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HIDMaestro.Internal;

namespace HIDMaestro;

/// <summary>
/// The top-level entry point for the HIDMaestro SDK. Owns the in-process
/// state for one consuming application: loaded profile catalog, allocated
/// controller indices, and the lifecycle of every <see cref="HMController"/>
/// it creates.
///
/// <para><b>Lifecycle:</b> create one <see cref="HMContext"/> at app startup,
/// dispose at shutdown. Disposing the context disposes every controller it
/// owns. Multiple contexts in one process are supported but not encouraged
/// (they share the same controller-index pool).</para>
///
/// <para><b>Backend:</b> every controller is presented through the bundled
/// USB/IP transport (usbip-win2). No UMDF2 driver, INF, or PnP device node
/// is involved.</para>
///
/// <para><b>Admin requirement:</b> Windows requires SeLoadDriverPrivilege
/// (admin) for the one-time USB/IP transport install and for attaching each
/// virtual controller. This matches every other virtual-controller library
/// on Windows (ViGEmBus, vJoy, etc.).</para>
/// </summary>
public sealed class HMContext : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<int, HMController> _controllers = new();
    private readonly List<HMProfile> _profiles = new();
    private readonly Dictionary<string, HMProfile> _profilesById = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>Create a new SDK context. Loading profiles and creating
    /// controllers are separate steps; this constructor only allocates the
    /// in-process state.
    ///
    /// <para>v1.3.0 — fires a background warm-up that pre-parses the embedded
    /// profile catalog so the first consumer-side profile lookup is
    /// instant.</para></summary>
    public HMContext()
    {
        System.Threading.Tasks.Task.Run(() =>
        {
            try { _ = Internal.ProfileDatabase.LoadEmbedded(); } catch { }
        });
    }

    // ════════════════════════════════════════════════════════════════════
    //  Transport lifecycle
    // ════════════════════════════════════════════════════════════════════

    /// <summary>True if the USB/IP transport (usbip-win2's virtual host
    /// controller) is already installed on this machine. Does not require
    /// admin to check.</summary>
    public bool IsDriverInstalled => Internal.Usbip.UsbipBackend.IsAvailable;

    /// <summary>Issue #39: true when the USB transport that composite
    /// personas ride (usbip-win2's virtual host controller) is already
    /// installed on this machine. Pure presence probe: requires no admin and
    /// installs nothing.</summary>
    public static bool IsUsbipBackendAvailable => Internal.Usbip.UsbipBackend.IsAvailable;

    /// <summary>Issue #39: install/upgrade the bundled USB transport now.
    /// Runs the installer silently (no series of prompts) and is the
    /// programmatic counterpart of the UI's install button. Idempotent,
    /// requires elevation, and reports progress through
    /// <paramref name="progress"/>.</summary>
    public static void InstallUsbipBackend(Action<string>? progress = null)
    {
        var result = Internal.Usbip.UsbipDriverInstaller.Install(progress, interactive: false);
        if (result != Internal.Usbip.UsbipInstallResult.Success)
            throw new InvalidOperationException(
                result == Internal.Usbip.UsbipInstallResult.RebootRequired
                    ? "usbip-win2 was installed but a reboot is required before it can be used."
                    : "The usbip-win2 install did not complete.");
    }

    /// <summary>Ensure the bundled USB/IP transport is present and at this
    /// build's interface. Idempotent. Installing now requires the user's
    /// explicit action, so this does not run the installer itself: when the
    /// transport is missing or too old it throws
    /// <see cref="Internal.Usbip.UsbipInstallRequiredException"/> (and
    /// <see cref="Internal.Usbip.UsbipRebootRequiredException"/> after an
    /// upgrade that only a reboot will load), which a UI turns into a prompt.
    /// Pair it with <see cref="InstallUsbipBackend"/> when the caller wants
    /// to install programmatically.</summary>
    /// <exception cref="UnauthorizedAccessException">Thrown if the calling
    /// process is not elevated.</exception>
    public void InstallDriver()
    {
        ThrowIfDisposed();
        Internal.Usbip.UsbipDriverInstaller.EnsureInstalled();
    }

    /// <summary>Removes ALL HIDMaestro virtual devices on the system,
    /// including orphans from previous runs that weren't cleanly disposed.
    /// Static (no HMContext instance needed). Requires admin.</summary>
    public static void RemoveAllVirtualControllers()
    {
        Internal.Usbip.UsbipBackend.DetachAllOwned();
    }

    /// <summary>Same device eviction as
    /// <see cref="RemoveAllVirtualControllers()"/>. The USB/IP transport has
    /// no installed driver package to preserve, so <paramref name="preserveInstall"/>
    /// is ignored.</summary>
    public static void RemoveAllVirtualControllers(bool preserveInstall)
    {
        Internal.Usbip.UsbipBackend.DetachAllOwned();
    }

    /// <summary>Disposes a set of controllers concurrently. The per-controller
    /// wall-clock for each Dispose() call is reported through
    /// <paramref name="perControllerCallback"/> so callers can log a
    /// "disposed slot N in M ms" line for each.</summary>
    public void DisposeControllersInParallel(
        IEnumerable<HMController> controllers,
        Action<HMController, long>? perControllerCallback = null)
    {
        if (controllers == null) throw new ArgumentNullException(nameof(controllers));
        var arr = controllers.Where(c => c != null).ToArray();
        if (arr.Length == 0) return;
        if (arr.Length == 1)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { arr[0].Dispose(); } catch { }
            perControllerCallback?.Invoke(arr[0], sw.ElapsedMilliseconds);
            return;
        }
        System.Threading.Tasks.Parallel.ForEach(arr, c =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { c.Dispose(); } catch { }
            perControllerCallback?.Invoke(c, sw.ElapsedMilliseconds);
        });
    }

    // ════════════════════════════════════════════════════════════════════
    //  Profile catalog
    // ════════════════════════════════════════════════════════════════════

    /// <summary>All profiles currently loaded in this context, in stable
    /// order by ID. Empty until you call one of the <c>LoadProfiles*</c>
    /// methods.</summary>
    public IReadOnlyList<HMProfile> AllProfiles
    {
        get { ThrowIfDisposed(); lock (_lock) return _profiles.ToArray(); }
    }

    /// <summary>Look up a profile by its stable ID slug. Returns null if
    /// no profile with that ID is loaded.</summary>
    public HMProfile? GetProfile(string id)
    {
        if (id == null) throw new ArgumentNullException(nameof(id));
        ThrowIfDisposed();
        lock (_lock)
            return _profilesById.TryGetValue(id, out var p) ? p : null;
    }

    /// <summary>Load profiles from a directory containing .json files
    /// matching the HIDMaestro profile schema. Profiles loaded from
    /// multiple sources are merged by ID; later loads override earlier
    /// ones. Schema files (schema.json) are ignored.</summary>
    public int LoadProfilesFromDirectory(string profilesDir)
    {
        if (profilesDir == null) throw new ArgumentNullException(nameof(profilesDir));
        ThrowIfDisposed();
        if (!Directory.Exists(profilesDir))
            throw new DirectoryNotFoundException($"Profiles directory not found: {profilesDir}");

        var db = ProfileDatabase.Load(profilesDir);
        int added = 0;
        lock (_lock)
        {
            foreach (var inner in db.All)
            {
                if (_profilesById.ContainsKey(inner.Id)) continue; // skip dupes
                var pub = new HMProfile(inner);
                _profiles.Add(pub);
                _profilesById[inner.Id] = pub;
                added++;
            }
            _profiles.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.Ordinal));
        }
        return added;
    }

    /// <summary>Load the default profile catalog embedded in the SDK
    /// assembly.</summary>
    public int LoadDefaultProfiles()
    {
        ThrowIfDisposed();
        int added = 0;
        lock (_lock)
        {
            foreach (var entry in StaticProfileRegistry.AllProfiles)
            {
                if (_profilesById.ContainsKey(entry.Key)) continue;
                var pub = new HMProfile(entry.Value);
                _profiles.Add(pub);
                _profilesById[entry.Key] = pub;
                added++;
            }
            _profiles.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.Ordinal));
        }
        return added;
    }

    // ════════════════════════════════════════════════════════════════════
    //  Controller lifecycle
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Create a new virtual controller using the given profile.
    /// Allocates the next free controller index, creates the USB/IP device
    /// (which pre-creates the per-index shared sections and events and
    /// attaches through usbip-win2's vhci), and returns a live
    /// <see cref="HMController"/> ready for input via
    /// <see cref="HMController.SubmitState"/> or
    /// <see cref="HMController.SubmitRawReport"/>. Requires admin.
    ///
    /// <para>All profile paths ride the USB/IP backend: plain HID
    /// (DualSense, DualShock 4), and vendor-class (Xbox 360 wired).</para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    /// <exception cref="ArgumentException">The profile has neither a HID
    /// descriptor nor a USB configuration and can't be deployed.</exception>
    /// <exception cref="InvalidOperationException">Transport install failed or
    /// device attach failed.</exception>
    public HMController CreateController(HMProfile profile)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        if (!profile.IsDeployable && profile.Inner.UsbConfiguration == null)
            throw new ArgumentException($"Profile '{profile.Id}' has no HID descriptor or USB configuration and cannot be deployed.", nameof(profile));
        ThrowIfDisposed();

        int index;
        lock (_lock)
        {
            index = 0;
            while (_controllers.ContainsKey(index)) index++;
        }

        return CreateUsbipController(index, profile);
    }

    // The USB/IP create path. The backend's device emulator pre-creates the
    // per-index shared sections and events, attaches through usbip-win2's
    // vhci, and the HMController then binds to the same sections it always
    // does. No PnP, no INF, no driver install.
    private HMController CreateUsbipController(int index, HMProfile profile)
    {
        Internal.Usbip.UsbipBackendHandle handle =
            Internal.Usbip.UsbipBackend.CreateDevice(profile.Inner, index);
        try
        {
            var controller = new HMController(this, index, profile, instanceId: null, handle);
            lock (_lock) _controllers[index] = controller;
            return controller;
        }
        catch
        {
            try { handle.Dispose(); } catch { }
            try { Internal.SharedMemoryIO.DestroyController(index); } catch { }
            throw;
        }
    }

    /// <summary>Create a controller pinned to a specific index. Used by live
    /// profile-switching workflows where the consumer wants to dispose the
    /// existing controller at index N and replace it with one running a
    /// different profile while keeping the same N. The index must be free
    /// (the previous controller at that index must already be disposed).</summary>
    /// <exception cref="InvalidOperationException">If the index is already
    /// in use by another live controller.</exception>
    public HMController CreateControllerAt(int index, HMProfile profile)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        if (!profile.IsDeployable && profile.Inner.UsbConfiguration == null)
            throw new ArgumentException($"Profile '{profile.Id}' has no HID descriptor or USB configuration and cannot be deployed.", nameof(profile));
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        ThrowIfDisposed();

        lock (_lock)
        {
            if (_controllers.ContainsKey(index))
                throw new InvalidOperationException(
                    $"Controller index {index} is already in use. Dispose the existing controller first.");
        }

        return CreateUsbipController(index, profile);
    }

    /// <summary>All currently-live controllers owned by this context.</summary>
    public IReadOnlyCollection<HMController> ActiveControllers
    {
        get { ThrowIfDisposed(); lock (_lock) return _controllers.Values.ToArray(); }
    }

    /// <summary>Re-apply friendly names to every live controller. USB/IP
    /// devices take their strings from the profile's device descriptor, so
    /// this is a no-op for the USB/IP backend (kept for API compatibility).
    /// </summary>
    public void FinalizeNames()
    {
        ThrowIfDisposed();
    }

    // Called by HMController.Dispose; the context tears down its half of the state.
    internal void OnControllerDisposing(HMController controller)
    {
        // Tear the device down BEFORE freeing its index. The USB/IP dispose
        // can block briefly waiting for the vhci port to be released; if the
        // index were freed first, a concurrent CreateController could pick
        // it (and hence the same busid "1-N") while the old device is still
        // registered, letting the old teardown unregister/destroy the new
        // controller's section. Keeping the index occupied until the device
        // is gone serializes the two.
        try { controller.UsbipHandle?.Dispose(); } catch { }
        try { Internal.SharedMemoryIO.DestroyController(controller.Index); } catch { }
        lock (_lock) _controllers.Remove(controller.Index);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(HMContext));
    }

    /// <summary>Disposes every controller this context owns and frees its
    /// resources. Safe to call multiple times.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        HMController[] toDispose;
        lock (_lock)
        {
            toDispose = _controllers.Values.ToArray();
            _controllers.Clear();
        }
        if (toDispose.Length == 0) return;
        if (toDispose.Length == 1)
        {
            try { toDispose[0].Dispose(); } catch { /* swallow during shutdown */ }
            return;
        }
        System.Threading.Tasks.Parallel.ForEach(toDispose, c =>
        {
            try { c.Dispose(); } catch { /* swallow during shutdown */ }
        });
    }
}
