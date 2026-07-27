using GamepadEmu.Protocol;
using Google.Protobuf;
using HIDMaestro;

namespace GamepadEmuHost.Core;

public class GamepadSession : IDisposable
{
    private const int ConnectionTimeoutMs = 3000;

    private readonly Func<IMessage, Task> _sendToPhone;
    private readonly HMContext _hmContext;
    private CancellationTokenSource _cts = new();
    private Task _reportTask = Task.CompletedTask;
    private bool _disposed;
    private bool _readyFired;
    private DateTime _lastReceiveTime = DateTime.UtcNow;
    private byte _lastLargeMotor;
    private byte _lastSmallMotor;
    private ulong _lastSeq;
    private DateTime _lastInputTime = DateTime.MinValue;
    private int _inputCountThisSecond;
    private int _measuredInputRate;
    private GamepadInput? _latestInput;
    private GamepadInput? _lastReportedInput;
    private readonly object _inputLock = new();
    private volatile bool _switchingMode;
    private byte _previousTouchCounter;
    private int _ds4TouchCounter;
    private readonly byte[] _touchIds = new byte[2];
    private readonly bool[] _prevTouchActive = new bool[2];
    private byte _nextTrackingId;

    public DiscoveredDevice Device { get; }
    public ControllerMode Mode { get; private set; }
    public HMController? Controller { get; private set; }
    public bool IsConnected => !_disposed;
    public bool IsReady => Controller != null;
    public bool HasBeenReady => _readyFired;

    public event Action<GamepadSession>? OnReady;
    public event Action<GamepadSession>? OnConnectionLost;
    public event Action<GamepadSession>? OnDisconnected;

    public GamepadSession(DiscoveredDevice device, Func<IMessage, Task> sendToPhone, HMContext hmContext)
    {
        Device = device;
        _sendToPhone = sendToPhone;
        _hmContext = hmContext;
    }

    public void Start()
    {
        _reportTask = Task.Run(async () =>
        {
            try
            {
                await ReportLoop(_cts.Token);
            }
            catch (OperationCanceledException) { }
            catch { }
        });
        _ = Task.Run(async () =>
        {
            try
            {
                await WatchdogLoop(_cts.Token);
            }
            catch (OperationCanceledException) { }
            catch { }
        });
    }

    private async Task WatchdogLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct);
            if (_disposed) return;
            if (_switchingMode) continue;
            if (Controller == null) continue;
            if ((DateTime.UtcNow - _lastReceiveTime).TotalMilliseconds > ConnectionTimeoutMs)
            {
                _readyFired = false;
                ResetController();
                try { OnConnectionLost?.Invoke(this); } catch { }
            }
        }
    }

    public void HandleClientToServer(ClientToServer cts)
    {
        try
        {
            _lastReceiveTime = DateTime.UtcNow;
            switch (cts.PayloadCase)
            {
                case ClientToServer.PayloadOneofCase.Hello:
                    HandleHello(cts.Hello);
                    break;
                case ClientToServer.PayloadOneofCase.KeepAlive:
                    break;
            }
        }
        catch { }
    }

    public void HandleUdpGamepadInput(GamepadInput input)
    {
        _lastReceiveTime = DateTime.UtcNow;
        if (!IsReady) return;
        HandleGamepadInput(input);
    }

    public async Task SendAsync(IMessage message)
    {
        if (_disposed) return;
        try
        {
            await _sendToPhone(message);
        }
        catch { }
    }

    private void HandleHello(Hello hello)
    {
        if (_disposed) return;

        if (Controller == null && !_switchingMode)
        {
            var ctrl = CreateFreshController(ControllerMode.Ds4);
            RegisterOutputHandlers(ctrl);
            Controller = ctrl;
            Mode = ControllerMode.Ds4;
            _lastLargeMotor = 0;
            _lastSmallMotor = 0;
        }

        if (!_readyFired)
        {
            _readyFired = true;
            OnReady?.Invoke(this);
        }
    }

    private static string ProfileIdForMode(ControllerMode mode) => mode switch
    {
        ControllerMode.XboxOneS => "xbox-one-s",
        ControllerMode.SwitchPro => "switch-pro",
        _ => "dualshock-4-v1-full",
    };

    private HMController CreateFreshController(ControllerMode mode)
    {
        var profileId = ProfileIdForMode(mode);
        var profile = _hmContext.GetProfile(profileId)
            ?? _hmContext.AllProfiles.FirstOrDefault(p =>
                p.Id.StartsWith("dualshock-4", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No HIDMaestro profile found for mode {mode}");

        return _hmContext.CreateController(profile);
    }

    private void RegisterOutputHandlers(HMController ctrl)
    {
        ctrl.OutputDecoded += OnHmOutputDecoded;
        ctrl.OutputReceived += OnHmOutputReceived;
    }

    private void UnregisterOutputHandlers(HMController ctrl)
    {
        ctrl.OutputDecoded -= OnHmOutputDecoded;
        ctrl.OutputReceived -= OnHmOutputReceived;
    }

    private void OnHmOutputDecoded(object? sender, HMOutputDecodedEventArgs e)
    {
        foreach (var (k, v) in e.Fields)
        {
            var key = k.ToLowerInvariant();
            if (key is "leftmotor" or "largemotor" or "leftactuator" or "leftlinear")
                _lastLargeMotor = Convert.ToByte(v);
            else if (key is "rightmotor" or "smallmotor" or "rightactuator" or "rightlinear")
                _lastSmallMotor = Convert.ToByte(v);
        }
    }

    private void OnHmOutputReceived(HMController controller, HMOutputPacket packet)
    {
        var d = packet.Data.Span;
        if (d.Length < 4) return;

        _lastLargeMotor = d[2];
        _lastSmallMotor = d[3];
    }

    private void HandleGamepadInput(GamepadInput input)
    {
        if (Controller == null) return;

        _lastSeq = input.Seq;
        var now = DateTime.UtcNow;
        if ((now - _lastInputTime).TotalSeconds < 1)
            _inputCountThisSecond++;
        else
        {
            _measuredInputRate = _inputCountThisSecond;
            _inputCountThisSecond = 1;
        }
        _lastInputTime = now;

        lock (_inputLock)
        {
            _latestInput = input;
        }
    }

    private static HMButton MapButtons(uint buttons)
    {
        HMButton b = 0;
        if ((buttons & (1 << 0)) != 0) b |= HMButton.A;
        if ((buttons & (1 << 1)) != 0) b |= HMButton.B;
        if ((buttons & (1 << 2)) != 0) b |= HMButton.X;
        if ((buttons & (1 << 3)) != 0) b |= HMButton.Y;
        if ((buttons & (1 << 4)) != 0) b |= HMButton.LeftBumper;
        if ((buttons & (1 << 5)) != 0) b |= HMButton.RightBumper;
        if ((buttons & (1 << 8)) != 0) b |= HMButton.Back;
        if ((buttons & (1 << 9)) != 0) b |= HMButton.Start;
        if ((buttons & (1 << 10)) != 0) b |= HMButton.LeftStick;
        if ((buttons & (1 << 11)) != 0) b |= HMButton.RightStick;
        if ((buttons & (1 << 16)) != 0) b |= HMButton.Guide;
        return b;
    }

    private static HMHat MapDpad(uint dpad)
    {
        return (dpad & 0x0F) switch
        {
            1 => HMHat.North,
            2 => HMHat.South,
            4 => HMHat.West,
            8 => HMHat.East,
            5 => HMHat.NorthWest,
            9 => HMHat.NorthEast,
            6 => HMHat.SouthWest,
            10 => HMHat.SouthEast,
            _ => HMHat.None,
        };
    }

    private void SubmitXboxReport(GamepadInput input)
    {
        var ctrl = Controller!;

        var lt = input.LeftTrigger > 0 ? input.LeftTrigger : ((input.Buttons & (1 << 6)) != 0 ? 255u : 0u);
        var rt = input.RightTrigger > 0 ? input.RightTrigger : ((input.Buttons & (1 << 7)) != 0 ? 255u : 0u);

        ctrl.SubmitState(new HMGamepadState
        {
            Axes = HMGamepadStateHelpers.StandardAxes(ctrl.Profile,
                leftStickX:   (input.LeftStickX + 32768f) / 65535f,
                leftStickY:   (input.LeftStickY + 32768f) / 65535f,
                rightStickX:  (input.RightStickX + 32768f) / 65535f,
                rightStickY:  (input.RightStickY + 32768f) / 65535f,
                leftTrigger:  lt / 255f,
                rightTrigger: rt / 255f),
            Buttons = MapButtons(input.Buttons),
            Hat = MapDpad(input.Dpad),
        });
    }

    private int _switchProTimer;
    private int _switchProRawDiagStep = -1;

    private void SubmitSwitchProReport(GamepadInput input)
    {
        var ctrl = Controller!;

        // If the profile doesn't exist and fell back to DS4, use DS4 raw format.
        if (ctrl.Profile.Id.StartsWith("dualshock-4", StringComparison.OrdinalIgnoreCase))
        {
            SubmitDs4Report(input);
            return;
        }

        // Diagnostic mode — cycles byte patterns for report format discovery
        if (_switchProRawDiagStep >= 0)
        {
            SubmitSwitchProDiagRaw(ctrl);
            return;
        }

        var buf = new byte[63];

        // Standard Switch Pro input report format (per dekuNukem RE, report 0x30)
        // The 63-byte buffer matches the real controller's output size.

        // Byte 0: Timer (increments each report, keeps connection alive)
        buf[0] = (byte)(_switchProTimer++);

        // Byte 1: Battery (high nibble) | Connection info (low nibble)
        byte bat = (byte)Math.Clamp(input.BatteryLevel / 25u, 0u, 4u);
        buf[1] = (byte)(bat << 4 | 0x01); // high=battery(0-4), low=USB connected

        // Byte 2: Buttons 0 (Right side)
        // bits[0]=X, [1]=Y, [2]=A, [3]=B, [6]=R, [7]=ZR
        byte b2 = 0;
        if ((input.Buttons & (1 << 2)) != 0) b2 |= 1 << 0; // X
        if ((input.Buttons & (1 << 3)) != 0) b2 |= 1 << 1; // Y
        if ((input.Buttons & (1 << 0)) != 0) b2 |= 1 << 2; // A
        if ((input.Buttons & (1 << 1)) != 0) b2 |= 1 << 3; // B
        if ((input.Buttons & (1 << 5)) != 0) b2 |= 1 << 6; // R
        if ((input.Buttons & (1 << 7)) != 0) b2 |= 1 << 7; // ZR
        buf[2] = b2;

        // Byte 3: Buttons 1 (Shared)
        // bits[0]=-, [1]=+, [2]=RStick, [3]=LStick, [4]=Home, [5]=Capture
        byte b3 = 0;
        if ((input.Buttons & (1 << 8)) != 0)  b3 |= 1 << 0; // - (Select)
        if ((input.Buttons & (1 << 9)) != 0)  b3 |= 1 << 1; // + (Start)
        if ((input.Buttons & (1 << 11)) != 0) b3 |= 1 << 2; // RStick (R3)
        if ((input.Buttons & (1 << 10)) != 0) b3 |= 1 << 3; // LStick (L3)
        if ((input.Buttons & (1 << 16)) != 0) b3 |= 1 << 4; // Home
        if ((input.Buttons & (1 << 17)) != 0) b3 |= 1 << 5; // Capture
        buf[3] = b3;

        // Byte 4: Buttons 2 (Left + Dpad)
        // bits[0]=Ddown, [1]=Dup, [2]=Dright, [3]=Dleft, [6]=L, [7]=ZL
        byte b4 = 0;
        if ((input.Dpad & 2) != 0) b4 |= 1 << 0; // Dpad-Down
        if ((input.Dpad & 1) != 0) b4 |= 1 << 1; // Dpad-Up
        if ((input.Dpad & 8) != 0) b4 |= 1 << 2; // Dpad-Right
        if ((input.Dpad & 4) != 0) b4 |= 1 << 3; // Dpad-Left
        if ((input.Buttons & (1 << 4)) != 0) b4 |= 1 << 6; // L
        if ((input.Buttons & (1 << 6)) != 0) b4 |= 1 << 7; // ZL
        buf[4] = b4;

        // Byte 5-7: Left stick (12-bit X, 12-bit Y packed)
        int lx = (int)((input.LeftStickX + 32768f) / 65535f * 4095f);
        int ly = (int)((input.LeftStickY + 32768f) / 65535f * 4095f);
        ly = 4095 - ly; // invert Y
        buf[5] = (byte)(lx & 0xFF);
        buf[6] = (byte)(((lx >> 8) & 0x0F) | ((ly & 0x0F) << 4));
        buf[7] = (byte)((ly >> 4) & 0xFF);

        // Byte 8-10: Right stick (12-bit X, 12-bit Y packed)
        int rx = (int)((input.RightStickX + 32768f) / 65535f * 4095f);
        int ry = (int)((input.RightStickY + 32768f) / 65535f * 4095f);
        ry = 4095 - ry; // invert Y
        buf[8] = (byte)(rx & 0xFF);
        buf[9] = (byte)(((rx >> 8) & 0x0F) | ((ry & 0x0F) << 4));
        buf[10] = (byte)((ry >> 4) & 0xFF);

        // Byte 11: Vibrator (unused)
        buf[11] = 0;

        // Bytes 12-47: IMU data (3 frames × 12 bytes = 36 bytes)
        // Each frame: Accel X,Y,Z (int16 LE) + Gyro X,Y,Z (int16 LE)
        // Axes: input X→profile Y, input Y→profile Z, input Z→profile X
        // Accel scale: 0.5g per 16384 (16-bit ±8g), /2 vs old 835f to fix 1g→2g
        int off = 12;
        for (int frame = 0; frame < 3; frame++)
        {
            WriteInt16LE(buf, off + 0, (short)Math.Clamp(input.AccelZ * -418f, short.MinValue, short.MaxValue));
            WriteInt16LE(buf, off + 2, (short)Math.Clamp(input.AccelX * -418f, short.MinValue, short.MaxValue));
            WriteInt16LE(buf, off + 4, (short)Math.Clamp(input.AccelY * 418f, short.MinValue, short.MaxValue));
            WriteInt16LE(buf, off + 6, (short)Math.Clamp(input.GyroZ * -940f, short.MinValue, short.MaxValue));
            WriteInt16LE(buf, off + 8, (short)Math.Clamp(input.GyroX * -940f, short.MinValue, short.MaxValue));
            WriteInt16LE(buf, off +10, (short)Math.Clamp(input.GyroY * 940f, short.MinValue, short.MaxValue));
            off += 12;
        }

        // Byte 48-62: Remaining padding (already zeroed)

        ctrl.SubmitRawReport(buf.AsSpan());
    }

    private static readonly (string Label, Action<byte[], int> Setter)[] _diagSteps =
    [
        ("Zero",            (b, _) => Array.Clear(b, 0, b.Length)),
        ("LStickX",         (b, _) => b[0] = 0xFF),
        ("LStickY",         (b, _) => b[1] = 0xFF),
        ("RStickX+BTN",     (b, _) => b[2] = 0xFF),
        ("RStickY+BTN",     (b, _) => b[3] = 0xFF),
        ("L+Dpad+Bat",      (b, _) => b[4] = 0xFF),
        ("ZL+Bat",          (b, _) => b[5] = 0xFF),
        ("Byte6=all",       (b, _) => b[6] = 0xFF),
        ("Byte7=all",       (b, _) => b[7] = 0xFF),
        ("Byte8=all",       (b, _) => b[8] = 0xFF),
        ("Byte9=all",       (b, _) => b[9] = 0xFF),
        ("Byte10=all",      (b, _) => b[10] = 0xFF),
        ("Byte11=all",      (b, _) => b[11] = 0xFF),
        ("Byte12=all",      (b, _) => b[12] = 0xFF),
        ("Byte13=all",      (b, _) => b[13] = 0xFF),
        ("Byte14=all",      (b, _) => b[14] = 0xFF),
        ("Byte15=all",      (b, _) => b[15] = 0xFF),
        ("Byte16=all",      (b, _) => b[16] = 0xFF),
        ("Byte17=all",      (b, _) => b[17] = 0xFF),
        ("Byte18=all",      (b, _) => b[18] = 0xFF),
        ("Byte19=all",      (b, _) => b[19] = 0xFF),
        ("Byte20=all",      (b, _) => b[20] = 0xFF),
        ("Byte21=all",      (b, _) => b[21] = 0xFF),
        ("Byte22=all",      (b, _) => b[22] = 0xFF),
        ("Byte23=all",      (b, _) => b[23] = 0xFF),
        ("All0xFF",         (b, _) => Array.Fill<byte>(b, 0xFF)),
        ("All0x00",         (b, _) => Array.Clear(b, 0, b.Length)),
    ];

    private void SubmitSwitchProDiagRaw(HMController ctrl)
    {
        var idx = _switchProRawDiagStep;
        if (idx < 0 || idx >= _diagSteps.Length)
        {
            _switchProRawDiagStep = -1;
            return;
        }

        var buf = new byte[63];
        _diagSteps[idx].Setter(buf, idx);
        ctrl.SubmitRawReport(buf.AsSpan());
    }

    public string SwitchProDiagStepName
    {
        get
        {
            var i = _switchProRawDiagStep;
            if (i < 0 || i >= _diagSteps.Length) return "";
            return _diagSteps[i].Label;
        }
    }

    /// <summary>Advance to the next diagnostic pattern (call from UI).</summary>
    public void SwitchProDiagNext()
    {
        var next = _switchProRawDiagStep + 1;
        if (next >= _diagSteps.Length)
            next = -1; // off
        _switchProRawDiagStep = next;
    }

    /// <summary>Start / restart diagnostic from step 0.</summary>
    public void SwitchProDiagStart()
    {
        _switchProRawDiagStep = 0;
    }

    private void SubmitDs4Report(GamepadInput input)
    {
        var ctrl = Controller!;
        var buf = new byte[63];

        buf[0] = (byte)((input.LeftStickX + 32768) / 256);
        buf[1] = (byte)((input.LeftStickY + 32768) / 256);
        buf[2] = (byte)((input.RightStickX + 32768) / 256);
        buf[3] = (byte)((input.RightStickY + 32768) / 256);

        ushort w = (input.Dpad & 0x0F) switch
        {
            1 => 0,
            2 => 4,
            4 => 6,
            8 => 2,
            5 => 7,
            6 => 5,
            9 => 1,
            10 => 3,
            _ => 8,
        };
        if ((input.Buttons & (1 << 0)) != 0) w |= 1 << 5;
        if ((input.Buttons & (1 << 1)) != 0) w |= 1 << 6;
        if ((input.Buttons & (1 << 2)) != 0) w |= 1 << 4;
        if ((input.Buttons & (1 << 3)) != 0) w |= 1 << 7;
        if ((input.Buttons & (1 << 4)) != 0) w |= 1 << 8;
        if ((input.Buttons & (1 << 5)) != 0) w |= 1 << 9;
        if ((input.Buttons & (1 << 6)) != 0) w |= 1 << 10;
        if ((input.Buttons & (1 << 7)) != 0) w |= 1 << 11;
        if ((input.Buttons & (1 << 8)) != 0) w |= 1 << 12;
        if ((input.Buttons & (1 << 9)) != 0) w |= 1 << 13;
        if ((input.Buttons & (1 << 10)) != 0) w |= 1 << 14;
        if ((input.Buttons & (1 << 11)) != 0) w |= 1 << 15;
        buf[4] = (byte)w;
        buf[5] = (byte)(w >> 8);

        byte special = 0;
        if ((input.Buttons & (1 << 16)) != 0) special |= 1;
        if ((input.Buttons & (1 << 17)) != 0) special |= 2;
        buf[6] = special;

        var lt = input.LeftTrigger > 0 ? input.LeftTrigger : ((input.Buttons & (1 << 6)) != 0 ? 255u : 0u);
        var rt = input.RightTrigger > 0 ? input.RightTrigger : ((input.Buttons & (1 << 7)) != 0 ? 255u : 0u);
        buf[7] = (byte)Math.Clamp(lt, 0, 255);
        buf[8] = (byte)Math.Clamp(rt, 0, 255);

        buf[11] = (byte)Math.Clamp(input.BatteryLevel / 10u, 0u, 10u);

        WriteInt16LE(buf, 12, (short)Math.Clamp(input.GyroX * 940f, short.MinValue, short.MaxValue));
        WriteInt16LE(buf, 14, (short)Math.Clamp(input.GyroY * 940f, short.MinValue, short.MaxValue));
        WriteInt16LE(buf, 16, (short)Math.Clamp(input.GyroZ * 940f, short.MinValue, short.MaxValue));

        WriteInt16LE(buf, 18, (short)Math.Clamp(input.AccelX * 835f, short.MinValue, short.MaxValue));
        WriteInt16LE(buf, 20, (short)Math.Clamp(input.AccelY * 835f, short.MinValue, short.MaxValue));
        WriteInt16LE(buf, 22, (short)Math.Clamp(input.AccelZ * 835f, short.MinValue, short.MaxValue));

        var powerState = (byte)Math.Clamp(input.BatteryLevel / 10u, 0u, 10u);
        if (input.IsCharging) powerState |= 0x10;
        buf[29] = powerState;

        var t0 = input.Touches.Count > 0 ? input.Touches[0] : null;
        var t1 = input.Touches.Count > 1 ? input.Touches[1] : null;

        for (int i = 0; i < 2; i++)
        {
            var t = i == 0 ? t0 : t1;
            bool isActive = t?.Active ?? false;
            if (isActive && !_prevTouchActive[i])
                _touchIds[i] = _nextTrackingId++;
            _prevTouchActive[i] = isActive;
        }

        byte currentCounter = (byte)(_ds4TouchCounter & 0x7F);
        buf[32] = 1;
        buf[33] = currentCounter;
        WriteTouchData(buf, 34,
            (int)(t0?.X ?? 0), (int)(t0?.Y ?? 0),
            t0?.Active ?? false, _touchIds[0]);
        WriteTouchData(buf, 38,
            (int)(t1?.X ?? 0), (int)(t1?.Y ?? 0),
            t1?.Active ?? false, _touchIds[1]);

        buf[42] = _previousTouchCounter;
        byte tid2 = (byte)((_nextTrackingId + 2) & 0x7F);
        WriteTouchData(buf, 43, 0, 0, false, tid2);
        byte tid3 = (byte)((_nextTrackingId + 3) & 0x7F);
        WriteTouchData(buf, 47, 0, 0, false, tid3);

        buf[51] = _previousTouchCounter;
        byte tid4 = (byte)((_nextTrackingId + 4) & 0x7F);
        WriteTouchData(buf, 52, 0, 0, false, tid4);
        byte tid5 = (byte)((_nextTrackingId + 5) & 0x7F);
        WriteTouchData(buf, 56, 0, 0, false, tid5);

        _previousTouchCounter = currentCounter;
        _ds4TouchCounter++;

        ctrl.SubmitRawReport(buf.AsSpan());
    }

    private static void WriteInt16LE(byte[] buf, int offset, short value)
    {
        buf[offset] = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteUInt16LE(byte[] buf, int offset, ushort value)
    {
        buf[offset] = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteTouchData(byte[] buf, int offset, int x, int y, bool active, byte id)
    {
        buf[offset] = active ? id : (byte)(0x80 | id);
        int cx = Math.Clamp(x, 0, 1919);
        int cy = Math.Clamp(y, 0, 1079);
        buf[offset + 1] = (byte)(cx & 0xFF);
        buf[offset + 2] = (byte)(((cx >> 8) & 0x0F) | ((cy & 0x0F) << 4));
        buf[offset + 3] = (byte)((cy >> 4) & 0xFF);
    }

    private async Task SendVibrationAsync(byte large, byte small)
    {
        await SendAsync(new ServerToClient
        {
            Vibration = new Vibration
            {
                LargeMotor = large,
                SmallMotor = small
            }
        });
    }

    private async Task ReportLoop(CancellationToken ct)
    {
        var intervalTicks = (long)(TimeSpan.TicksPerSecond / 125.0);
        var sw = new System.Diagnostics.Stopwatch();
        sw.Start();
        long nextTick = intervalTicks;

        while (!ct.IsCancellationRequested)
        {
            if (_disposed) return;

            // Submit latest input (batched, one report per tick)
            GamepadInput? input;
            lock (_inputLock)
            {
                input = _latestInput;
            }

            // Always submit a report every tick to keep the controller alive.
            // Use the last known state when no new input has arrived.
            var report = input ?? _lastReportedInput;

            if (report != null && Controller != null)
            {
                try
                {
                    if (Mode == ControllerMode.Ds4)
                        SubmitDs4Report(report);
                    else if (Mode == ControllerMode.SwitchPro)
                        SubmitSwitchProReport(report);
                    else
                        SubmitXboxReport(report);
                }
                catch { }

                if (input != null)
                    _lastReportedInput = input;
            }

            // Send vibration/RTT back to phone
            if (_lastSeq > 0)
            {
                try
                {
                    await SendVibrationAsync(_lastLargeMotor, _lastSmallMotor);
                    await SendAsync(new ServerToClient
                    {
                        RttReport = new RttReport
                        {
                            AckSeq = _lastSeq,
                            MeasuredUplinkRateHz = (uint)_measuredInputRate,
                            RecommendedUplinkIntervalUs = 8000,
                        }
                    });
                }
                catch { }
            }
            else if (_lastLargeMotor > 0 || _lastSmallMotor > 0)
            {
                try
                {
                    await SendVibrationAsync(_lastLargeMotor, _lastSmallMotor);
                }
                catch { }
            }

            // Drift-compensated wait for next 125Hz tick
            var remainingTicks = nextTick - sw.Elapsed.Ticks;
            if (remainingTicks > 0)
            {
                var waitMs = (int)(remainingTicks / TimeSpan.TicksPerMillisecond);
                if (waitMs > 1)
                {
                    await Task.Delay(waitMs - 1, ct);
                    while (sw.Elapsed.Ticks < nextTick && !ct.IsCancellationRequested)
                        Thread.SpinWait(1);
                }
            }
            nextTick += intervalTicks;

            // If fallen behind, reset schedule
            if (sw.Elapsed.Ticks > nextTick + intervalTicks)
                nextTick = sw.Elapsed.Ticks + intervalTicks;
        }
    }

    public async Task SetControllerModeAsync(ControllerMode mode)
    {
        if (_disposed || _switchingMode) return;
        _switchingMode = true;
        try
        {
            _lastLargeMotor = 0;
            _lastSmallMotor = 0;
            _lastReportedInput = null;
            _switchProTimer = 0;

            // Destroy old controller first so the new one gets a clean PnP slot
            var oldCtrl = Controller;
            Controller = null;
            if (oldCtrl != null)
                await Task.Run(() => { try { oldCtrl.Dispose(); } catch { } });

            // Create new controller with the target mode
            var newCtrl = await Task.Run(() =>
            {
                var c = CreateFreshController(mode);
                RegisterOutputHandlers(c);
                return c;
            });

            Controller = newCtrl;
            Mode = mode;
            _lastLargeMotor = 0;
            _lastSmallMotor = 0;

            // Ensure OnReady fires — the initial Hello may have been dropped
            // because _switchingMode was true when it arrived.
            if (!_readyFired)
            {
                _readyFired = true;
                OnReady?.Invoke(this);
            }
        }
        finally
        {
            _switchingMode = false;
        }
    }

    public void Disconnect()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        var oldCtrl = Controller;
        Controller = null;
        if (oldCtrl != null)
            _ = Task.Run(() => { try { oldCtrl.Dispose(); } catch { } });
        OnDisconnected?.Invoke(this);
    }

    private void ResetController()
    {
        try
        {
            if (Controller == null) return;
            Controller.SubmitState(new HMGamepadState
            {
                Axes = HMGamepadStateHelpers.StandardAxes(Controller.Profile),
                Buttons = HMButton.None,
                Hat = HMHat.None,
            });
            ClearLatestInput();
        }
        catch { }
    }

    private void ClearLatestInput()
    {
        lock (_inputLock)
        {
            _latestInput = null;
            _lastReportedInput = null;
        }
    }

    public void Dispose()
    {
        Disconnect();
        _cts.Dispose();
    }
}
