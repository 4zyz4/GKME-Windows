using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using GamepadEmu.Protocol;
using Google.Protobuf;
using HIDMaestro;

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
    private readonly HMContext _hmContext;
    private readonly bool _hidMaestroAvailable;
    private readonly object _controllerLock = new();
    private CancellationTokenSource _cts = new();
    private volatile bool _disposed;
    private bool _readyFired;
    private DateTime _lastReceiveTime = DateTime.UtcNow;
    private byte _lastLargeMotor;
    private byte _lastSmallMotor;
    private readonly ConcurrentQueue<GamepadInput> _inputQueue = new();

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
    private uint _audioFrameIndex;
    private bool _audioFormatReady;
    private uint _audioSampleRate;
    private byte _audioChannels;
    private byte _audioBitsPerSample;

    // 4-channel PCM buffer for test tone. The phone side (dualsense-
    // tester) expects 48 kHz / 4ch / 16-bit 2-byte PCM samples on
    // channels 1/2 for the speaker and channels 3/4 for haptics.
    // Pre-chunked into 10 ms UDP frames to tolerate network jitter.
    private readonly List<byte[]> _toneFrames = new();
    private int _toneFrameIdx;
    private bool _toneRunning;
    private double _tonePhase;              // 0..2π accumulator

    private const double ToneAmplitude = 0.25;
    private const int ToneSampleRate = 48000;
    private const int ToneChannels = 4;
    private const int ToneBitsPerSample = 16;
    private const double ToneFreq = 1000.0;
    private const double ToneStep = 2.0 * Math.PI * ToneFreq / ToneSampleRate;

    // Tone drain: 10 ms frames to tolerate network jitter.
    private const int ToneFrameMs = 10;
    private const int ToneFrameSamples = (ToneSampleRate * ToneFrameMs) / 1000;
    private const int ToneFrameBytes = ToneFrameSamples * ToneChannels * (ToneBitsPerSample / 8);

    // Telemetry for the driver-delivered PCM stream (game → speaker).
    private long _audioReceivedBytes;
    private long _audioDrainedBytes;
    private int _audioWindowsSeen;
    private int _lastAudioWindowBytes;

    public DiscoveredDevice Device { get; }
    public ControllerMode Mode { get; private set; }
    private volatile object? _controller;
    public object? Controller => _controller;
    public bool IsConnected => !_disposed;
    public bool IsReady => Controller != null;
    public bool HasBeenReady => _readyFired;

    public event Action<GamepadSession>? OnReady;
    public event Action<GamepadSession>? OnConnectionLost;
    public event Action<GamepadSession>? OnDisconnected;

    public GamepadSession(DiscoveredDevice device, Func<IMessage, Task> sendToPhone, HMContext hmContext, bool hidMaestroAvailable = true, ControllerMode initialMode = ControllerMode.Ds4)
    {
        Device = device;
        _sendToPhone = sendToPhone;
        _hmContext = hmContext;
        _hidMaestroAvailable = hidMaestroAvailable;
        Mode = ResolveMode(initialMode);
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
    }

    /// <summary>Downstream loop — ~125 Hz.  Sends vibration echoes + RTT
    /// telemetry to the phone at a steady rate regardless of how fast
    /// the phone pushes input to us.  Also drains the input queue so no
    /// phone report is left unsubmitted.</summary>
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

            // Periodic audio-flow diagnostics (~every 500 ms).
            if (sw.Elapsed.Ticks - lastStatTicks > TimeSpan.TicksPerMillisecond * 500)
            {
                lastStatTicks = sw.Elapsed.Ticks;
                _audioReceivedBytes = 0;
                _audioDrainedBytes = 0;
                _audioWindowsSeen = 0;
            }

            // Drain pending test tone — sends PCM to the phone on the same
            // 125 Hz cadence the phone expects.
            DrainPendingTone();

            // Drain the input queue — submit every arrived report to the
            // emulated controller so no phone input is silently dropped.
            while (_inputQueue.TryDequeue(out var input) && Controller != null)
            {
                try
                {
                    switch (Mode)
                    {
                        case ControllerMode.Ds4:
                            SubmitDs4Report(input);
                            break;
                        case ControllerMode.SwitchPro:
                            SubmitDualsenseReport(input);
                            break;
                        case ControllerMode.Dualsense:
                            SubmitDualsenseReport(input);
                            break;
                        default:
                            SubmitXbox360Report(input);
                            break;
                    }
                }
                catch { }
            }

            // Send vibration echo back to phone at fixed 125 Hz regardless of input.
            if (Controller != null)
            {
                _ = SendVibrationAsync(_lastLargeMotor, _lastSmallMotor);
            }

            // Precise timing: keep 125 Hz regardless of how much work we did.
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

    public async Task SendServerHelloAsync()
    {
        await SendAsync(NetworkService.BuildServerHello());
    }

    private void HandleHello(Hello hello)
    {
        if (_disposed) return;

        if (_controller == null && !_switchingMode)
        {
            lock (_controllerLock)
            {
                if (_controller == null && !_switchingMode)
                {
                    try
                    {
                        _controller = CreateController(Mode);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[GamepadSession] create controller {Mode} failed: {ex.Message}");
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
    }

    private object? CreateController(ControllerMode mode)
    {
        // All controllers are presented through the USB/IP backend. Pick the
        // matching composite profile; the Xbox 360 wired persona is a
        // vendor-class device, the others are HID composite personas.
        string profileId = mode switch
        {
            ControllerMode.Xbox360 => "xbox360-composite",
            ControllerMode.Dualsense => "dualsense-composite",
            _ => "dualshock-4-v2-composite",
        };

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
        // [2]!=0 = enable internal 1 kHz sine-wave generator on the phone side.
        // We synthesise 1 kHz PCM and forward it to the phone through the
        // normal audio lane so dualsense-tester can verify speaker output.
        byte deviceId = d[0];
        byte actionId = d[1];
        if (deviceId != 6 || actionId != 2) return;

        if (d[2] != 0)
        {
            ArmTestTone();
        }
        else
        {
            StopTestTone();
        }
    }

    /// <summary>Pre-generate a 1 kHz sine-wave PCM buffer (2 seconds of
    /// continuous tone, already split into 10 ms frames) and prime the
    /// drain cycle.  Phase continues from its previous value so repeated
    /// arming stays sample-continuous.</summary>
    private void ArmTestTone()
    {
        if (_toneRunning) return;
        _toneRunning = true;

        // Initialise audio format if not yet known.
        if (!_audioFormatReady)
        {
            _audioSampleRate = ToneSampleRate;
            _audioChannels = ToneChannels;
            _audioBitsPerSample = ToneBitsPerSample;
            _audioFormatReady = true;
        }

        if (_toneFrames.Count == 0)
        {
            GenerateToneFrames();
        }

        _toneFrameIdx = 0;
        DrainPendingTone();
    }

    private void StopTestTone()
    {
        _toneRunning = false;
        _toneFrameIdx = 0;
        _toneFrames.Clear();
    }

    /// <summary>Generate 2 seconds of 1 kHz PCM at 48 kHz / 4 ch / 16 bit
    /// and split into 10 ms frames stored in _toneFrames.</summary>
    private void GenerateToneFrames()
    {
        int totalSamples = ToneSampleRate * 2; // 2 seconds
        int samplesPerFrame = ToneFrameSamples;
        int bytesPerSample = ToneChannels * (ToneBitsPerSample / 8); // 8 bytes per sample
        int frameCount = totalSamples / samplesPerFrame;

        for (int f = 0; f < frameCount; f++)
        {
            var frameBytes = new byte[ToneFrameBytes];
            int startSample = f * samplesPerFrame;
            int bo = 0;
            for (int s = 0; s < samplesPerFrame; s++)
            {
                int i = startSample + s;
                double angle = _tonePhase + i * ToneStep;
                short v = (short)(Math.Sin(angle) * short.MaxValue * ToneAmplitude);

                // interleaved 4ch: ch0=silent, ch1=speaker, ch2=speaker, ch3=silent
                frameBytes[bo    ] = 0;                                          // ch0 LSB
                frameBytes[bo + 1] = 0;                                          // ch0 MSB
                frameBytes[bo + 2] = (byte)(v & 0xFF);                           // ch1 LSB
                frameBytes[bo + 3] = (byte)((v >> 8) & 0xFF);                    // ch1 MSB
                frameBytes[bo + 4] = (byte)(v & 0xFF);                           // ch2 LSB
                frameBytes[bo + 5] = (byte)((v >> 8) & 0xFF);                    // ch2 MSB
                frameBytes[bo + 6] = 0;                                          // ch3 LSB
                frameBytes[bo + 7] = 0;                                          // ch3 MSB

                bo += bytesPerSample;
            }
            _toneFrames.Add(frameBytes);
        }
        // Advance phase so next arm restart is sample-continuous.
        _tonePhase += totalSamples * ToneStep;
        if (_tonePhase >= 2.0 * Math.PI)
            _tonePhase -= 2.0 * Math.PI;
    }

    /// <summary>Drain one 10 ms tone frame through DrainSingleChunk so the
    /// phone receives them as a continuous audio stream.  Called from the
    /// 125 Hz ReportLoop.  When frame index wraps around, re-generate the
    /// buffer with fresh phase.</summary>
    private void DrainPendingTone()
    {
        if (!_toneRunning || _toneFrames.Count == 0) return;

        var chunk = _toneFrames[_toneFrameIdx];
        _toneFrameIdx++;
        DrainSingleChunk(chunk);

        if (_toneFrameIdx >= _toneFrames.Count)
        {
            _toneFrameIdx = 0;
            _toneFrames.Clear();
            GenerateToneFrames();
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

    // Target sample rate for phone playback (universally supported on Android)
    private const int TargetSampleRate = 44100;

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

            DrainSingleChunk(dst);
            _audioReceivedBytes += span.Length;
            _audioWindowsSeen++;
            _lastAudioWindowBytes = span.Length;
        }
        catch { }
    }

    /// <summary>Split an already-collected PCM buffer into frame-aligned
    /// UDP chunks and send them. Called by OnDualsenseAudioFrames and
    /// DrainPendingTone.</summary>
    private void DrainSingleChunk(byte[] pcm)
    {
        if (!_audioFormatReady) return;

        int bytesPerFrame = _audioChannels * (_audioBitsPerSample / 8);
        if (bytesPerFrame <= 0) return;

        int completeBytes = (pcm.Length / bytesPerFrame) * bytesPerFrame;
        if (completeBytes == 0) return;

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
        _toneRunning = false;
        _toneFrameIdx = 0;
        _toneFrames.Clear();
        _audioFormatReady = false;
    }

    private void HandleGamepadInput(GamepadInput input)
    {
        if (Controller == null) return;
        _inputQueue.Enqueue(input);
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
        short ly = (short)(-input.LeftStickY);
        short rx = (short)input.RightStickX;
        short ry = (short)(-input.RightStickY);
        buf[6] = (byte)(lx & 0xFF); buf[7] = (byte)((lx >> 8) & 0xFF);
        buf[8] = (byte)(ly & 0xFF); buf[9] = (byte)((ly >> 8) & 0xFF);
        buf[10] = (byte)(rx & 0xFF); buf[11] = (byte)((rx >> 8) & 0xFF);
        buf[12] = (byte)(ry & 0xFF); buf[13] = (byte)((ry >> 8) & 0xFF);
        // 14..19 reserved (zero)

        ctrl.SubmitRawReport(buf.AsSpan());
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

    private ControllerMode ResolveMode(ControllerMode mode)
    {
        // Switch Pro simulation is dropped; route it to the DualSense persona
        // so the device still presents a usable gamepad. Every persona now
        // rides the USB/IP backend, which is always available once the bundled
        // transport is installed.
        if (mode == ControllerMode.SwitchPro) return ControllerMode.Dualsense;
        return mode;
    }

    public async Task<ControllerMode> SetControllerModeAsync(ControllerMode mode)
    {
        if (_disposed || _switchingMode) return Mode;

        mode = ResolveMode(mode);

        _switchingMode = true;
        try
        {
            _lastLargeMotor = 0;
            _lastSmallMotor = 0;
            ClearPendingAudio();

            // 创建新控制器；成功前保留旧控制器。若创建失败，系统仍只有一个旧手柄，
            // 不会出现 "多出一个 DS4" 或切换后无手柄的状态。
            object? newCtrl;
            try
            {
                newCtrl = await Task.Run(() => CreateController(mode));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[GamepadSession] create controller {mode} failed: {ex.Message}; keep current {Controller}");
                return Mode;
            }

            object? oldCtrl;
            lock (_controllerLock)
            {
                if (_disposed)
                {
                    DestroyController(newCtrl);
                    return Mode;
                }
                oldCtrl = _controller;
                _controller = newCtrl;
                Mode = mode;
            }

            // 销毁旧控制器（HIDMaestro teardown 慢，放后台）；ViGEm 控制器销毁快。
            if (oldCtrl != null && !ReferenceEquals(oldCtrl, newCtrl))
            {
                var toDestroy = oldCtrl;
                _ = Task.Run(() =>
                {
                    try
                    {
                        if (toDestroy is HMController hm)
                            PreRemoveDevice(hm);
                        DestroyController(toDestroy);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[GamepadSession] destroy old controller failed: {ex.Message}");
                    }
                });
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
                try { ctrl.Dispose(); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[GamepadSession] dispose HM controller failed: {ex.Message}");
                }
            });
        }
        else
        {
            DestroyController(Controller);
        }
        _controller = null;

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
                default:
                    SubmitDs4Report(neutral);
                    break;
            }
        }
        catch { }
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