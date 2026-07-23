using GamepadEmu.Protocol;
using Google.Protobuf;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using Nefarius.ViGEm.Client.Targets.DualShock4;

namespace GamepadEmuHost.Core;

public class GamepadSession : IDisposable
{
    private const int ConnectionTimeoutMs = 3000;

    private readonly Func<IMessage, Task> _sendToPhone;
    private readonly ViGEmClient _viGEm;
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

    public DiscoveredDevice Device { get; }
    public ControllerMode Mode { get; private set; }
    public object? Controller { get; private set; }
    public bool IsConnected => !_disposed;
    public bool IsReady => Controller != null;
    public bool HasBeenReady => _readyFired;

    public event Action<GamepadSession>? OnReady;
    public event Action<GamepadSession>? OnConnectionLost;
    public event Action<GamepadSession>? OnDisconnected;

    public GamepadSession(DiscoveredDevice device, Func<IMessage, Task> sendToPhone, ViGEmClient viGEm)
    {
        Device = device;
        _sendToPhone = sendToPhone;
        _viGEm = viGEm;
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
        if (!IsReady) return;
        _lastReceiveTime = DateTime.UtcNow;
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
        Mode = ControllerMode.Ds4;
        if (Controller == null)
            CreateController(Mode);

        _readyFired = true;
        OnReady?.Invoke(this);
    }

    private void CreateController(ControllerMode mode)
    {
        switch (mode)
        {
            case ControllerMode.Xbox360:
            {
                var ctrl = _viGEm.CreateXbox360Controller();
                ctrl.FeedbackReceived += OnXbox360Feedback;
                ctrl.AutoSubmitReport = false;
                ctrl.Connect();
                ctrl.SetAxisValue(Xbox360Axis.LeftThumbX, 0);
                ctrl.SetAxisValue(Xbox360Axis.LeftThumbY, 0);
                ctrl.SetAxisValue(Xbox360Axis.RightThumbX, 0);
                ctrl.SetAxisValue(Xbox360Axis.RightThumbY, 0);
                ctrl.SetSliderValue(Xbox360Slider.LeftTrigger, 0);
                ctrl.SetSliderValue(Xbox360Slider.RightTrigger, 0);
                ctrl.SubmitReport();
                Controller = ctrl;
                break;
            }
            default:
            {
                var ctrl = _viGEm.CreateDualShock4Controller();
                ctrl.FeedbackReceived += OnDs4Feedback;
                ctrl.Connect();
                Controller = ctrl;
                break;
            }
        }
    }

    private void OnDs4Feedback(object? sender, DualShock4FeedbackReceivedEventArgs e)
    {
        _lastLargeMotor = e.LargeMotor;
        _lastSmallMotor = e.SmallMotor;
    }

    private void OnXbox360Feedback(object? sender, Xbox360FeedbackReceivedEventArgs e)
    {
        _lastLargeMotor = e.LargeMotor;
        _lastSmallMotor = e.SmallMotor;
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

    private void SubmitXbox360Report(GamepadInput input)
    {
        var ctrl = (IXbox360Controller)Controller!;

        ctrl.SetButtonState(Xbox360Button.A, (input.Buttons & (1 << 0)) != 0);
        ctrl.SetButtonState(Xbox360Button.B, (input.Buttons & (1 << 1)) != 0);
        ctrl.SetButtonState(Xbox360Button.X, (input.Buttons & (1 << 2)) != 0);
        ctrl.SetButtonState(Xbox360Button.Y, (input.Buttons & (1 << 3)) != 0);
        ctrl.SetButtonState(Xbox360Button.LeftShoulder, (input.Buttons & (1 << 4)) != 0);
        ctrl.SetButtonState(Xbox360Button.RightShoulder, (input.Buttons & (1 << 5)) != 0);
        ctrl.SetButtonState(Xbox360Button.LeftThumb, (input.Buttons & (1 << 10)) != 0);
        ctrl.SetButtonState(Xbox360Button.RightThumb, (input.Buttons & (1 << 11)) != 0);
        ctrl.SetButtonState(Xbox360Button.Back, (input.Buttons & (1 << 8)) != 0);
        ctrl.SetButtonState(Xbox360Button.Start, (input.Buttons & (1 << 9)) != 0);
        ctrl.SetButtonState(Xbox360Button.Guide, (input.Buttons & (1 << 16)) != 0);

        ctrl.SetButtonState(Xbox360Button.Up, (input.Dpad & 1) != 0);
        ctrl.SetButtonState(Xbox360Button.Down, (input.Dpad & 2) != 0);
        ctrl.SetButtonState(Xbox360Button.Left, (input.Dpad & 4) != 0);
        ctrl.SetButtonState(Xbox360Button.Right, (input.Dpad & 8) != 0);

        ctrl.SetAxisValue(Xbox360Axis.LeftThumbX, (short)input.LeftStickX);
        ctrl.SetAxisValue(Xbox360Axis.LeftThumbY, (short)-input.LeftStickY);
        ctrl.SetAxisValue(Xbox360Axis.RightThumbX, (short)input.RightStickX);
        ctrl.SetAxisValue(Xbox360Axis.RightThumbY, (short)-input.RightStickY);

        var lt = input.LeftTrigger > 0 ? input.LeftTrigger : ((input.Buttons & (1 << 6)) != 0 ? 255u : 0u);
        var rt = input.RightTrigger > 0 ? input.RightTrigger : ((input.Buttons & (1 << 7)) != 0 ? 255u : 0u);
        ctrl.SetSliderValue(Xbox360Slider.LeftTrigger, (byte)Math.Clamp(lt, 0, 255));
        ctrl.SetSliderValue(Xbox360Slider.RightTrigger, (byte)Math.Clamp(rt, 0, 255));

        ctrl.SubmitReport();
    }

    private ushort _ds4Timestamp;
    private int _ds4TouchCounter;
    private byte _previousTouchCounter;
    private readonly byte[] _touchIds = new byte[2];
    private readonly bool[] _prevTouchActive = new bool[2];
    private byte _nextTrackingId;

    private void SubmitDs4Report(GamepadInput input)
    {
        var ctrl = (IDualShock4Controller)Controller!;
        var buf = new byte[63];

        buf[0] = MapAxisToByte(input.LeftStickX);
        buf[1] = MapAxisToByte(input.LeftStickY);
        buf[2] = MapAxisToByte(input.RightStickX);
        buf[3] = MapAxisToByte(input.RightStickY);

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

        _ds4Timestamp++;
        buf[9] = (byte)_ds4Timestamp;
        buf[10] = (byte)(_ds4Timestamp >> 8);

        buf[11] = (byte)Math.Clamp(input.BatteryLevel / 10u, 0u, 10u);

        WriteInt16LE(buf, 12, (short)Math.Clamp(input.GyroX * 940f, short.MinValue, short.MaxValue));
        WriteInt16LE(buf, 14, (short)Math.Clamp(input.GyroY * 940f, short.MinValue, short.MaxValue));
        WriteInt16LE(buf, 16, (short)Math.Clamp(input.GyroZ * 940f, short.MinValue, short.MaxValue));

        WriteInt16LE(buf, 18, (short)Math.Clamp(input.AccelX * 835f, short.MinValue, short.MaxValue));
        WriteInt16LE(buf, 20, (short)Math.Clamp(input.AccelY * 835f, short.MinValue, short.MaxValue));
        WriteInt16LE(buf, 22, (short)Math.Clamp(input.AccelZ * 835f, short.MinValue, short.MaxValue));

        buf[24] = 0xE9;

        var powerState = (byte)Math.Clamp(input.BatteryLevel / 10u, 0u, 10u);
        if (input.IsCharging) powerState |= 0x10;
        buf[29] = powerState;

        buf[30] = 0x1B;

        buf[32] = 1;

        // Use phone's slot-indexed touches directly; phone always sends 2 slots
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

        ctrl.SubmitRawReport(buf);
    }

    private static void WriteInt16LE(byte[] buf, int offset, short value)
    {
        buf[offset] = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteTouchData(byte[] buf, int offset, int x, int y, bool active, byte tracking)
    {
        buf[offset] = active ? (byte)(tracking & 0x7F) : (byte)(0x80 | (tracking & 0x7F));
        int cx = Math.Clamp(x, 0, 1919);
        int cy = Math.Clamp(y, 0, 942);
        buf[offset + 1] = (byte)(cx & 0xFF);
        buf[offset + 2] = (byte)(((cx >> 8) & 0x0F) | ((cy & 0x0F) << 4));
        buf[offset + 3] = (byte)((cy >> 4) & 0xFF);
    }

    private static byte MapAxisToByte(int value)
    {
        return (byte)Math.Clamp((value + 32768) / 256, 0, 255);
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

            // Submit latest input to ViGEm (batched, one report per tick)
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

    public Task SetControllerModeAsync(ControllerMode mode)
    {
        if (_disposed) return Task.CompletedTask;

        Mode = mode;
        DestroyController();
        CreateController(mode);
        return Task.CompletedTask;
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
            switch (Controller)
            {
                case IXbox360Controller xbox:
                    xbox.SetButtonState(Xbox360Button.A, false);
                    xbox.SetButtonState(Xbox360Button.B, false);
                    xbox.SetButtonState(Xbox360Button.X, false);
                    xbox.SetButtonState(Xbox360Button.Y, false);
                    xbox.SetButtonState(Xbox360Button.LeftShoulder, false);
                    xbox.SetButtonState(Xbox360Button.RightShoulder, false);
                    xbox.SetButtonState(Xbox360Button.LeftThumb, false);
                    xbox.SetButtonState(Xbox360Button.RightThumb, false);
                    xbox.SetButtonState(Xbox360Button.Back, false);
                    xbox.SetButtonState(Xbox360Button.Start, false);
                    xbox.SetButtonState(Xbox360Button.Guide, false);
                    xbox.SetButtonState(Xbox360Button.Up, false);
                    xbox.SetButtonState(Xbox360Button.Down, false);
                    xbox.SetButtonState(Xbox360Button.Left, false);
                    xbox.SetButtonState(Xbox360Button.Right, false);
                    xbox.SetAxisValue(Xbox360Axis.LeftThumbX, 0);
                    xbox.SetAxisValue(Xbox360Axis.LeftThumbY, 0);
                    xbox.SetAxisValue(Xbox360Axis.RightThumbX, 0);
                    xbox.SetAxisValue(Xbox360Axis.RightThumbY, 0);
                    xbox.SetSliderValue(Xbox360Slider.LeftTrigger, 0);
                    xbox.SetSliderValue(Xbox360Slider.RightTrigger, 0);
                    xbox.SubmitReport();
                    ClearLatestInput();
                    break;
                case IDualShock4Controller ds4:
                    var buf = new byte[63];
                    buf[0] = 128; buf[1] = 128;
                    buf[2] = 128; buf[3] = 128;
                    buf[4] = 8;
                    buf[11] = 10;
                    buf[24] = 0xE9;
                    buf[29] = 0x10 | 10;
                    buf[30] = 0x1B;
                    buf[34] = 0x80;
                    buf[38] = 0x80;
                    buf[43] = 0x80;
                    buf[47] = 0x80;
                    buf[52] = 0x80;
                    buf[56] = 0x80;
                    ds4.SubmitRawReport(buf);
                    ClearLatestInput();
                    break;
            }
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
            switch (Controller)
            {
                case IXbox360Controller xbox:
                    xbox.Disconnect();
                    break;
                case IDualShock4Controller ds4:
                    ds4.Disconnect();
                    break;
            }
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
