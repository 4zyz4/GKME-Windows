using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using GamepadEmu.Protocol;
using Google.Protobuf;
using HIDMaestro;
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
    private readonly HMContext _hmContext;
    private CancellationTokenSource _cts = new();
    private Task _reportTask = Task.CompletedTask;
    private volatile bool _disposed;
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
    private Task? _cleanupTask;

    public DiscoveredDevice Device { get; }
    public ControllerMode Mode { get; private set; }
    public object? Controller { get; private set; }
    public bool IsConnected => !_disposed;
    public bool IsReady => Controller != null;
    public bool HasBeenReady => _readyFired;

    public event Action<GamepadSession>? OnReady;
    public event Action<GamepadSession>? OnConnectionLost;
    public event Action<GamepadSession>? OnDisconnected;

    public GamepadSession(DiscoveredDevice device, Func<IMessage, Task> sendToPhone, ViGEmClient viGEm, HMContext hmContext)
    {
        Device = device;
        _sendToPhone = sendToPhone;
        _viGEm = viGEm;
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
            CreateController(ControllerMode.Ds4);
        }

        if (!_readyFired)
        {
            _readyFired = true;
            OnReady?.Invoke(this);
        }
    }

    private void CreateController(ControllerMode mode)
    {
        Mode = mode;

        switch (mode)
        {
            case ControllerMode.SwitchPro:
            {
                var profileId = "switch-pro";
                var profile = _hmContext.GetProfile(profileId)
                    ?? _hmContext.AllProfiles.FirstOrDefault(p =>
                        p.Id.StartsWith("dualshock-4", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"No HIDMaestro profile found for mode {mode}");

                var ctrl = _hmContext.CreateController(profile);
                ctrl.OutputDecoded += OnHmOutputDecoded;
                ctrl.OutputReceived += OnHmOutputReceived;
                Controller = ctrl;
                break;
            }
            case ControllerMode.Xbox360:
            {
                var ctrl = _viGEm.CreateXbox360Controller();
                ctrl.FeedbackReceived += OnXbox360Feedback;
                ctrl.AutoSubmitReport = false;
                ctrl.Connect();
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

    private void SubmitSwitchProReport(GamepadInput input)
    {
        var ctrl = (HMController)Controller!;

        var buf = new byte[48];

        byte b2 = 0;
        if ((input.Buttons & (1 << 2)) != 0) b2 |= 1 << 0;
        if ((input.Buttons & (1 << 3)) != 0) b2 |= 1 << 1;
        if ((input.Buttons & (1 << 0)) != 0) b2 |= 1 << 2;
        if ((input.Buttons & (1 << 1)) != 0) b2 |= 1 << 3;
        if ((input.Buttons & (1 << 5)) != 0) b2 |= 1 << 6;
        if ((input.Buttons & (1 << 7)) != 0) b2 |= 1 << 7;
        buf[2] = b2;

        byte b3 = 0;
        if ((input.Buttons & (1 << 8)) != 0)  b3 |= 1 << 0;
        if ((input.Buttons & (1 << 9)) != 0)  b3 |= 1 << 1;
        if ((input.Buttons & (1 << 11)) != 0) b3 |= 1 << 2;
        if ((input.Buttons & (1 << 10)) != 0) b3 |= 1 << 3;
        if ((input.Buttons & (1 << 16)) != 0) b3 |= 1 << 4;
        if ((input.Buttons & (1 << 17)) != 0) b3 |= 1 << 5;
        buf[3] = b3;

        byte b4 = 0;
        if ((input.Dpad & 2) != 0) b4 |= 1 << 0;
        if ((input.Dpad & 1) != 0) b4 |= 1 << 1;
        if ((input.Dpad & 8) != 0) b4 |= 1 << 2;
        if ((input.Dpad & 4) != 0) b4 |= 1 << 3;
        if ((input.Buttons & (1 << 4)) != 0) b4 |= 1 << 6;
        if ((input.Buttons & (1 << 6)) != 0) b4 |= 1 << 7;
        buf[4] = b4;

        int lx = (int)((input.LeftStickX + 32768f) / 65535f * 4095f);
        int ly = (int)((input.LeftStickY + 32768f) / 65535f * 4095f);
        ly = 4095 - ly;
        buf[5] = (byte)(lx & 0xFF);
        buf[6] = (byte)(((lx >> 8) & 0x0F) | ((ly & 0x0F) << 4));
        buf[7] = (byte)((ly >> 4) & 0xFF);

        int rx = (int)((input.RightStickX + 32768f) / 65535f * 4095f);
        int ry = (int)((input.RightStickY + 32768f) / 65535f * 4095f);
        ry = 4095 - ry;
        buf[8] = (byte)(rx & 0xFF);
        buf[9] = (byte)(((rx >> 8) & 0x0F) | ((ry & 0x0F) << 4));
        buf[10] = (byte)((ry >> 4) & 0xFF);

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

        ctrl.SubmitRawReport(buf.AsSpan());
    }

    private void SubmitDs4Report(GamepadInput input)
    {
        var ctrl = (IDualShock4Controller)Controller!;
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

        ctrl.SubmitRawReport(buf);
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

            GamepadInput? input;
            lock (_inputLock)
            {
                input = _latestInput;
            }

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
                        SubmitXbox360Report(report);
                }
                catch { }

                if (input != null)
                    _lastReportedInput = input;
            }

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

            var oldCtrl = Controller;
            var oldMode = Mode;

            if (oldCtrl != null && oldMode == ControllerMode.SwitchPro)
            {
                // SwitchPro teardown is slow (~5s). Create new first, destroy old in background.
                Controller = null;
                if (mode == ControllerMode.SwitchPro)
                {
                    // SwitchPro → SwitchPro: recreate via background
                    var newCtrl = await Task.Run(() =>
                    {
                        CreateController(mode);
                        return Controller;
                    });
                    Controller = newCtrl;
                }
                else
                {
                    // SwitchPro → DS4/Xbox: ViGEm is fast, create inline
                    CreateController(mode);
                }
                Mode = mode;
                var toDestroy = oldCtrl;
                _ = Task.Run(() =>
                {
                    try
                    {
                        PreRemoveDevice((HMController)toDestroy);
                        DestroyController(toDestroy);
                    }
                    catch { }
                });
            }
            else
            {
                Controller = null;
                if (oldCtrl != null)
                    DestroyController(oldCtrl);

                if (mode == ControllerMode.SwitchPro)
                {
                    // ViGEm → SwitchPro: HIDMaestro creation is slow
                    await Task.Run(() => CreateController(mode));
                }
                else
                {
                    CreateController(mode);
                }
                Mode = mode;
            }

            _lastLargeMotor = 0;
            _lastSmallMotor = 0;

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

        if (Controller is HMController hmCtrl)
        {
            var ctrl = hmCtrl;
            _cleanupTask = Task.Run(() =>
            {
                PreRemoveDevice(ctrl);
                try { ctrl.Dispose(); } catch { }
            });
        }
        else
        {
            DestroyController(Controller);
        }
        Controller = null;

        OnDisconnected?.Invoke(this);
    }

    private const uint CR_SUCCESS = 0;
    private const uint DN_DISABLE_ASYNC = 0x00000001;

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern uint CM_Locate_DevNodeW(ref IntPtr pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern uint CM_Disable_DevNode(IntPtr dnDevInst, uint ulFlags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern uint CM_Uninstall_DevNode(IntPtr dnDevInst, uint ulFlags);

    private static readonly object _preRemoveLock = new();
    private static FieldInfo? _instanceIdField;

    private static void PreRemoveDevice(HMController ctrl)
    {
        try
        {
            EnsureReflectionCache(ctrl);
            var instanceId = _instanceIdField?.GetValue(ctrl) as string;
            if (string.IsNullOrEmpty(instanceId)) return;

            IntPtr devnode = IntPtr.Zero;
            var status = CM_Locate_DevNodeW(ref devnode, instanceId, 0);
            if (status != CR_SUCCESS || devnode == IntPtr.Zero) return;

            CM_Disable_DevNode(devnode, DN_DISABLE_ASYNC);
            CM_Uninstall_DevNode(devnode, 0);
        }
        catch { }
    }

    private static void EnsureReflectionCache(HMController ctrl)
    {
        if (_instanceIdField != null) return;
        lock (_preRemoveLock)
        {
            if (_instanceIdField != null) return;
            _instanceIdField = ctrl.GetType().GetField("<InstanceId>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);
        }
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
                case HMController hm:
                    hm.SubmitState(new HMGamepadState
                    {
                        Axes = HMGamepadStateHelpers.StandardAxes(hm.Profile),
                        Buttons = HMButton.None,
                        Hat = HMHat.None,
                    });
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
            _lastReportedInput = null;
        }
    }

    private static void DestroyController(object? ctrl)
    {
        try
        {
            switch (ctrl)
            {
                case IXbox360Controller xbox:
                    xbox.Disconnect();
                    break;
                case IDualShock4Controller ds4:
                    ds4.Disconnect();
                    break;
                case HMController hm:
                    hm.Dispose();
                    break;
            }
        }
        catch { }
    }

    public Task WaitForCleanupAsync()
    {
        return _cleanupTask ?? Task.CompletedTask;
    }

    public void Dispose()
    {
        Disconnect();
        _cts.Dispose();
    }
}
