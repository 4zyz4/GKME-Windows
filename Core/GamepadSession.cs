using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using GamepadEmu.Protocol;
using Google.Protobuf;
using HIDMaestro;
using HIDMaestro.Internal.Usbip;

namespace GKME.Core;

public class GamepadSession : IDisposable
{
    private const int ConnectionTimeoutMs = 3000;

    // DS4 gyro soft-knee: keep full sensitivity below the knee, then taper the
    // gain linearly so the report never hits the ±32767/±32768 full-scale value
    // (2000 deg/s) that some games treat as a sensor fault.
    private const float Ds4GyroScale = 940f;
    private const float Ds4GyroKnee = 30f;
    private const float Ds4GyroMaxInput = 40f;
    private const float Ds4GyroMaxOutput = 32760f;

    // DS4 accel soft-knee (same idea; phone sends up to ~15g while the DS4's
    // full-scale accel range is only ±4g).
    private const float Ds4AccelScale = 835f;
    private const float Ds4AccelKnee = 35f;
    private const float Ds4AccelMaxInput = 160f;
    private const float Ds4AccelMaxOutput = 32760f;

    private readonly Func<IMessage, Task> _sendToPhone;
    private readonly Action _onAudioSendStart;
    private readonly HMContext _hmContext;
    private readonly bool _hidMaestroAvailable;
    private readonly object _controllerLock = new();
    private CancellationTokenSource _cts = new();
    private volatile bool _disposed;
    private bool _readyFired;
    private DateTime _lastReceiveTime = DateTime.UtcNow;
    private byte _lastLargeMotor;
    private byte _lastSmallMotor;
    // Emulated controller LED state, forwarded to the phone. color is 0xRRGGBB.
    private volatile int _lastLedColor;
    private volatile int _lastPlayerLed;
    private byte[]? _lastLeftTriggerEffect;
    private byte[]? _lastRightTriggerEffect;

    // Switch Pro HD rumble -> decoded band parameters for the phone, which
    // synthesises the LRA waveform locally. Only low-rate parameter updates
    // cross the network, so a stutter delays an update instead of tearing a
    // hole in the PCM stream (which the phone hears as a pop). The last-sent
    // copy is kept to suppress redundant packets and to pace a keep-alive.
    private readonly object _switchHapticLock = new();
    private float _swLastHiFreqL, _swLastHiAmpL, _swLastLoFreqL, _swLastLoAmpL;
    private float _swLastHiFreqR, _swLastHiAmpR, _swLastLoFreqR, _swLastLoAmpR;

    private readonly ManualResetEventSlim _inputReceived = new(false, 1);
    private volatile GamepadInput? _pendingInput;

    private volatile bool _switchingMode;
    // Tracks the background teardown of the controllers a previous switch
    // retired. The next switch awaits it before creating replacements, so a
    // new USB/IP persona never plugs in while the old one is still being
    // removed (otherwise Windows can carry the old audio function over).
    private Task? _pendingDestroy;
    private byte _previousTouchCounter;
    private int _ds4TouchCounter;
    private readonly System.Diagnostics.Stopwatch _ds4Timestamp = System.Diagnostics.Stopwatch.StartNew();
    private readonly byte[] _touchIds = new byte[2];
    private readonly bool[] _prevTouchActive = new bool[2];
    private byte _nextTrackingId;
    private byte _ds5SequenceCounter;
    private Task? _cleanupTask;
    // Virtual USB HID keyboard + mouse (USB/IP backend). Created on first use
    // and kept for the session's lifetime so mode switches never tear them down.
    private readonly object _kbMouseLock = new();
    private VirtualKeyboardMouse? _kbMouse;
    private bool _kbMouseCreating;
    private bool _kbMouseFailed;

    // 4-channel audio forwarding (DualSense): the phone needs the raw
    // speaker + voice-coil PCM, not just the rumble peaks folded above.
    private uint _audioFrameIndex;
    private bool _audioFormatReady;
    private uint _audioSampleRate;
    private byte _audioChannels;
    private byte _audioBitsPerSample;

    // 1 kHz test-tone state (dualsense-tester WAVEOUT_CTRL). Forwarded to the
    // phone as a state flag; the phone synthesises and plays the tone locally
    // instead of the PC streaming test-tone PCM over the audio lane.
    private volatile bool _testToneEnabled;

    // Telemetry for the driver-delivered PCM stream (game → speaker).
    private long _audioReceivedBytes;
    private long _audioDrainedBytes;
    private int _audioWindowsSeen;
    private int _lastAudioWindowBytes;

    public DiscoveredDevice Device { get; }
    public ControllerMode Mode { get; private set; }
    private volatile object? _controller;
    // Second USB/IP controller, used only by modes that instantiate two
    // devices at once (currently dual Joy-Con: primary = left, secondary =
    // right). Every other mode leaves this null.
    private volatile object? _controllerSecondary;
    public object? Controller => _controller;
    public object? SecondaryController => _controllerSecondary;
    public bool IsConnected => !_disposed;
    public bool IsReady => Controller != null;
    public bool HasBeenReady => _readyFired;

    public event Action<GamepadSession>? OnReady;
    public event Action<GamepadSession>? OnConnectionLost;
    public event Action<GamepadSession>? OnDisconnected;
    /// <summary>Raised when the phone's Hello provides/updates the device name
    /// and MAC (used mainly for manually entered IP connections).</summary>
    public event Action<GamepadSession>? OnDeviceInfoUpdated;
    /// <summary>Raised when the USBip transport needs a reboot before
    /// controllers can be created (a driver upgrade Windows will not load
    /// until restart). The UI surfaces this as a reboot prompt, not an
    /// error.</summary>
    public event Action<GamepadSession>? OnRebootRequired;
    /// <summary>Raised when the USBip transport must be installed or
    /// upgraded before controllers can be created. The UI surfaces this as
    /// an install prompt.</summary>
    public event Action<GamepadSession>? OnUsbipInstallRequired;

    public GamepadSession(DiscoveredDevice device, Func<IMessage, Task> sendToPhone, Action onAudioSendStart, HMContext hmContext, bool hidMaestroAvailable = true, ControllerMode initialMode = ControllerMode.Ds4)
    {
        Device = device;
        _sendToPhone = sendToPhone;
        _onAudioSendStart = onAudioSendStart;
        _hmContext = hmContext;
        _hidMaestroAvailable = hidMaestroAvailable;
        Mode = initialMode;
    }

    public void Start()
    {
        _ = Task.Run(async () =>
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
        _ = Task.Run(async () =>
        {
            try
            {
                await SwitchHapticPumpAsync(_cts.Token);
            }
            catch (OperationCanceledException) { }
            catch { }
        });
    }

    /// <summary>Event-driven input submission — each UDP packet triggers an
    /// immediate report to the emulated controller with no rate limit.
    /// Also drains test tone and sends vibration echo on each wake to keep
    /// the phone's feedback loop running without a separate timer.</summary>
    private async Task ReportLoop(CancellationToken ct)
    {
        var sw = new System.Diagnostics.Stopwatch();
        sw.Start();
        long lastStatTicks = 0;

        while (!ct.IsCancellationRequested && !_disposed)
        {
            // Wait for an input packet or a periodic wake (vibration/telemetry).
            // 500ms timeout keeps vibration echo + audio drain running at ~2 Hz
            // when no input arrives, without fighting Windows timer throttling.
            if (!_inputReceived.Wait(500, ct))
            {
                // Periodic wake — send compact frame + test-tone state when idle.
                if (_testToneEnabled) _ = SendTestToneStateAsync();
                if (Controller != null)
                {
                    _ = SendCompactFrameAsync(
                        _lastLargeMotor, _lastSmallMotor,
                        _lastLeftTriggerEffect, _lastRightTriggerEffect,
                        sendLed: true, sendTestTone: false);
                }

                // Periodic audio-flow diagnostics (~every 500 ms).
                if (sw.Elapsed.Ticks - lastStatTicks > TimeSpan.TicksPerMillisecond * 500)
                {
                    lastStatTicks = sw.Elapsed.Ticks;
                    _audioReceivedBytes = 0;
                    _audioDrainedBytes = 0;
                    _audioWindowsSeen = 0;
                }
                continue;
            }

            // Input arrived — drain ALL pending inputs immediately, no rate limit.
            while (_pendingInput != null && Controller != null)
            {
                var input = _pendingInput;
                _pendingInput = null;
                _inputReceived.Reset();

                try
                {
                    switch (Mode)
                    {
                        case ControllerMode.Ds4:
                            SubmitDs4Report(input);
                            break;
                        case ControllerMode.Dualsense:
                            SubmitDualsenseReport(input);
                            break;
                        case ControllerMode.SwitchPro:
                            SubmitSwitchReport(input);
                            break;
                        case ControllerMode.JoyCon:
                            SubmitJoyConReport(input);
                            break;
                        default:
                            SubmitXbox360Report(input);
                            break;
                    }
                }
                catch { }

                try { SubmitKeyboardInput(input); } catch { }

                // Check for more pending input without waiting.
                if (_pendingInput == null && !_inputReceived.IsSet) break;
            }

            // Send compact frame on every input wake.
            if (Controller != null)
            {
                _ = SendCompactFrameAsync(
                    _lastLargeMotor, _lastSmallMotor,
                    _lastLeftTriggerEffect, _lastRightTriggerEffect,
                    sendLed: true, sendTestTone: false);
            }

            // Periodic audio-flow diagnostics.
            if (sw.Elapsed.Ticks - lastStatTicks > TimeSpan.TicksPerMillisecond * 500)
            {
                lastStatTicks = sw.Elapsed.Ticks;
                _audioReceivedBytes = 0;
                _audioDrainedBytes = 0;
                _audioWindowsSeen = 0;
            }
        }
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
            }
        }
        catch { }
    }

    public void HandleUdpGamepadInput(GamepadInput input)
    {
        _lastReceiveTime = DateTime.UtcNow;
        if (!IsReady) return;
        _pendingInput = input;
        _inputReceived.Set();
    }

    /// <summary>收包线程直调入口（无队列，收到即上报）。</summary>
    public void ReportGamepadInput(GamepadInput input)
    {
        _lastReceiveTime = DateTime.UtcNow;
        if (!IsReady) return;
        _pendingInput = input;
        _inputReceived.Set();
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

    public async Task SendServerHelloAsync()
    {
        await SendAsync(NetworkService.BuildServerHello());
    }

    private void HandleHello(Hello hello)
    {
        if (_disposed) return;

        bool infoChanged = false;
        if (!string.IsNullOrWhiteSpace(hello.DeviceName) && hello.DeviceName != Device.DeviceName)
        {
            Device.DeviceName = hello.DeviceName.Trim();
            infoChanged = true;
        }
        if (!string.IsNullOrWhiteSpace(hello.MacAddress) && hello.MacAddress != Device.MacAddress)
        {
            Device.MacAddress = hello.MacAddress.Trim();
            infoChanged = true;
        }
        if (infoChanged)
        {
            OnDeviceInfoUpdated?.Invoke(this);
        }

        if (_controller == null && !_switchingMode)
        {
            lock (_controllerLock)
            {
                if (_controller == null && !_switchingMode)
                {
                    try
                    {
                        var created = CreateControllers(Mode);
                        _controller = created.Primary;
                        _controllerSecondary = created.Secondary;
                    }
                    catch (UsbipRebootRequiredException)
                    {
                        // The driver update needs a reboot, not an error
                        // dialog; surface it as a prompt.
                        try { OnRebootRequired?.Invoke(this); } catch { }
                    }
                    catch (UsbipInstallRequiredException)
                    {
                        // The transport must be installed/upgraded; the UI
                        // shows the install prompt rather than an error.
                        try { OnUsbipInstallRequired?.Invoke(this); } catch { }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[GamepadSession] create controller {Mode} failed: {ex.Message}");
                        System.IO.File.AppendAllText(
                            Path.Combine(AppContext.BaseDirectory, "gkme_error.log"),
                            $"{DateTime.UtcNow:O} CREATE_CONTROLLER FAIL {Mode}: {ex}\n");
                    }
                }
            }
        }

        if (!_readyFired)
        {
            _readyFired = true;
            OnReady?.Invoke(this);
        }

        // Acknowledge the phone's hello so an already-existing session (e.g.
        // one that survived a watchdog timeout) can re-sync on the auto-reconnect
        // path — otherwise the phone never receives any packet and stays stuck.
        _ = SendAsync(NetworkService.BuildServerHello());
        _ = SendLedStateAsync();
        _ = SendTestToneStateAsync();
    }

    private readonly record struct CreatedControllers(object Primary, object? Secondary);

    /// <summary>Creates the controller(s) a mode needs. Every mode is a single
    /// USB/IP composite device except dual Joy-Con, which presents a real
    /// left (057E:2006) and right (057E:2007) half as two independent USB
    /// devices so eden/SDL sees a genuine pair.</summary>
    private CreatedControllers CreateControllers(ControllerMode mode)
    {
        if (mode == ControllerMode.JoyCon)
        {
            var left = CreateController("joycon-l-composite", mode);
            object? right = null;
            try
            {
                right = CreateController("joycon-r-composite", mode);
            }
            catch
            {
                DestroyController(left);
                throw;
            }
            return new CreatedControllers(left, right);
        }

        // All controllers are presented through the USB/IP backend. Pick the
        // matching composite profile; the Xbox 360 wired persona is a
        // vendor-class device, the others are HID composite personas.
        string profileId = mode switch
        {
            ControllerMode.Xbox360 => "xbox360-composite",
            ControllerMode.Dualsense => "dualsense-composite",
            ControllerMode.SwitchPro => "switch-pro-composite",
            _ => "dualshock-4-v2-composite",
        };
        return new CreatedControllers(CreateController(profileId, mode), null);
    }

    private object CreateController(string profileId, ControllerMode mode)
    {
        var profile = _hmContext.GetProfile(profileId)
            ?? throw new InvalidOperationException($"No HIDMaestro profile found for mode {mode} (profile '{profileId}').");

        var ctrl = _hmContext.CreateController(profile);
        ctrl.OutputDecoded += OnHmOutputDecoded;
        ctrl.OutputReceived += OnHmOutputReceived;
        if (mode == ControllerMode.Dualsense)
            AttachDualsenseAudio(ctrl);
        else if (mode == ControllerMode.Ds4)
            AttachDs4Audio(ctrl);
        return ctrl;
    }

    private void OnHmOutputDecoded(object? sender, HMOutputDecodedEventArgs e)
    {
        int? newColor = null;
        int? newPlayerLed = null;
        byte[]? newLeftTriggerEffect = null;
        byte[]? newRightTriggerEffect = null;
        int? validFlag0 = null;
        foreach (var (k, v) in e.Fields)
        {
            var key = k.ToLowerInvariant();
            if (key is "leftmotor" or "largemotor" or "leftactuator" or "leftlinear")
                _lastLargeMotor = Convert.ToByte(v);
            else if (key is "rightmotor" or "smallmotor" or "rightactuator" or "rightlinear")
                _lastSmallMotor = Convert.ToByte(v);
            else if (key is "lightbar" && v is byte[] rgb && rgb.Length >= 3)
                newColor = (rgb[0] << 16) | (rgb[1] << 8) | rgb[2];
            else if (key is "playerindicator")
                newPlayerLed = Convert.ToInt32(v);
            else if (key is "validflag0")
                validFlag0 = Convert.ToInt32(v);
            else if (key is "lefttriggereffect" && v is byte[] leftTriggerBytes && leftTriggerBytes.Length >= 11)
                newLeftTriggerEffect = leftTriggerBytes;
            else if (key is "righttriggereffect" && v is byte[] rightTriggerBytes && rightTriggerBytes.Length >= 11)
                newRightTriggerEffect = rightTriggerBytes;
        }

        bool changed = false;
        if (newColor.HasValue && newColor.Value != _lastLedColor)
        {
            _lastLedColor = newColor.Value;
            changed = true;
        }
        if (newPlayerLed.HasValue && newPlayerLed.Value != _lastPlayerLed)
        {
            _lastPlayerLed = newPlayerLed.Value;
            changed = true;
        }
        if (changed)
        {
            _ = SendLedStateAsync();
        }

        // DualSense VALID_FLAG0：bit2(0x04)=右扳机、bit3(0x08)=左扳机。手柄只应用被标记的
        // 那一侧扳机块；一次只更新一侧的报文里，另一侧块往往是 0/旧值。旧实现无脑把两块都
        // 转发，手机会用 0/旧值覆盖另一侧，导致“扳机字节不完整 / 少效果”。这里按标志只更新
        // 有效侧，另一侧保留上次已知值再一起下发（与 Android 端 updateAdvancedAudioHaptics
        // TriggerCache 的语义一致）。
        const int EnableRightTrigger = 0x04;
        const int EnableLeftTrigger = 0x08;
        bool leftValid = validFlag0 == null || (validFlag0.Value & EnableLeftTrigger) != 0;
        bool rightValid = validFlag0 == null || (validFlag0.Value & EnableRightTrigger) != 0;

        bool triggersUpdated = false;
        if (newLeftTriggerEffect != null && leftValid)
        {
            _lastLeftTriggerEffect = newLeftTriggerEffect;
            triggersUpdated = true;
        }
        if (newRightTriggerEffect != null && rightValid)
        {
            _lastRightTriggerEffect = newRightTriggerEffect;
            triggersUpdated = true;
        }

        if (triggersUpdated)
        {
            _ = SendTriggerEffectAsync(_lastLeftTriggerEffect, _lastRightTriggerEffect);
        }
    }

    /// <summary>Forwards the emulated controller's LED state to the phone. Xbox 360
    /// has no LED, so it always reports #000000 / no player indicator.</summary>
    private async Task SendLedStateAsync()
    {
        bool noLed = Mode == ControllerMode.Xbox360;
        int color = noLed ? 0 : _lastLedColor;
        int player = noLed ? 0 : _lastPlayerLed;
        await SendAsync(new ServerToClient
        {
            LedState = new LedState
            {
                Color = (uint)color,
                PlayerLed = (uint)player,
            }
        });
    }

    /// <summary>Forwards the 1 kHz test-tone on/off state to the phone. The phone
    /// synthesises the tone locally; no test-tone PCM is streamed.</summary>
    private async Task SendTestToneStateAsync()
    {
        await SendAsync(new ServerToClient
        {
            TestTone = new TestTone
            {
                Enabled = _testToneEnabled,
            }
        });
    }

    private async Task SendTriggerEffectAsync(byte[]? leftEffect, byte[]? rightEffect)
    {
        if (leftEffect == null && rightEffect == null) return;

        _lastLeftTriggerEffect = leftEffect;
        _lastRightTriggerEffect = rightEffect;

        await SendAsync(new ServerToClient
        {
            TriggerEffects = new TriggerEffects
            {
                LeftTriggerEffect = ByteString.CopyFrom(leftEffect ?? Array.Empty<byte>()),
                RightTriggerEffect = ByteString.CopyFrom(rightEffect ?? Array.Empty<byte>()),
            }
        });
    }

    private async Task SendTriggerEffectEchoAsync(bool force = true)
    {
        if (_lastLeftTriggerEffect == null && _lastRightTriggerEffect == null) return;

        await SendAsync(new ServerToClient
        {
            TriggerEffects = new TriggerEffects
            {
                LeftTriggerEffect = ByteString.CopyFrom(_lastLeftTriggerEffect),
                RightTriggerEffect = ByteString.CopyFrom(_lastRightTriggerEffect),
            }
        });
    }

    private async Task SendClearTriggerEffectsAsync()
    {
        var clearEffect = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        await SendAsync(new ServerToClient
        {
            TriggerEffects = new TriggerEffects
            {
                LeftTriggerEffect = ByteString.CopyFrom(clearEffect),
                RightTriggerEffect = ByteString.CopyFrom(clearEffect),
            }
        });
    }

    /// <summary>Decode one HD rumble block ([hf, hfAmp|hfHi, lf|lfAmpHi, lfAmpLo],
    /// SDL/dekuNukem encoding). Frequency: 10 * 2^(index/32); high index =
    /// hf/4 + 0x60, low index = lf + 0x40. Amplitude: table index / 100.</summary>
    private static void DecodeHdRumble(ReadOnlySpan<byte> b,
        out float hiFreqHz, out float hiAmp, out float loFreqHz, out float loAmp)
    {
        int encHiF = b[0] | ((b[1] & 0x01) << 8);
        int hiCode = b[1] & 0xFE;
        int encLoF = b[2] & 0x7F;
        int loMsb = (b[2] & 0x80) != 0 ? 1 : 0;
        int loLow = b[3];

        int hiIndex = encHiF / 4 + 0x60;
        hiFreqHz = 10.0f * MathF.Pow(2.0f, hiIndex / 32.0f);
        hiAmp = Math.Clamp((hiCode / 2) / 100.0f, 0f, 1f);

        int loIndex = encLoF + 0x40;
        loFreqHz = 10.0f * MathF.Pow(2.0f, loIndex / 32.0f);
        int loAmpIdx = loLow >= 0x40 ? (loLow - 0x40) * 2 + loMsb : 0;
        loAmp = Math.Clamp(loAmpIdx / 100.0f, 0f, 1f);
    }

    /// <summary>Switch Pro HD rumble, decoded and forwarded to the phone.
    /// Output reports 0x01 and 0x10 carry one 4-byte HD rumble block per side
    /// at payload [1..4] / [5..8] (after the packet-number byte). The phone
    /// synthesises the LRA waveform from (frequency, amplitude), so only these
    /// parameters cross the network. A state change is sent immediately for low
    /// latency; <see cref="SwitchHapticPumpAsync"/> keeps a 50 ms keep-alive
    /// flowing so the phone's synth thread stays warm — its output device must
    /// not stop between rumbles, or the next onset clicks.</summary>
    private void HandleSwitchRumbleOutput(HMOutputPacket packet)
    {
        if (packet.Source != HMOutputSource.HidOutput) return;
        if (packet.ReportId != 0x01 && packet.ReportId != 0x10) return;
        var d = packet.Data.Span;
        if (d.Length < 9) return;

        DecodeHdRumble(d.Slice(1, 4), out float lHiF, out float lHiA, out float lLoF, out float lLoA);
        DecodeHdRumble(d.Slice(5, 4), out float rHiF, out float rHiA, out float rLoF, out float rLoA);

        lock (_switchHapticLock)
        {
            bool changed =
                MathF.Abs(lHiF - _swLastHiFreqL) > 0.5f || MathF.Abs(lLoF - _swLastLoFreqL) > 0.5f ||
                MathF.Abs(rHiF - _swLastHiFreqR) > 0.5f || MathF.Abs(rLoF - _swLastLoFreqR) > 0.5f ||
                MathF.Abs(lHiA - _swLastHiAmpL) > 0.002f || MathF.Abs(lLoA - _swLastLoAmpL) > 0.002f ||
                MathF.Abs(rHiA - _swLastHiAmpR) > 0.002f || MathF.Abs(rLoA - _swLastLoAmpR) > 0.002f;

            _swLastHiFreqL = lHiF; _swLastHiAmpL = lHiA; _swLastLoFreqL = lLoF; _swLastLoAmpL = lLoA;
            _swLastHiFreqR = rHiF; _swLastHiAmpR = rHiA; _swLastLoFreqR = rLoF; _swLastLoAmpR = rLoA;
            if (!changed) return;
        }

        SendHdRumble(lHiF, lHiA, lLoF, lLoA, rHiF, rHiA, rLoF, rLoA);
    }

    /// <summary>Joy-Con HD rumble. A single Joy-Con has one LRA and reports
    /// one effective accent per output 0x01/0x10; decode it and drive the
    /// phone lane matching the half that produced it (left device → left
    /// lane, right device → right lane), keeping the other lane's last value.</summary>
    private void HandleJoyconRumbleOutput(HMOutputPacket packet, bool left)
    {
        if (packet.Source != HMOutputSource.HidOutput) return;
        if (packet.ReportId != 0x01 && packet.ReportId != 0x10) return;
        var d = packet.Data.Span;
        if (d.Length < 5) return;

        DecodeHdRumble(d.Slice(1, 4), out float hiF, out float hiA, out float loF, out float loA);

        lock (_switchHapticLock)
        {
            bool changed;
            if (left)
            {
                changed =
                    MathF.Abs(hiF - _swLastHiFreqL) > 0.5f || MathF.Abs(loF - _swLastLoFreqL) > 0.5f ||
                    MathF.Abs(hiA - _swLastHiAmpL) > 0.002f || MathF.Abs(loA - _swLastLoAmpL) > 0.002f;
                _swLastHiFreqL = hiF; _swLastHiAmpL = hiA; _swLastLoFreqL = loF; _swLastLoAmpL = loA;
            }
            else
            {
                changed =
                    MathF.Abs(hiF - _swLastHiFreqR) > 0.5f || MathF.Abs(loF - _swLastLoFreqR) > 0.5f ||
                    MathF.Abs(hiA - _swLastHiAmpR) > 0.002f || MathF.Abs(loA - _swLastLoAmpR) > 0.002f;
                _swLastHiFreqR = hiF; _swLastHiAmpR = hiA; _swLastLoFreqR = loF; _swLastLoAmpR = loA;
            }
            if (!changed) return;

            SendHdRumble(_swLastHiFreqL, _swLastHiAmpL, _swLastLoFreqL, _swLastLoAmpL,
                         _swLastHiFreqR, _swLastHiAmpR, _swLastLoFreqR, _swLastLoAmpR);
        }
    }

    /// <summary>Keep-alive for the phone's HD-rumble synth. While a Switch Pro
    /// is emulated the current band state is re-sent every 50 ms, including an
    /// all-zero state, so the phone keeps its synthesis thread and output
    /// device running. Restarting that device at each rumble onset is what
    /// clicks; a steady stream (silent when idle) removes the transient.</summary>
    private async Task SwitchHapticPumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(50, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (_disposed) return;
            if ((Mode != ControllerMode.SwitchPro && Mode != ControllerMode.JoyCon) || Controller == null) continue;

            float lhf, lha, llf, lla, rhf, rha, rlf, rla;
            lock (_switchHapticLock)
            {
                lhf = _swLastHiFreqL; lha = _swLastHiAmpL; llf = _swLastLoFreqL; lla = _swLastLoAmpL;
                rhf = _swLastHiFreqR; rha = _swLastHiAmpR; rlf = _swLastLoFreqR; rla = _swLastLoAmpR;
            }
            SendHdRumble(lhf, lha, llf, lla, rhf, rha, rlf, rla);
        }
    }

    private void SendHdRumble(float lhf, float lha, float llf, float lla,
                              float rhf, float rha, float rlf, float rla)
    {
        _ = SendAsync(new ServerToClient
        {
            HdRumble = new HdRumble
            {
                LeftHighFreqHz = lhf, LeftHighAmp = lha,
                LeftLowFreqHz = llf, LeftLowAmp = lla,
                RightHighFreqHz = rhf, RightHighAmp = rha,
                RightLowFreqHz = rlf, RightLowAmp = rla,
            }
        });
    }

    private void OnHmOutputReceived(HMController controller, HMOutputPacket packet)
    {
        if (Mode == ControllerMode.SwitchPro)
        {
            HandleSwitchRumbleOutput(packet);
            return;
        }

        if (Mode == ControllerMode.JoyCon)
        {
            // One report per half; the primary device is the left Joy-Con,
            // the secondary the right. Map each half's rumble to the phone's
            // matching LRA lane. Ignore reports from a controller being
            // torn down during a mode switch.
            if (ReferenceEquals(controller, _controller))
                HandleJoyconRumbleOutput(packet, left: true);
            else if (ReferenceEquals(controller, _controllerSecondary))
                HandleJoyconRumbleOutput(packet, left: false);
            return;
        }

        // Feature report 0x80 is Sony's test-command surface (device/action
        // first two bytes). dualsense-tester uses it for the 1kHz sine-wave
        // audio test (WAVEOUT_CTRL on the AUDIO device); the real pad's
        // firmware generates the tone internally. Synthesize it here instead
        // of misreading the command bytes as rumble amplitudes.
        if (packet.Source == HMOutputSource.HidFeature && packet.ReportId == 0x80)
        {
            HandleDualsenseTestCommand(packet.Data.Span);
            return;
        }

        var d = packet.Data.Span;
        if (d.Length < 4) return;

        if (Mode == ControllerMode.Dualsense)
        {
            // DualSense output report 0x02 (no report-id byte): payload
            // [0]=validFlag0, [1]=validFlag1, [2]=rightMotor(small),
            // [3]=leftMotor(large).
            _lastSmallMotor = d[2];
            _lastLargeMotor = d[3];
            return;
        }

        if (Mode == ControllerMode.Xbox360 && packet.Source == HMOutputSource.HidOutput)
        {
            // Xbox 360 wired output report arrives on the interrupt OUT
            // endpoint: wire bytes are [RID=0x00][len=0x08][0x00][large][small]...
            // The device strips the RID, so the payload here starts at len.
            // [2]=large motor, [3]=small motor.
            _lastLargeMotor = d[2];
            _lastSmallMotor = d[3];
            return;
        }

        _lastLargeMotor = d[2];
        _lastSmallMotor = d[3];
    }

    private void HandleDualsenseTestCommand(ReadOnlySpan<byte> d)
    {
        if (Mode != ControllerMode.Dualsense) return;
        if (d.Length < 3) return;

        // Feature report 0x80 with bytes [0]=6 (AUDIO), [1]=2 (WAVEOUT_CTRL),
        // [2]!=0 = enable the internal 1 kHz sine-wave generator. The phone
        // synthesises the tone locally; forward only the on/off state.
        byte deviceId = d[0];
        byte actionId = d[1];
        if (deviceId != 6 || actionId != 2) return;

        _testToneEnabled = d[2] != 0;
        _ = SendTestToneStateAsync();
    }

    private void AttachDualsenseAudio(HMController ctrl)
    {
        try
        {
            var audio = ctrl.UsbAudio;
            if (audio == null) return;

            // Haptic audio arrives as channels 3/4 of the 4-channel OUT
            // stream (roles hapticLeft/hapticRight). Fold its amplitude
            // into the normal rumble lanes sent to the phone.
            audio.Output.FramesReceived += OnDualsenseAudioFrames;
        }
        catch { }
    }

    private void AttachDs4Audio(HMController ctrl)
    {
        try
        {
            var audio = ctrl.UsbAudio;
            if (audio == null) return;

            // DS4 has 2-channel 32 kHz stereo headset audio (headsetLeft/headsetRight).
            // Convert to 4-channel format expected by phone: ch0=silent, ch1=mono mix, ch2=0, ch3=0.
            audio.Output.FramesReceived += OnDs4AudioFrames;
        }
        catch { }
    }

    private void OnDs4AudioFrames(HMAudioOutput output, ReadOnlyMemory<byte> pcm)
    {
        try
        {
            int channels = output.Channels;
            if (channels != 2 || pcm.IsEmpty) return;

            var span = pcm.Span;
            // DS4 has 2-channel 32 kHz stereo. Resample to TargetSampleRate (44.1 kHz)
            // and convert to phone's expected 4-channel layout.
            if (!_audioFormatReady)
            {
                _audioSampleRate = (uint)TargetSampleRate;
                _audioChannels = 4;
                _audioBitsPerSample = (byte)output.BitsPerSample;
                _audioFormatReady = true;
                System.Diagnostics.Debug.WriteLine(
                    $"[Ds4Audio] stream opened: {output.SampleRateHz}Hz {channels}ch {output.BitsPerSample}bit -> 4ch @ {TargetSampleRate}Hz");
            }

            int bitsPerSample = output.BitsPerSample;
            int bytesPerSample = bitsPerSample / 8;
            int srcChannels = channels;
            int srcBytesPerFrame = srcChannels * bytesPerSample;
            int srcSampleRate = output.SampleRateHz;
            int srcNumSamples = span.Length / srcBytesPerFrame;

            // Resample from srcSampleRate to TargetSampleRate using linear interpolation
            double ratio = (double)TargetSampleRate / srcSampleRate;
            int dstNumSamples = (int)(srcNumSamples * ratio);
            int dstChannels = 4;
            int dstBytesPerFrame = dstChannels * bytesPerSample;
            var dst = new byte[dstNumSamples * dstBytesPerFrame];

            for (int s = 0; s < dstNumSamples; s++)
            {
                double srcPos = s / ratio;
                int srcIdx0 = (int)srcPos;
                int srcIdx1 = Math.Min(srcIdx0 + 1, srcNumSamples - 1);
                double frac = srcPos - srcIdx0;

                int srcOff0 = srcIdx0 * srcBytesPerFrame;
                int srcOff1 = srcIdx1 * srcBytesPerFrame;
                int dstOff = s * dstBytesPerFrame;

                short left0 = BitConverter.ToInt16(span[srcOff0..(srcOff0 + 2)]);
                short left1 = BitConverter.ToInt16(span[srcOff1..(srcOff1 + 2)]);
                short right0 = BitConverter.ToInt16(span[(srcOff0 + 2)..(srcOff0 + 4)]);
                short right1 = BitConverter.ToInt16(span[(srcOff1 + 2)..(srcOff1 + 4)]);

                short left = (short)(left0 + frac * (left1 - left0));
                short right = (short)(right0 + frac * (right1 - right0));
                short mono = (short)((left + right) / 2);

                dst[dstOff] = 0; dst[dstOff + 1] = 0;
                dst[dstOff + 2] = (byte)(mono & 0xFF); dst[dstOff + 3] = (byte)((mono >> 8) & 0xFF);
                dst[dstOff + 4] = 0; dst[dstOff + 5] = 0;
                dst[dstOff + 6] = 0; dst[dstOff + 7] = 0;
            }

            DrainSingleChunk(dst);
            _audioReceivedBytes += span.Length;
            _audioWindowsSeen++;
            _lastAudioWindowBytes = span.Length;
        }
        catch { }
    }

    // Target sample rate forwarded to the phone. Both the emulated DualSense
    // audio endpoint and the phone's native DS5 PCM path run at 48 kHz, so
    // forwarding at the source rate avoids a lossy double resample
    // (48k -> 44.1k on the PC, 44.1k -> 48k on the phone). The per-window
    // resamplers are stateless, so any rate conversion injects a click at
    // every buffer boundary, which is heard as noise on the voice coils.
    private const int TargetSampleRate = 48000;

    private void OnDualsenseAudioFrames(HMAudioOutput output, ReadOnlyMemory<byte> pcm)
    {
        try
        {
            int channels = output.Channels;
            if (channels < 4 || pcm.IsEmpty) return;

            var span = pcm.Span;
            // DualSense 4-channel layout: ch0=speakerLeft, ch1=speakerRight, ch2=hapticLeft, ch3=hapticRight
            // Phone expects: ch0=silent, ch1=controller audio (mono speaker mix), ch2=left voice coil, ch3=right voice coil
            if (!_audioFormatReady)
            {
                _audioSampleRate = (uint)TargetSampleRate;
                _audioChannels = 4;
                _audioBitsPerSample = (byte)output.BitsPerSample;
                _audioFormatReady = true;
                System.Diagnostics.Debug.WriteLine(
                    $"[DualSenseAudio] stream opened: {output.SampleRateHz}Hz {channels}ch {output.BitsPerSample}bit -> remapped 4ch @ {TargetSampleRate}Hz");
            }

            int bitsPerSample = output.BitsPerSample;
            int bytesPerSample = bitsPerSample / 8;
            int srcChannels = channels;
            int srcBytesPerFrame = srcChannels * bytesPerSample;
            int srcSampleRate = output.SampleRateHz;
            int srcNumSamples = span.Length / srcBytesPerFrame;

            // Resample from srcSampleRate to TargetSampleRate using linear interpolation
            double ratio = (double)TargetSampleRate / srcSampleRate;
            int dstNumSamples = (int)(srcNumSamples * ratio);
            int dstChannels = 4;
            int dstBytesPerFrame = dstChannels * bytesPerSample;
            var dst = new byte[dstNumSamples * dstBytesPerFrame];

            for (int s = 0; s < dstNumSamples; s++)
            {
                double srcPos = s / ratio;
                int srcIdx0 = (int)srcPos;
                int srcIdx1 = Math.Min(srcIdx0 + 1, srcNumSamples - 1);
                double frac = srcPos - srcIdx0;

                int srcOff0 = srcIdx0 * srcBytesPerFrame;
                int srcOff1 = srcIdx1 * srcBytesPerFrame;
                int dstOff = s * dstBytesPerFrame;

                // Channel 0: speakerLeft
                short speakerL0 = BitConverter.ToInt16(span[srcOff0..(srcOff0 + 2)]);
                short speakerL1 = BitConverter.ToInt16(span[srcOff1..(srcOff1 + 2)]);
                // Channel 1: speakerRight
                short speakerR0 = BitConverter.ToInt16(span[(srcOff0 + 2)..(srcOff0 + 4)]);
                short speakerR1 = BitConverter.ToInt16(span[(srcOff1 + 2)..(srcOff1 + 4)]);
                // Channel 2: hapticLeft
                short hapticL0 = BitConverter.ToInt16(span[(srcOff0 + 4)..(srcOff0 + 6)]);
                short hapticL1 = BitConverter.ToInt16(span[(srcOff1 + 4)..(srcOff1 + 6)]);
                // Channel 3: hapticRight
                short hapticR0 = BitConverter.ToInt16(span[(srcOff0 + 6)..(srcOff0 + 8)]);
                short hapticR1 = BitConverter.ToInt16(span[(srcOff1 + 6)..(srcOff1 + 8)]);

                // Linear interpolation
                short speakerL = (short)(speakerL0 + frac * (speakerL1 - speakerL0));
                short speakerR = (short)(speakerR0 + frac * (speakerR1 - speakerR0));
                short hapticL = (short)(hapticL0 + frac * (hapticL1 - hapticL0));
                short hapticR = (short)(hapticR0 + frac * (hapticR1 - hapticR0));

                short monoSpeaker = (short)((speakerL + speakerR) / 2);

                dst[dstOff] = 0; dst[dstOff + 1] = 0;                    // ch0: silent
                dst[dstOff + 2] = (byte)(monoSpeaker & 0xFF); dst[dstOff + 3] = (byte)((monoSpeaker >> 8) & 0xFF); // ch1: mono speaker
                dst[dstOff + 4] = (byte)(hapticL & 0xFF); dst[dstOff + 5] = (byte)((hapticL >> 8) & 0xFF);         // ch2: left voice coil
                dst[dstOff + 6] = (byte)(hapticR & 0xFF); dst[dstOff + 7] = (byte)((hapticR >> 8) & 0xFF);         // ch3: right voice coil
            }

            // Skip silent windows: the phone's native sender keeps the DualSense
            // endpoint fed with locally-generated silence, so forwarding silence
            // only floods the link and blocks the phone's receive thread.
            if (!IsSilent(dst)) DrainSingleChunk(dst);
            _audioReceivedBytes += span.Length;
            _audioWindowsSeen++;
            _lastAudioWindowBytes = span.Length;
        }
        catch { }
    }

    /// <summary>True when every 16-bit sample is within a few LSB of zero. A game
    /// holding the DualSense audio stream open makes the endpoint deliver silent
    /// windows (often dithered, not exactly 0) continuously; the phone cannot tell
    /// them from real audio, so they are dropped at the source.</summary>
    private static bool IsSilent(byte[] data)
    {
        for (int i = 0; i + 1 < data.Length; i += 2)
        {
            int s = (short)(data[i] | (data[i + 1] << 8));
            if (s > 8 || s < -8) return false;
        }
        return true;
    }

    /// <summary>Split an already-collected PCM buffer into frame-aligned
    /// UDP chunks and send them. Called by OnDualsenseAudioFrames.</summary>
    private void DrainSingleChunk(byte[] pcm)
    {
        if (!_audioFormatReady) return;

        int bytesPerFrame = _audioChannels * (_audioBitsPerSample / 8);
        if (bytesPerFrame <= 0) return;

        int completeBytes = (pcm.Length / bytesPerFrame) * bytesPerFrame;
        if (completeBytes == 0) return;

        _onAudioSendStart?.Invoke();

        const int MaxPacketBytes = 4096;
        int chunkBytes = (MaxPacketBytes / bytesPerFrame) * bytesPerFrame;

        for (int sent = 0; sent < completeBytes; sent += chunkBytes)
        {
            int n = Math.Min(chunkBytes, completeBytes - sent);
            var bytes = new byte[n];
            Buffer.BlockCopy(pcm, sent, bytes, 0, n);
            _ = SendAudioFrameAsync(bytes);
        }
        _audioDrainedBytes += completeBytes;
    }

    private Task SendAudioFrameAsync(byte[] pcm)
    {
        try
        {
            var frame = new ServerToClient
            {
                AudioFrame = new AudioFrame
                {
                    SampleRateHz = _audioSampleRate,
                    Channels = _audioChannels,
                    BitsPerSample = _audioBitsPerSample,
                    FrameIndex = _audioFrameIndex++,
                    SampleCount = (uint)(pcm.Length / (_audioChannels * (_audioBitsPerSample / 8))),
                    Pcm = Google.Protobuf.ByteString.CopyFrom(pcm),
                }
            };
            return SendAsync(frame);
        }
        catch
        {
            return Task.CompletedTask;
        }
    }

    private void ClearPendingAudio()
    {
        _audioFrameIndex = 0;
        _testToneEnabled = false;
        _audioFormatReady = false;
    }

    private void SubmitXbox360Report(GamepadInput input)
    {
        var ctrl = (HMController)Controller!;

        // XInput button word: A/B/X/Y live in the high byte, LB/RB/Guide and
        // the menu/d-pad buttons in the low byte. This is the native layout a
        // real Xbox 360 controller reports to Windows, so the host's XInput
        // stack decodes it correctly.
        ushort xb = 0;
        if ((input.Buttons & (1u << 0)) != 0) xb |= 0x1000; // A
        if ((input.Buttons & (1u << 1)) != 0) xb |= 0x2000; // B
        if ((input.Buttons & (1u << 2)) != 0) xb |= 0x4000; // X
        if ((input.Buttons & (1u << 3)) != 0) xb |= 0x8000; // Y
        if ((input.Buttons & (1u << 4)) != 0) xb |= 0x0100; // LB
        if ((input.Buttons & (1u << 5)) != 0) xb |= 0x0200; // RB
        if ((input.Buttons & (1u << 8)) != 0) xb |= 0x0020; // Back
        if ((input.Buttons & (1u << 9)) != 0) xb |= 0x0010; // Start
        if ((input.Buttons & (1u << 10)) != 0) xb |= 0x0040; // LS
        if ((input.Buttons & (1u << 11)) != 0) xb |= 0x0080; // RS
        if ((input.Buttons & (1u << 16)) != 0) xb |= 0x0400; // Guide
        if ((input.Dpad & 1) != 0) xb |= 0x0001; // Up
        if ((input.Dpad & 2) != 0) xb |= 0x0002; // Down
        if ((input.Dpad & 4) != 0) xb |= 0x0004; // Left
        if ((input.Dpad & 8) != 0) xb |= 0x0008; // Right

        var lt = input.LeftTrigger > 0 ? input.LeftTrigger : ((input.Buttons & (1u << 6)) != 0 ? 255u : 0u);
        var rt = input.RightTrigger > 0 ? input.RightTrigger : ((input.Buttons & (1u << 7)) != 0 ? 255u : 0u);

        // 20-byte Xbox 360 wired input report (verbatim, no separate Report ID
        // handling — the USB/IP backend transmits vendor reports as-is).
        var buf = new byte[20];
        buf[0] = 0x00; // report id
        buf[1] = 0x14; // payload size
        buf[2] = (byte)(xb & 0xFF);
        buf[3] = (byte)(xb >> 8);
        buf[4] = (byte)Math.Clamp(lt, 0u, 255u);
        buf[5] = (byte)Math.Clamp(rt, 0u, 255u);

        short lx = (short)input.LeftStickX;
        short ly = (short)Math.Clamp(-(long)input.LeftStickY, short.MinValue, short.MaxValue);
        short rx = (short)input.RightStickX;
        short ry = (short)Math.Clamp(-(long)input.RightStickY, short.MinValue, short.MaxValue);
        buf[6] = (byte)(lx & 0xFF); buf[7] = (byte)((lx >> 8) & 0xFF);
        buf[8] = (byte)(ly & 0xFF); buf[9] = (byte)((ly >> 8) & 0xFF);
        buf[10] = (byte)(rx & 0xFF); buf[11] = (byte)((rx >> 8) & 0xFF);
        buf[12] = (byte)(ry & 0xFF); buf[13] = (byte)((ry >> 8) & 0xFF);
        // 14..19 reserved (zero)

        ctrl.SubmitRawReport(buf.AsSpan());

        // Mousepad handled via SubmitMouseAndTouchpad (reads protobuf mouse fields)
        SubmitMouseAndTouchpad(input);
    }

    private void SubmitDs4Report(GamepadInput input)
    {
        var ctrl = (HMController)Controller!;
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

        WriteInt16LE(buf, 9, (short)(_ds4Timestamp.ElapsedMilliseconds & 0xFFFF));

        buf[11] = (byte)Math.Clamp(input.BatteryLevel / 10u, 0u, 10u);

        WriteInt16LE(buf, 12, (short)SoftLimit(input.GyroX, Ds4GyroScale, Ds4GyroKnee, Ds4GyroMaxInput, Ds4GyroMaxOutput));
        WriteInt16LE(buf, 14, (short)SoftLimit(input.GyroY, Ds4GyroScale, Ds4GyroKnee, Ds4GyroMaxInput, Ds4GyroMaxOutput));
        WriteInt16LE(buf, 16, (short)SoftLimit(input.GyroZ, Ds4GyroScale, Ds4GyroKnee, Ds4GyroMaxInput, Ds4GyroMaxOutput));

        WriteInt16LE(buf, 18, (short)SoftLimit(input.AccelX, Ds4AccelScale, Ds4AccelKnee, Ds4AccelMaxInput, Ds4AccelMaxOutput));
        WriteInt16LE(buf, 20, (short)SoftLimit(input.AccelY, Ds4AccelScale, Ds4AccelKnee, Ds4AccelMaxInput, Ds4AccelMaxOutput));
        WriteInt16LE(buf, 22, (short)SoftLimit(input.AccelZ, Ds4AccelScale, Ds4AccelKnee, Ds4AccelMaxInput, Ds4AccelMaxOutput));

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
                _touchIds[i] = (byte)(_nextTrackingId++ & 0x7F);
            _prevTouchActive[i] = isActive;
        }

        byte ds4CurrentCounter = (byte)(_ds4TouchCounter & 0x7F);
        buf[32] = 1;
        buf[33] = ds4CurrentCounter;
        WriteTouchData(buf, 34,
            (int)(t0?.X ?? 0), (int)(t0?.Y ?? 0),
            t0?.Active ?? false, (byte)(_touchIds[0] & 0x7F));
        WriteTouchData(buf, 38,
            (int)(t1?.X ?? 0), (int)(t1?.Y ?? 0),
            t1?.Active ?? false, (byte)(_touchIds[1] & 0x7F));

        byte tid1 = (byte)((_nextTrackingId + 1) & 0x7F);
        WriteTouchData(buf, 42, 0, 0, false, tid1);
        byte tid2 = (byte)((_nextTrackingId + 2) & 0x7F);
        WriteTouchData(buf, 46, 0, 0, false, tid2);
        byte tid3 = (byte)((_nextTrackingId + 3) & 0x7F);
        WriteTouchData(buf, 50, 0, 0, false, tid3);
        byte tid4 = (byte)((_nextTrackingId + 4) & 0x7F);
        WriteTouchData(buf, 54, 0, 0, false, tid4);
        byte tid5 = (byte)((_nextTrackingId + 5) & 0x7F);
        WriteTouchData(buf, 58, 0, 0, false, tid5);

        _previousTouchCounter = ds4CurrentCounter;
        _ds4TouchCounter++;

        ctrl.SubmitRawReport(buf);
        // Mousepad handled via SubmitMouseAndTouchpad (reads protobuf mouse fields)
        SubmitMouseAndTouchpad(input);
    }

    private void SubmitDualsenseReport(GamepadInput input)
    {
        var ctrl = (HMController)Controller!;
        var buf = new byte[63];

        buf[0] = (byte)((input.LeftStickX + 32768) / 256);
        buf[1] = (byte)((input.LeftStickY + 32768) / 256);
        buf[2] = (byte)((input.RightStickX + 32768) / 256);
        buf[3] = (byte)((input.RightStickY + 32768) / 256);

        var lt = input.LeftTrigger > 0 ? input.LeftTrigger : ((input.Buttons & (1 << 6)) != 0 ? 255u : 0u);
        var rt = input.RightTrigger > 0 ? input.RightTrigger : ((input.Buttons & (1 << 7)) != 0 ? 255u : 0u);
        buf[4] = (byte)Math.Clamp(lt, 0, 255);
        buf[5] = (byte)Math.Clamp(rt, 0, 255);

        buf[6] = _ds5SequenceCounter++;
        buf[7] = (byte)(input.Dpad & 0x0F) switch
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
        if ((input.Buttons & (1 << 2)) != 0) buf[7] |= 1 << 4; // Square
        if ((input.Buttons & (1 << 0)) != 0) buf[7] |= 1 << 5; // Cross
        if ((input.Buttons & (1 << 1)) != 0) buf[7] |= 1 << 6; // Circle
        if ((input.Buttons & (1 << 3)) != 0) buf[7] |= 1 << 7; // Triangle

        if ((input.Buttons & (1 << 4)) != 0) buf[8] |= 1 << 0; // L1
        if ((input.Buttons & (1 << 5)) != 0) buf[8] |= 1 << 1; // R1
        if ((input.Buttons & (1 << 6)) != 0) buf[8] |= 1 << 2; // L2
        if ((input.Buttons & (1 << 7)) != 0) buf[8] |= 1 << 3; // R2
        if ((input.Buttons & (1 << 8)) != 0) buf[8] |= 1 << 4; // Share
        if ((input.Buttons & (1 << 9)) != 0) buf[8] |= 1 << 5; // Options
        if ((input.Buttons & (1 << 10)) != 0) buf[8] |= 1 << 6; // L3
        if ((input.Buttons & (1 << 11)) != 0) buf[8] |= 1 << 7; // R3

        if ((input.Buttons & (1 << 16)) != 0) buf[9] |= 1 << 0; // PS
        if ((input.Buttons & (1 << 17)) != 0) buf[9] |= 1 << 1; // Touchpad

        if ((input.Buttons & (1u << 18)) != 0) buf[9] |= 1 << 2; // Mic Mute

        WriteInt16LE(buf, 15, (short)SoftLimit(input.GyroX, Ds4GyroScale, Ds4GyroKnee, Ds4GyroMaxInput, Ds4GyroMaxOutput));
        WriteInt16LE(buf, 17, (short)SoftLimit(input.GyroY, Ds4GyroScale, Ds4GyroKnee, Ds4GyroMaxInput, Ds4GyroMaxOutput));
        WriteInt16LE(buf, 19, (short)SoftLimit(input.GyroZ, Ds4GyroScale, Ds4GyroKnee, Ds4GyroMaxInput, Ds4GyroMaxOutput));

        WriteInt16LE(buf, 21, (short)SoftLimit(input.AccelX, Ds4AccelScale, Ds4AccelKnee, Ds4AccelMaxInput, Ds4AccelMaxOutput));
        WriteInt16LE(buf, 23, (short)SoftLimit(input.AccelY, Ds4AccelScale, Ds4AccelKnee, Ds4AccelMaxInput, Ds4AccelMaxOutput));
        WriteInt16LE(buf, 25, (short)SoftLimit(input.AccelZ, Ds4AccelScale, Ds4AccelKnee, Ds4AccelMaxInput, Ds4AccelMaxOutput));

        buf[27] = (byte)(_ds4Timestamp.ElapsedMilliseconds & 0xFF);
        buf[28] = (byte)((_ds4Timestamp.ElapsedMilliseconds >> 8) & 0xFF);
        buf[29] = (byte)((_ds4Timestamp.ElapsedMilliseconds >> 16) & 0xFF);
        buf[30] = (byte)((_ds4Timestamp.ElapsedMilliseconds >> 24) & 0xFF);
        buf[31] = 0x80; // no-touch placeholder for byte 32 (reserved/temp in real DS5); prevents phantom id=0 touch

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

        byte ds5CurrentCounter = (byte)(_ds4TouchCounter & 0x7F);
        byte ds5PreviousCounter = (byte)(_previousTouchCounter & 0x7F);

        int f0x = (int)(t0?.X ?? 0);
        int f0y = (int)(t0?.Y ?? 0);
        int f1x = (int)(t1?.X ?? 0);
        int f1y = (int)(t1?.Y ?? 0);
        bool f0Active = t0?.Active ?? false;
        bool f1Active = t1?.Active ?? false;

        buf[32] = f0Active
            ? (byte)(_touchIds[0] & 0x7F)
            : (byte)0x80;
        buf[33] = (byte)(Math.Clamp(f0x, 0, 1919) & 0xFF);
        buf[34] = (byte)(((Math.Clamp(f0x, 0, 1919) >> 8) & 0x0F) | ((Math.Clamp(f0y, 0, 1079) & 0x0F) << 4));
        buf[35] = (byte)(Math.Clamp(f0y, 0, 1079) >> 4);

        buf[36] = f1Active
            ? (byte)(_touchIds[1] & 0x7F)
            : (byte)0x80;
        buf[37] = (byte)(Math.Clamp(f1x, 0, 1919) & 0xFF);
        buf[38] = (byte)(((Math.Clamp(f1x, 0, 1919) >> 8) & 0x0F) | ((Math.Clamp(f1y, 0, 1079) & 0x0F) << 4));
        buf[39] = (byte)(Math.Clamp(f1y, 0, 1079) >> 4);
        buf[40] = ds5CurrentCounter;
        buf[41] = ds5PreviousCounter;

        _previousTouchCounter = ds5CurrentCounter;
        _ds4TouchCounter++;

        buf[52] = input.BatteryLevel > 0 ? (byte)Math.Clamp(input.BatteryLevel / 10u, 0u, 9u) : (byte)0x09;
        if (input.IsCharging) buf[52] |= 0x10;

        ctrl.SubmitRawReport(buf.AsSpan());

        // Mousepad handled via SubmitMouseAndTouchpad (reads protobuf mouse fields)
        SubmitMouseAndTouchpad(input);
    }

    /// <summary>Packs a 48-byte Switch Pro report-0x30 body into the shared
    /// input section; the USB/IP backend frames it and streams it at the
    /// 15 ms wire cadence.</summary>
    private void SubmitSwitchReport(GamepadInput input)
    {
        var ctrl = (HMController)Controller!;
        Span<byte> body = stackalloc byte[SwitchProInput.BodySize];
        SwitchProInput.BuildBody(input, body);
        ctrl.SubmitRawReport(body);
        SubmitMouseAndTouchpad(input);
    }

    /// <summary>Packs one 48-byte Switch report-0x30 body and feeds it to both
    /// Joy-Con halves. Each half is a separate USB HID device with its own
    /// device type, so eden reads only the buttons/stick belonging to that
    /// side; the shared body carries everything and each side ignores the
    /// other's bits.</summary>
    private void SubmitJoyConReport(GamepadInput input)
    {
        Span<byte> body = stackalloc byte[SwitchProInput.BodySize];
        SwitchProInput.BuildBody(input, body);
        if (Controller is HMController primary) primary.SubmitRawReport(body);
        if (_controllerSecondary is HMController secondary) secondary.SubmitRawReport(body);
        SubmitMouseAndTouchpad(input);
    }

    /// <summary>Forwards the frame's mouse movement / scroll / buttons to the
    /// virtual USB HID mouse on the USB/IP backend. The device is created
    /// lazily on the first frame that actually carries mouse activity.</summary>
    private void SubmitMouseAndTouchpad(GamepadInput input)
    {
        bool active = input.MouseDx != 0 || input.MouseDy != 0
                   || input.MouseWheel != 0 || input.MousePan != 0
                   || input.MouseButtons != 0;
        var kbMouse = _kbMouse;
        if (kbMouse == null && active)
            kbMouse = EnsureKeyboardMouse();
        if (kbMouse == null) return;

        kbMouse.SubmitMouse(input.MouseDx, input.MouseDy,
                            input.MouseWheel, input.MousePan, input.MouseButtons);
    }

    /// <summary>Forwards the frame's full keyboard state (modifier bitmask +
    /// HID key usages) to the virtual USB HID keyboard on the USB/IP backend.
    /// Typematic auto-repeat is produced by the OS keyboard driver from the
    /// held bitmap, so GKME no longer re-injects repeats itself.</summary>
    private void SubmitKeyboardInput(GamepadInput input)
    {
        bool active = input.PressedScanCodes.Count > 0 || input.KeyboardModifiers != 0;
        var kbMouse = _kbMouse;
        if (kbMouse == null && active)
            kbMouse = EnsureKeyboardMouse();
        if (kbMouse == null) return;

        kbMouse.SubmitKeyboard(input.KeyboardModifiers, input.PressedScanCodes);
    }

    /// <summary>Lazily creates the virtual USB HID keyboard/mouse pair. The
    /// first call starts a background create (a USB/IP attach is slow enough
    /// that doing it inline would stall the report loop); callers that arrive
    /// before it finishes simply drop that frame. Returns the live device, or
    /// null while the create is still in flight (or after it failed).</summary>
    private VirtualKeyboardMouse? EnsureKeyboardMouse()
    {
        if (!_hidMaestroAvailable) return null;

        lock (_kbMouseLock)
        {
            if (_kbMouse != null) return _kbMouse;
            if (_kbMouseCreating || _kbMouseFailed || _disposed) return null;
            _kbMouseCreating = true;
        }

        _ = Task.Run(() =>
        {
            VirtualKeyboardMouse created;
            try
            {
                created = new VirtualKeyboardMouse(_hmContext);
            }
            catch (UsbipRebootRequiredException)
            {
                MarkKeyboardMouseFailed();
                try { OnRebootRequired?.Invoke(this); } catch { }
                return;
            }
            catch (UsbipInstallRequiredException)
            {
                MarkKeyboardMouseFailed();
                try { OnUsbipInstallRequired?.Invoke(this); } catch { }
                return;
            }
            catch (Exception ex)
            {
                MarkKeyboardMouseFailed();
                System.Diagnostics.Debug.WriteLine(
                    $"[GamepadSession] create virtual keyboard/mouse failed: {ex.Message}");
                return;
            }

            bool orphaned;
            lock (_kbMouseLock)
            {
                orphaned = _disposed || _kbMouse != null;
                if (!orphaned) _kbMouse = created;
                _kbMouseCreating = false;
            }
            if (orphaned) created.Dispose();
        });

        return null;
    }

    private void MarkKeyboardMouseFailed()
    {
        lock (_kbMouseLock) { _kbMouseCreating = false; _kbMouseFailed = true; }
    }

    private static void WriteInt16LE(byte[] buf, int offset, short value)
    {
        buf[offset] = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
    }

    private static float SoftLimit(float input, float scale, float knee, float maxInput, float maxOutput)
    {
        float abs = MathF.Abs(input);
        if (abs <= knee)
            return input * scale;

        float sign = input < 0 ? -1f : 1f;
        if (abs >= maxInput)
            return sign * maxOutput;

        float atKnee = knee * scale;
        float slope = (maxOutput - atKnee) / (maxInput - knee);
        return sign * (atKnee + (abs - knee) * slope);
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

    private async Task SendCompactFrameAsync(
        byte largeMotor, byte smallMotor,
        byte[]? leftTrigger, byte[]? rightTrigger,
        bool sendLed = true, bool sendTestTone = false)
    {
        await SendAsync(new ServerToClient
        {
            CompactFrame = new global::GamepadEmu.Protocol.CompactFrame
            {
                Vibration = new Vibration
                {
                    LargeMotor = largeMotor,
                    SmallMotor = smallMotor,
                },
                TriggerEffects = (leftTrigger != null || rightTrigger != null)
                    ? new TriggerEffects
                    {
                        LeftTriggerEffect = ByteString.CopyFrom(leftTrigger ?? Array.Empty<byte>()),
                        RightTriggerEffect = ByteString.CopyFrom(rightTrigger ?? Array.Empty<byte>()),
                    }
                    : null,
                LedState = sendLed
                    ? new LedState
                    {
                        Color = (uint)_lastLedColor,
                        PlayerLed = (uint)_lastPlayerLed,
                    }
                    : null,
                TestTone = sendTestTone
                    ? new TestTone { Enabled = _testToneEnabled }
                    : null,
            }
        });
    }

    public async Task<ControllerMode> SetControllerModeAsync(ControllerMode mode)
    {
        if (_disposed || _switchingMode) return Mode;

        _switchingMode = true;
        try
        {
            // Let the previous switch's background teardown finish first.
            // The USB/IP dispose waits for the vhci port to be released; if
            // we created the new device before that, Windows could reuse the
            // dying port and graft the old composite's audio function onto
            // the new persona.
            var pendingDestroy = _pendingDestroy;
            if (pendingDestroy != null)
            {
                try { await pendingDestroy; } catch { }
                _pendingDestroy = null;
            }

            _lastLargeMotor = 0;
            _lastSmallMotor = 0;
            _lastLeftTriggerEffect = null;
            _lastRightTriggerEffect = null;
            ClearPendingAudio();
            _ = SendClearTriggerEffectsAsync();

            // 创建新控制器；成功前保留旧控制器。若创建失败，系统仍只有一个旧手柄，
            CreatedControllers newSet;
            try
            {
                newSet = await Task.Run(() => CreateControllers(mode));
            }
            catch (UsbipRebootRequiredException)
            {
                try { OnRebootRequired?.Invoke(this); } catch { }
                return Mode;
            }
            catch (UsbipInstallRequiredException)
            {
                try { OnUsbipInstallRequired?.Invoke(this); } catch { }
                return Mode;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[GamepadSession] create controller {mode} failed: {ex.Message}; keep current {Controller}");
                return Mode;
            }

            object? oldPrimary, oldSecondary;
            lock (_controllerLock)
            {
                if (_disposed)
                {
                    DestroyController(newSet.Primary);
                    DestroyController(newSet.Secondary);
                    return Mode;
                }
                oldPrimary = _controller;
                oldSecondary = _controllerSecondary;
                _controller = newSet.Primary;
                _controllerSecondary = newSet.Secondary;
                Mode = mode;
            }

            // 销毁旧控制器
            if ((oldPrimary != null && !ReferenceEquals(oldPrimary, newSet.Primary))
                || (oldSecondary != null && !ReferenceEquals(oldSecondary, newSet.Secondary)))
            {
                var toDestroyPrimary = oldPrimary;
                var toDestroySecondary = oldSecondary;
                _pendingDestroy = Task.Run(() =>
                {
                    DestroyControllerForMode(toDestroyPrimary);
                    DestroyControllerForMode(toDestroySecondary);
                });
            }

            _lastLargeMotor = 0;
            _lastSmallMotor = 0;
            _lastLedColor = 0;
            _lastPlayerLed = 0;
            _ = SendLedStateAsync();
            _ = SendTestToneStateAsync();

            if (!_readyFired)
            {
                _readyFired = true;
                OnReady?.Invoke(this);
            }

            return Mode;
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
        VirtualKeyboardMouse? kbMouse;
        lock (_kbMouseLock)
        {
            kbMouse = _kbMouse;
            _kbMouse = null;
        }
        ClearPendingAudio();
        _ = SendClearTriggerEffectsAsync();

        var primary = Controller;
        var secondary = _controllerSecondary;
        if (primary is HMController hmCtrl)
        {
            var ctrl = hmCtrl;
            var ctrlSecond = secondary as HMController;
            var priorDestroy = _pendingDestroy;
            _cleanupTask = Task.Run(async () =>
            {
                // Drain any teardown a mode switch left running so a caller
                // awaiting WaitForCleanupAsync sees the whole tree gone.
                if (priorDestroy != null)
                {
                    try { await priorDestroy; } catch { }
                }
                PreRemoveDevice(ctrl);
                try { ctrl.Dispose(); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[GamepadSession] dispose HM controller failed: {ex.Message}");
                }
                if (ctrlSecond != null)
                {
                    PreRemoveDevice(ctrlSecond);
                    try { ctrlSecond.Dispose(); }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[GamepadSession] dispose HM controller failed: {ex.Message}");
                    }
                }
            });
        }
        else
        {
            DestroyController(primary);
            DestroyController(secondary);
        }
        _controller = null;
        _controllerSecondary = null;

        // Tear the virtual keyboard/mouse down after the gamepad so the whole
        // tree is gone when WaitForCleanupAsync resumes.
        if (kbMouse != null)
        {
            var priorCleanup = _cleanupTask;
            _cleanupTask = Task.Run(async () =>
            {
                if (priorCleanup != null)
                {
                    try { await priorCleanup; } catch { }
                }
                try { kbMouse.Dispose(); } catch { }
            });
        }

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

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern uint CM_Get_Child(ref IntPtr pdnDevInst, IntPtr dnDevInst, uint ulFlags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern uint CM_Get_Sibling(ref IntPtr pdnDevInst, IntPtr dnDevInst, uint ulFlags);

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
            if (status != CR_SUCCESS || devnode == IntPtr.Zero)
            {
                // Already uninstalled
                status = CM_Locate_DevNodeW(ref devnode, instanceId, 1);
                if (status != CR_SUCCESS || devnode == IntPtr.Zero) return;
            }

            // Fast-remove the whole subtree (HID children first, then the root) —
            // the same disable+uninstall HIDMaestro's internal fast path performs,
            // so the later non-fast removal sweeps find nothing left to wait on.
            var nodes = new List<IntPtr>();
            CollectDescendants(devnode, nodes);
            nodes.Add(devnode);
            foreach (var node in nodes)
            {
                CM_Disable_DevNode(node, DN_DISABLE_ASYNC);
                CM_Uninstall_DevNode(node, 0);
            }
        }
        catch { }
    }

    private static void CollectDescendants(IntPtr parent, List<IntPtr> nodes)
    {
        IntPtr child = IntPtr.Zero;
        if (CM_Get_Child(ref child, parent, 0) != CR_SUCCESS) return;
        do
        {
            CollectDescendants(child, nodes);
            nodes.Add(child);
        }
        while (CM_Get_Sibling(ref child, child, 0) == CR_SUCCESS);
    }

    private static void EnsureReflectionCache(HMController ctrl)
    {
        if (_instanceIdField != null) return;
        lock (_preRemoveLock)
        {
            if (_instanceIdField != null) return;
#pragma warning disable IL2072
            _instanceIdField = ctrl.GetType().GetField("<InstanceId>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);
#pragma warning restore IL2072
        }
    }

    private void ResetController()
    {
        if (Controller == null) return;
        try
        {
            var neutral = new GamepadInput();
            switch (Mode)
            {
                case ControllerMode.Xbox360:
                    SubmitXbox360Report(neutral);
                    break;
                case ControllerMode.Dualsense:
                    SubmitDualsenseReport(neutral);
                    break;
                case ControllerMode.SwitchPro:
                    SubmitSwitchReport(neutral);
                    break;
                case ControllerMode.JoyCon:
                    SubmitJoyConReport(neutral);
                    break;
                default:
                    SubmitDs4Report(neutral);
                    break;
            }
        }
        catch { }

        // Release any keys / mouse buttons the virtual HID devices may still be
        // holding, so a dropped link can't leave the host with a stuck key.
        VirtualKeyboardMouse? kbMouse;
        lock (_kbMouseLock) kbMouse = _kbMouse;
        try { kbMouse?.ReleaseAll(); } catch { }
    }

    private static void DestroyControllerForMode(object? ctrl)
    {
        if (ctrl == null) return;
        try
        {
            if (ctrl is HMController hm) PreRemoveDevice(hm);
            DestroyController(ctrl);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[GamepadSession] destroy old controller failed: {ex.Message}");
        }
    }

    private static void DestroyController(object? ctrl)
    {
        try
        {
            if (ctrl is HMController hm) hm.Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GamepadSession] destroy {ctrl?.GetType().Name} failed: {ex.Message}");
        }
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