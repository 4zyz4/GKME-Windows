using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using GamepadEmu.Protocol;
using Google.Protobuf;
using GKMD;
using GKMD.Internal.Usbip;
using GKME.Core.Personas;

namespace GKME.Core;

public class GamepadSession : IDisposable
{
    private const int ConnectionTimeoutMs = 3000;

    private readonly Func<IMessage, Task> _sendToPhone;
    private readonly Action _onAudioSendStart;
    private readonly GKContext _gkContext;
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
    // Xbox One impulse-trigger rumble amplitudes, normalized to 0-255 for the
    // phone's rumble lane (the GIP wire value is a 0-100 percentage). Forwarded
    // in the TriggerEffects message (separate from the DualSense effect blocks).
    private byte _lastLeftTriggerRumble;
    private byte _lastRightTriggerRumble;

    private readonly ManualResetEventSlim _inputReceived = new(false, 1);
    // A queue (not a single overwritten slot) so that a fast press+release in
    // the same processing window is never collapsed to just the last state —
    // which would drop the press entirely.
    private readonly System.Collections.Concurrent.ConcurrentQueue<GamepadInput> _inputQueue = new();

    private volatile bool _switchingMode;
    // Tracks the background teardown of the controllers a previous switch
    // retired. The next switch awaits it before creating replacements, so a
    // new USB/IP persona never plugs in while the old one is still being
    // removed (otherwise Windows can carry the old audio function over).
    private Task? _pendingDestroy;
    private readonly Stopwatch _reportClock = Stopwatch.StartNew();
    private Task? _cleanupTask;
    // Virtual USB HID keyboard + mouse (USB/IP backend). Created when the
    // session connects and kept for the session's lifetime so mode switches
    // never tear them down.
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

    // The wire format currently spoken. Replaced together with Mode and the
    // controllers on a mode switch; every persona-specific branch in the
    // session goes through it.
    private volatile IControllerPersona _persona;
    private readonly PersonaHost _personaHost;

    /// <summary>True when the emulated controller reports trigger rumble instead of
    /// adaptive-trigger effects (currently Xbox One / Series), so compact frames carry the
    /// trigger-rumble fields rather than the DualSense effect blocks.</summary>
    private bool SendsTriggerRumble => _persona.SendsTriggerRumble;
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

    public GamepadSession(DiscoveredDevice device, Func<IMessage, Task> sendToPhone, Action onAudioSendStart, GKContext gkContext, bool hidMaestroAvailable = true, ControllerMode initialMode = ControllerMode.Ds4)
    {
        Device = device;
        _sendToPhone = sendToPhone;
        _onAudioSendStart = onAudioSendStart;
        _gkContext = gkContext;
        _hidMaestroAvailable = hidMaestroAvailable;
        Mode = initialMode;
        _persona = PersonaCatalog.Create(initialMode);
        _personaHost = new PersonaHost(this);
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

            // Input arrived — drain ALL queued inputs immediately, in order,
            // with no rate limit and no dropped transitions.
            _inputReceived.Reset();
            while (Controller != null && _inputQueue.TryDequeue(out var input))
            {
                try
                {
                    _persona.WriteInput(input, _personaHost);
                    SubmitMouseAndTouchpad(input);
                }
                catch { }

                try { SubmitKeyboardInput(input); } catch { }
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
        _inputQueue.Enqueue(input);
        _inputReceived.Set();
    }

    /// <summary>收包线程直调入口（无速率限制，入队后立即唤醒上报线程）。</summary>
    public void ReportGamepadInput(GamepadInput input)
    {
        _lastReceiveTime = DateTime.UtcNow;
        if (!IsReady) return;
        _inputQueue.Enqueue(input);
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
                        var created = CreateControllers(_persona);
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

        // Create the virtual keyboard/mouse as part of bringing the session up
        // (rather than lazily on the first mouse/keyboard frame), so the USB/IP
        // devices are attached and ready before the user interacts.
        _ = EnsureKeyboardMouse();

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

    /// <summary>Creates the controller(s) a persona needs. Every persona is a
    /// single USB/IP composite device except dual Joy-Con, which presents a real
    /// left (057E:2006) and right (057E:2007) half as two independent USB
    /// devices so eden/SDL sees a genuine pair.</summary>
    private CreatedControllers CreateControllers(IControllerPersona persona)
    {
        var primary = CreateController(persona.PrimaryProfileId, persona);
        if (persona.SecondaryProfileId is not { } secondaryProfileId)
            return new CreatedControllers(primary, null);

        try
        {
            return new CreatedControllers(primary, CreateController(secondaryProfileId, persona));
        }
        catch
        {
            DestroyController(primary);
            throw;
        }
    }

    private object CreateController(string profileId, IControllerPersona persona)
    {
        var profile = _gkContext.GetProfile(profileId)
            ?? throw new InvalidOperationException($"No GKMD profile found for mode {persona.Mode} (profile '{profileId}').");

        var ctrl = _gkContext.CreateController(profile);
        ctrl.OutputDecoded += OnGkOutputDecoded;
        ctrl.OutputReceived += OnGkOutputReceived;
        if (persona.AudioLayout == PersonaAudioLayout.DualsenseFourChannel)
            AttachDualsenseAudio(ctrl);
        else if (persona.AudioLayout == PersonaAudioLayout.Ds4Stereo)
            AttachDs4Audio(ctrl);
        return ctrl;
    }

    private void OnGkOutputDecoded(object? sender, GKOutputDecodedEventArgs e)
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

    /// <summary>Forwards the emulated controller's LED state to the phone. Personas
    /// without an LED (Xbox) always report #000000 / no player indicator.</summary>
    private async Task SendLedStateAsync()
    {
        bool noLed = !_persona.HasLed;
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

    /// <summary>Forwards Xbox One impulse-trigger rumble amplitudes (0-255) to the
    /// phone. Sent in the same TriggerEffects envelope as DualSense adaptive effects
    /// but in dedicated fields, so the phone can render it unconditionally.</summary>
    private async Task SendTriggerRumbleAsync(byte left, byte right)
    {
        await SendAsync(new ServerToClient
        {
            TriggerEffects = new TriggerEffects
            {
                LeftTriggerRumble = (uint)left,
                RightTriggerRumble = (uint)right,
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

    /// <summary>Keep-alive for the phone's HD-rumble synth. While a persona that
    /// needs it (Switch Pro / Joy-Con) is emulated, the current band state is
    /// re-sent every 50 ms, including an all-zero state, so the phone keeps its
    /// synthesis thread and output device running. Restarting that device at
    /// each rumble onset is what clicks; a steady stream (silent when idle)
    /// removes the transient.</summary>
    private async Task SwitchHapticPumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(50, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (_disposed) return;
            if (!_persona.NeedsHapticKeepAlive || Controller == null) continue;

            _persona.PumpHaptics(_personaHost);
        }
    }

    private void SendHdRumble(in HdRumbleBands bands)
    {
        _ = SendAsync(new ServerToClient
        {
            HdRumble = new HdRumble
            {
                LeftHighFreqHz = bands.LeftHighFreqHz, LeftHighAmp = bands.LeftHighAmp,
                LeftLowFreqHz = bands.LeftLowFreqHz, LeftLowAmp = bands.LeftLowAmp,
                RightHighFreqHz = bands.RightHighFreqHz, RightHighAmp = bands.RightHighAmp,
                RightLowFreqHz = bands.RightLowFreqHz, RightLowAmp = bands.RightLowAmp,
            }
        });
    }

    /// <summary>Output reports belong to the persona that owns the wire format;
    /// the session only routes the packet to it.</summary>
    private void OnGkOutputReceived(GKController controller, GKOutputPacket packet)
    {
        _persona.DecodeOutput(controller, packet, _personaHost);
    }

    private void AttachDualsenseAudio(GKController ctrl)
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

    private void AttachDs4Audio(GKController ctrl)
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

    private void OnDs4AudioFrames(GKAudioOutput output, ReadOnlyMemory<byte> pcm)
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

    private void OnDualsenseAudioFrames(GKAudioOutput output, ReadOnlyMemory<byte> pcm)
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

    /// <summary>Forwards the frame's mouse movement / scroll / buttons to the
    /// virtual USB HID mouse on the USB/IP backend. The device is created when
    /// the session connects, so it is already live by the time frames arrive.</summary>
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

    /// <summary>Creates the virtual USB HID keyboard/mouse pair. It is invoked
    /// when the session connects so the devices are ready up front; the create
    /// runs in the background because a USB/IP attach is slow enough that doing
    /// it inline would stall the report loop. Returns the live device, or null
    /// while the create is still in flight (or after it failed).</summary>
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
                created = new VirtualKeyboardMouse(_gkContext);
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
                TriggerEffects = BuildCompactTriggerEffects(leftTrigger, rightTrigger),
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

    /// <summary>Builds the compact frame's TriggerEffects envelope. DualSense modes carry
    /// the adaptive-effect byte blocks; Xbox One carries the (always present, even 0)
    /// trigger-rumble amplitudes so the phone can stop a latched rumble.</summary>
    private TriggerEffects? BuildCompactTriggerEffects(byte[]? leftTrigger, byte[]? rightTrigger)
    {
        bool hasRumble = SendsTriggerRumble;
        if (leftTrigger == null && rightTrigger == null && !hasRumble) return null;

        var effects = new TriggerEffects
        {
            LeftTriggerEffect = ByteString.CopyFrom(leftTrigger ?? Array.Empty<byte>()),
            RightTriggerEffect = ByteString.CopyFrom(rightTrigger ?? Array.Empty<byte>()),
        };
        if (hasRumble)
        {
            // Assigning marks the optional fields present, so the phone can tell an
            // Xbox source (including an explicit 0) from "no trigger-rumble source".
            effects.LeftTriggerRumble = _lastLeftTriggerRumble;
            effects.RightTriggerRumble = _lastRightTriggerRumble;
        }
        return effects;
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
            _lastLeftTriggerRumble = 0;
            _lastRightTriggerRumble = 0;
            ClearPendingAudio();
            _ = SendClearTriggerEffectsAsync();

            // 创建新控制器；成功前保留旧控制器。若创建失败，系统仍只有一个旧手柄，
            var nextPersona = PersonaCatalog.Create(mode);
            CreatedControllers newSet;
            try
            {
                newSet = await Task.Run(() => CreateControllers(nextPersona));
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
                // The persona changes with the wire format, and only here.
                _persona = nextPersona;
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
        if (primary is GKController gkCtrl)
        {
            var ctrl = gkCtrl;
            var ctrlSecond = secondary as GKController;
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
                    System.Diagnostics.Debug.WriteLine($"[GamepadSession] dispose GK controller failed: {ex.Message}");
                }
                if (ctrlSecond != null)
                {
                    PreRemoveDevice(ctrlSecond);
                    try { ctrlSecond.Dispose(); }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[GamepadSession] dispose GK controller failed: {ex.Message}");
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

    private static void PreRemoveDevice(GKController ctrl)
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

    private static void EnsureReflectionCache(GKController ctrl)
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
            _persona.WriteInput(neutral, _personaHost);
            SubmitMouseAndTouchpad(neutral);
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
            if (ctrl is GKController gk) PreRemoveDevice(gk);
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
            if (ctrl is GKController gk) gk.Dispose();
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

    /// <summary>
    /// The narrow surface a persona uses to reach its session: submit a report
    /// into an emulated device, or update the state the phone mirrors. Nested
    /// and private so the persona seam never widens the public API.
    /// </summary>
    private sealed class PersonaHost : IPersonaHost
    {
        private readonly GamepadSession _session;

        public PersonaHost(GamepadSession session) => _session = session;

        public GKController? PrimaryController => _session.Controller as GKController;

        public GKController? SecondaryController => _session.SecondaryController as GKController;

        public long TimestampMs => _session._reportClock.ElapsedMilliseconds;

        public void SubmitReport(GKController controller, ReadOnlySpan<byte> report)
            => controller.SubmitRawReport(report);

        public void SetVibration(byte largeMotor, byte smallMotor)
        {
            _session._lastLargeMotor = largeMotor;
            _session._lastSmallMotor = smallMotor;
        }

        public void SetTriggerRumble(byte left, byte right)
            => _ = _session.SendTriggerRumbleAsync(left, right);

        public void SetTestTone(bool enabled)
        {
            _session._testToneEnabled = enabled;
            _ = _session.SendTestToneStateAsync();
        }

        public void SendHdRumble(in HdRumbleBands bands) => _session.SendHdRumble(bands);
    }

    public void Dispose()
    {
        Disconnect();
        _cts.Dispose();
    }
}