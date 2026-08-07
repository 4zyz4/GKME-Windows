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
    private readonly ViGEmClient _viGEm;
    private readonly HMContext _hmContext;
    private readonly bool _hidMaestroAvailable;
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
    private readonly System.Diagnostics.Stopwatch _ds4Timestamp = System.Diagnostics.Stopwatch.StartNew();
    private readonly byte[] _touchIds = new byte[2];
    private readonly bool[] _prevTouchActive = new bool[2];
    private byte _nextTrackingId;
    private byte _ds5SequenceCounter;
    private Task? _cleanupTask;

    // 4-channel audio forwarding (DualSense): the phone needs the raw
    // speaker + voice-coil PCM, not just the rumble peaks folded above.
    private readonly object _audioLock = new();
    private readonly List<byte[]> _audioPending = new();
    private byte[] _audioTail = Array.Empty<byte>();
    private uint _audioFrameIndex;
    private bool _audioFormatReady;
    private uint _audioSampleRate;
    private byte _audioChannels;
    private byte _audioBitsPerSample;

    // Sony test-command tone (dualsense-tester's "1kHz sine wave" button):
    // the tester writes feature report 0x80 (WAVEOUT_CTRL) instead of
    // streaming UAC audio, so we synthesize the tone here and forward it
    // through the normal audio lane. _toneActive is set on the output
    // polling thread; the phase accumulator is only touched by the
    // ReportLoop thread inside DrainAudio.
    private volatile bool _toneActive;
    private double _tonePhase;

    // Audio telemetry (diagnostic only): how many PCM bytes arrived from
    // the UAC endpoint and how many were drained to the phone, plus the
    // last seen window size. Helps distinguish "game never sends audio to
    // the endpoint" from "data flows but the phone doesn't play it".
    private long _audioReceivedBytes;
    private long _audioDrainedBytes;
    private int _lastAudioWindowBytes;
    private int _audioWindowsSeen;

    public DiscoveredDevice Device { get; }
    public ControllerMode Mode { get; private set; }
    public object? Controller { get; private set; }
    public bool IsConnected => !_disposed;
    public bool IsReady => Controller != null;
    public bool HasBeenReady => _readyFired;

    public event Action<GamepadSession>? OnReady;
    public event Action<GamepadSession>? OnConnectionLost;
    public event Action<GamepadSession>? OnDisconnected;

    public GamepadSession(DiscoveredDevice device, Func<IMessage, Task> sendToPhone, ViGEmClient viGEm, HMContext hmContext, bool hidMaestroAvailable = true)
    {
        Device = device;
        _sendToPhone = sendToPhone;
        _viGEm = viGEm;
        _hmContext = hmContext;
        _hidMaestroAvailable = hidMaestroAvailable;
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
            case ControllerMode.Dualsense:
            {
                var profile = _hmContext.GetProfile("dualsense-composite")
                    ?? throw new InvalidOperationException($"No HIDMaestro profile found for mode {mode}");

                var ctrl = _hmContext.CreateController(profile);
                ctrl.OutputDecoded += OnHmOutputDecoded;
                ctrl.OutputReceived += OnHmOutputReceived;
                Controller = ctrl;
                AttachDualsenseAudio(ctrl);
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

        _lastLargeMotor = d[2];
        _lastSmallMotor = d[3];
    }

    private void HandleDualsenseTestCommand(ReadOnlySpan<byte> d)
    {
        if (Mode != ControllerMode.Dualsense) return;
        if (d.Length < 3) return;

        // setTestCommandWithParams layout: [0]=deviceId, [1]=actionId,
        // [2..]=params. AUDIO device (6) + WAVEOUT_CTRL (2) controls the
        // internal 1 kHz sine-wave generator; params[0] != 0 starts it.
        byte deviceId = d[0];
        byte actionId = d[1];
        if (deviceId != 6 || actionId != 2) return;

        bool enable = d[2] != 0;
        if (enable)
        {
            // (Re)arm the tone. Phase keeps its previous value so a
            // restart stays sample-continuous.
            _toneActive = true;
            if (!_audioFormatReady)
            {
                _audioSampleRate = 48000;
                _audioChannels = 4;
                _audioBitsPerSample = 16;
                _audioFormatReady = true;
            }
        }
        else
        {
            _toneActive = false;
            lock (_audioLock)
            {
                _audioPending.Clear();
            }
            _audioTail = Array.Empty<byte>();
        }
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

    private void OnDualsenseAudioFrames(HMAudioOutput output, ReadOnlyMemory<byte> pcm)
    {
        try
        {
            int channels = output.Channels;
            if (channels < 4 || pcm.IsEmpty) return;

            var span = pcm.Span;
            // Real-time: send the full 4-channel packet directly without
            // any downmix or re-ordering. The phone will pick lanes it needs.
            if (!_audioFormatReady)
            {
                _audioSampleRate = (uint)output.SampleRateHz;
                _audioChannels = (byte)channels;
                _audioBitsPerSample = (byte)output.BitsPerSample;
                _audioFormatReady = true;
                System.Diagnostics.Debug.WriteLine(
                    $"[DualSenseAudio] stream opened: {output.SampleRateHz}Hz {channels}ch {output.BitsPerSample}bit");
            }

            DrainSingleChunk(span.ToArray());
            _audioReceivedBytes += span.Length;
            _audioWindowsSeen++;
            _lastAudioWindowBytes = span.Length;
        }
        catch { }
    }

    /// <summary>One drain window of a synthesized 1 kHz sine wave,
    /// 16-bit LE interleaved on 4 channels (ch0=silent, ch1=ch2=speaker, ch3=haptic silent).
    /// 48000 / 125 = 384 samples per tick.</summary>
    private byte[] BuildToneWindow()
    {
        const int sampleRate = 48000;
        const int channels = 4;
        const int bytesPerSample = 2;
        const int samples = sampleRate / 125;
        const double amplitude = 0.25;
        const double step = 2.0 * Math.PI * 1000.0 / sampleRate;

        var buf = new byte[samples * channels * bytesPerSample];
        for (int i = 0; i < samples; i++)
        {
            _tonePhase += step;
            if (_tonePhase >= 2.0 * Math.PI) _tonePhase -= 2.0 * Math.PI;
            short v = (short)(Math.Sin(_tonePhase) * 32767.0 * amplitude);
            int o = i * channels * bytesPerSample;
            buf[o] = 0; buf[o + 1] = 0;
            buf[o + 2] = (byte)(v & 0xFF);
            buf[o + 3] = (byte)((v >> 8) & 0xFF);
            buf[o + 4] = (byte)(v & 0xFF);
            buf[o + 5] = (byte)((v >> 8) & 0xFF);
            buf[o + 6] = 0;
            buf[o + 7] = 0;
        }
        return buf;
    }

    /// <summary>Split an already-collected PCM buffer into frame-aligned
    /// UDP chunks and send them. Called both by the old drain loop and
    /// the new real-time path.</summary>
    private void DrainSingleChunk(byte[] pcm)
    {
        if (!_audioFormatReady) return;

        int bytesPerFrame = _audioChannels * (_audioBitsPerSample / 8);
        if (bytesPerFrame <= 0) return;

        int completeBytes = (pcm.Length / bytesPerFrame) * bytesPerFrame;
        if (completeBytes == 0) return;

        const int MaxPacketBytes = 4096;
        int chunkBytes = (MaxPacketBytes / bytesPerFrame) * bytesPerFrame;

        // Update tail so the caller's real-time path can keep partial frames.
        // Here we just handle complete frames.

        for (int sent = 0; sent < completeBytes; sent += chunkBytes)
        {
            int n = Math.Min(chunkBytes, completeBytes - sent);
            var bytes = new byte[n];
            Buffer.BlockCopy(pcm, sent, bytes, 0, n);
            _ = SendAudioFrameAsync(bytes);
        }
        _audioDrainedBytes += completeBytes;
    }

    private void DrainAudio()
    {
        if (Mode != ControllerMode.Dualsense) return;

        // Synthesize the test tone (1 kHz, speaker L/R, haptics silent)
        // when the tester armed it. One window per 125 Hz tick keeps the
        // sample cadence aligned with the drain rate.
        if (_toneActive)
        {
            if (!_audioFormatReady)
            {
                _audioSampleRate = 48000;
                _audioChannels = 4;
                _audioBitsPerSample = 16;
                _audioFormatReady = true;
            }
            lock (_audioLock)
            {
                if (_audioPending.Count < 1024)
                    _audioPending.Add(BuildToneWindow());
            }
        }

        if (!_audioFormatReady) return;

        byte[][] windows;
        lock (_audioLock)
        {
            if (_audioPending.Count == 0) return;
            windows = _audioPending.ToArray();
            _audioPending.Clear();
        }

        int bytesPerFrame = _audioChannels * (_audioBitsPerSample / 8);
        if (bytesPerFrame <= 0) return;

        int total = _audioTail.Length;
        foreach (var w in windows) total += w.Length;
        if (total == 0) return;

        var merged = new byte[total];
        int off = 0;
        if (_audioTail.Length > 0)
        {
            _audioTail.CopyTo(merged, 0);
            off = _audioTail.Length;
        }
        foreach (var w in windows)
        {
            w.CopyTo(merged, off);
            off += w.Length;
        }

        int completeBytes = (total / bytesPerFrame) * bytesPerFrame;
        int remainder = total - completeBytes;
        if (remainder > 0)
            _audioTail = merged.AsSpan(completeBytes).ToArray();
        else
            _audioTail = Array.Empty<byte>();

        if (completeBytes == 0) return;

        // Forward to DrainSingleChunk which splits into UDP chunks.
        DrainSingleChunk(merged.AsSpan(0, completeBytes).ToArray());
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
        lock (_audioLock)
        {
            _audioPending.Clear();
        }
        _audioTail = Array.Empty<byte>();
        _audioFrameIndex = 0;
        _toneActive = false;
        _tonePhase = 0;
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
                _touchIds[i] = _nextTrackingId++;
            _prevTouchActive[i] = isActive;
        }

        byte ds4CurrentCounter = (byte)(_ds4TouchCounter & 0x7F);
        buf[32] = 1;
        buf[33] = ds4CurrentCounter;
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

        _previousTouchCounter = ds4CurrentCounter;
        _ds4TouchCounter++;

        ctrl.SubmitRawReport(buf);
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

        buf[10] = 0x0F;

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
        WriteTouchData(buf, 32,
            (int)(t0?.X ?? 0), (int)(t0?.Y ?? 0),
            t0?.Active ?? false, (byte)(_touchIds[0] & 0x7F));
        WriteTouchData(buf, 36,
            (int)(t1?.X ?? 0), (int)(t1?.Y ?? 0),
            t1?.Active ?? false, (byte)(_touchIds[1] & 0x7F));
        buf[40] = ds5CurrentCounter;
        buf[41] = ds5PreviousCounter;

        _previousTouchCounter = ds5CurrentCounter;
        _ds4TouchCounter++;

        buf[52] = (byte)Math.Clamp(input.BatteryLevel / 10u, 0u, 10u);
        if (input.IsCharging) buf[52] |= 0x10;

        ctrl.SubmitRawReport(buf.AsSpan());
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
        long lastStatTicks = 0;

        while (!ct.IsCancellationRequested)
        {
            if (_disposed) return;

            DrainAudio();

            // Periodic audio-flow diagnostics (~every 500 ms). Received = PCM
            // the UAC endpoint delivered; drained = PCM forwarded to the phone.
            if (sw.Elapsed.Ticks - lastStatTicks > TimeSpan.TicksPerMillisecond * 500)
            {
                lastStatTicks = sw.Elapsed.Ticks;
                System.Diagnostics.Debug.WriteLine(
                    $"[DualSenseAudio] windows={_audioWindowsSeen} lastWindow={_lastAudioWindowBytes}B " +
                    $"received={_audioReceivedBytes}B drained={_audioDrainedBytes}B tone={_toneActive}");
                _audioReceivedBytes = 0;
                _audioDrainedBytes = 0;
                _audioWindowsSeen = 0;
            }

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
                    else if (Mode == ControllerMode.Dualsense)
                        SubmitDualsenseReport(report);
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

    public async Task<ControllerMode> SetControllerModeAsync(ControllerMode mode)
    {
        if (_disposed || _switchingMode) return Mode;

        bool wantsHm = mode is ControllerMode.SwitchPro or ControllerMode.Dualsense;
        if (wantsHm && !_hidMaestroAvailable)
        {
            mode = ControllerMode.Ds4;
        }

        _switchingMode = true;
        try
        {
            _lastLargeMotor = 0;
            _lastSmallMotor = 0;
            _lastReportedInput = null;
            ClearPendingAudio();

            var oldCtrl = Controller;
            var oldMode = Mode;

            bool oldIsHm = oldMode is ControllerMode.SwitchPro or ControllerMode.Dualsense;
            bool newIsHm = mode is ControllerMode.SwitchPro or ControllerMode.Dualsense;

            if (oldCtrl != null && oldIsHm)
            {
                // HIDMaestro teardown is slow (~5s). Create new first, destroy old in background.
                Controller = null;
                if (newIsHm)
                {
                    // HIDMaestro → HIDMaestro: recreate via background
                    var newCtrl = await Task.Run(() =>
                    {
                        CreateController(mode);
                        return Controller;
                    });
                    Controller = newCtrl;
                }
                else
                {
                    // HIDMaestro → DS4/Xbox: ViGEm is fast, create inline
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

                if (newIsHm)
                {
                    // ViGEm → HIDMaestro: HIDMaestro creation is slow
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
        ClearPendingAudio();

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
                // Already uninstalled; pick up the phantom node so HIDMaestro's
                // non-fast teardown sweep doesn't wait on stale registry residue.
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
