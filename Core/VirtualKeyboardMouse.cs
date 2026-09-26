using HIDMaestro;

namespace GKME.Core;

/// <summary>
/// The virtual USB HID keyboard and mouse GKME presents through HIDMaestro's
/// USB/IP backend.
///
/// <para>Both devices are backed by the <c>keyboard-composite</c> /
/// <c>mouse-composite</c> profiles in <c>StaticProfileRegistry</c> and are fed
/// with complete HID input reports via <see cref="HMController.SubmitRawReport"/>
/// — the same shared-memory contract the gamepad personas use. This replaces
/// the old user-mode <c>SendInput</c> injection (InputDll.dll): the host now
/// sees genuine USB HID hardware and no injection DLL is loaded into the
/// process.</para>
///
/// <para>All report state is full-state (not press/release deltas): the phone
/// resends the current key set / button mask with every input frame, and the
/// OS keyboard driver produces typematic auto-repeat on its own while a key
/// stays in the bitmap.</para>
/// </summary>
internal sealed class VirtualKeyboardMouse : IDisposable
{
    // Must match StaticProfileRegistry's profile IDs.
    private const string KeyboardProfileId = "keyboard-composite";
    private const string MouseProfileId = "mouse-composite";

    // [modifiers][reserved][256-bit key bitmap].
    private const int KeyboardReportSize = 34;
    // [buttons][dx int16 LE][dy int16 LE][wheel int16 LE][pan int16 LE].
    private const int MouseReportSize = 9;

    private readonly object _lock = new();
    private readonly HMController _keyboard;
    private readonly HMController _mouse;
    private bool _disposed;

    private readonly byte[] _keyboardReport = new byte[KeyboardReportSize];
    private readonly byte[] _lastKeyboardReport = new byte[KeyboardReportSize];
    private bool _hasKeyboardReport;

    private readonly byte[] _mouseReport = new byte[MouseReportSize];

    public VirtualKeyboardMouse(HMContext context)
    {
        var keyboardProfile = context.GetProfile(KeyboardProfileId)
            ?? throw new InvalidOperationException(
                $"HIDMaestro profile '{KeyboardProfileId}' is not loaded.");
        var mouseProfile = context.GetProfile(MouseProfileId)
            ?? throw new InvalidOperationException(
                $"HIDMaestro profile '{MouseProfileId}' is not loaded.");

        _keyboard = context.CreateController(keyboardProfile);
        try
        {
            _mouse = context.CreateController(mouseProfile);
        }
        catch
        {
            try { _keyboard.Dispose(); } catch { }
            throw;
        }
    }

    /// <summary>The live keyboard controller (for teardown bookkeeping).</summary>
    public HMController Keyboard => _keyboard;

    /// <summary>The live mouse controller (for teardown bookkeeping).</summary>
    public HMController Mouse => _mouse;

    /// <summary>Push the full keyboard state. <paramref name="modifiers"/> is the
    /// HID modifier bitmask (bit 0 = Left Ctrl … bit 7 = Right GUI);
    /// <paramref name="keys"/> are HID keyboard-page usages (0x04–0xFF,
    /// modifiers 0xE0–0xE7 excluded). The report is only transmitted when the
    /// state actually changes.</summary>
    public void SubmitKeyboard(uint modifiers, IReadOnlyList<uint> keys)
    {
        lock (_lock)
        {
            if (_disposed) return;

            Array.Clear(_keyboardReport, 0, KeyboardReportSize);
            _keyboardReport[0] = (byte)(modifiers & 0xFF);
            if (keys != null)
            {
                for (int i = 0; i < keys.Count; i++)
                {
                    int code = (int)keys[i];
                    // 0x00–0x03 are reserved; 0xE0–0xE7 live in the modifier
                    // byte and must not also be mirrored into the bitmap.
                    if (code < 0x04 || code > 0xFF) continue;
                    if (code >= 0xE0 && code <= 0xE7) continue;
                    _keyboardReport[2 + (code >> 3)] |= (byte)(1 << (code & 7));
                }
            }

            if (_hasKeyboardReport && ReportsEqual(_lastKeyboardReport, _keyboardReport))
                return;

            try { _keyboard.SubmitRawReport(_keyboardReport); }
            catch (ObjectDisposedException) { return; }
            catch { return; }

            Buffer.BlockCopy(_keyboardReport, 0, _lastKeyboardReport, 0, KeyboardReportSize);
            _hasKeyboardReport = true;
        }
    }

    /// <summary>Push one mouse frame. The report is transmitted on <b>every</b>
    /// call — not only when the deltas are non-zero — so the mouse keeps the
    /// same report cadence as the gamepad personas (and Windows' mouse stack
    /// keeps reading it at the endpoint interval instead of only at the rate
    /// input deltas happen to occur). Buttons: bit 0 left, 1 right, 2 middle,
    /// 3 back, 4 forward. <paramref name="wheel"/> / <paramref name="pan"/> are
    /// 16-bit relative values in WHEEL_DELTA units: the descriptor declares a
    /// 120-count Resolution Multiplier, so the host maps one detent to 120 raw
    /// units and keeps the sub-detent precision for high-resolution scrolling.</summary>
    public void SubmitMouse(int dx, int dy, int wheel, int pan, uint buttons)
    {
        lock (_lock)
        {
            if (_disposed) return;

            _mouseReport[0] = (byte)(buttons & 0xFF);
            WriteInt16LE(_mouseReport, 1, dx);
            WriteInt16LE(_mouseReport, 3, dy);
            WriteInt16LE(_mouseReport, 5, wheel);
            WriteInt16LE(_mouseReport, 7, pan);

            try { _mouse.SubmitRawReport(_mouseReport); }
            catch (ObjectDisposedException) { return; }
            catch { return; }
        }
    }

    /// <summary>Release every key and mouse button (neutral reports). Safe to
    /// call when nothing was ever pressed.</summary>
    public void ReleaseAll()
    {
        SubmitKeyboard(0, Array.Empty<uint>());
        SubmitMouse(0, 0, 0, 0, 0);
    }

    private static bool ReportsEqual(byte[] a, byte[] b)
    {
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    private static void WriteInt16LE(byte[] buf, int offset, int value)
    {
        short v = (short)Math.Clamp(value, short.MinValue, short.MaxValue);
        buf[offset] = (byte)v;
        buf[offset + 1] = (byte)(v >> 8);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        try { _keyboard.Dispose(); } catch { }
        try { _mouse.Dispose(); } catch { }
    }
}
