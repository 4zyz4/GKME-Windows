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
        if (_switchingMode || _disposed) return;

        if (Controller == null)
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
        ControllerMode.Xbox360Wired => "xbox-360-wired",
        ControllerMode.XboxOneS => "xbox-one-s",
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
            if (key is "leftmotor" or "largemotor" or "leftactuator")
                _lastLargeMotor = Convert.ToByte(v);
            else if (key is "rightmotor" or "smallmotor" or "rightactuator")
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

    private void SubmitXbox360Report(GamepadInput input)
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

            if (input != null && Controller != null)
            {
                try
                {
                    if (Mode == ControllerMode.Ds4)
                        SubmitDs4Report(input);
                    else
                        SubmitXbox360Report(input);
                }
                catch { }
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

            // Create new controller BEFORE destroying old one
            // so phone keeps working during the 20s PnP creation
            var oldCtrl = Controller;
            var newCtrl = await Task.Run(() =>
            {
                var c = CreateFreshController(mode);
                RegisterOutputHandlers(c);
                return c;
            });

            // Swap: new controller takes over, old one is cleaned up
            oldCtrl?.Dispose();
            Controller = newCtrl;
            Mode = mode;
            _lastLargeMotor = 0;
            _lastSmallMotor = 0;
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
        DestroyController();
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
        }
    }

    private void DestroyController()
    {
        try
        {
            Controller?.Dispose();
        }
        catch { }
        Controller = null;
    }

    public void Dispose()
    {
        Disconnect();
        _cts.Dispose();
    }
}
