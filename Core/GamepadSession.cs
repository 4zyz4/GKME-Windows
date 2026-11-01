using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using GamepadEmu.Protocol;
using Google.Protobuf;
using GKMD;
using GKMD.Internal.Usbip;
using GKME.Core.Downlink;
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
    // Every phone-bound feedback packet is constructed here, and the last-known
    // state it is built from lives here. The session only feeds it state and
    // publishes; see PhoneDownlink for why the seven shallow senders moved.
    private readonly PhoneDownlink _downlink;

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
    private DateTime _kbMouseRetryAfterUtc;

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
    /// <summary>Raised when creating a virtual controller fails; the exception
    /// has already been written to <see cref="Log"/> in full.</summary>
    public event Action<GamepadSession, Exception>? OnControllerError;

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
        _downlink = new PhoneDownlink(SendAsync, onAudioSendStart);
        _downlink.Configure(_persona.HasLed, _persona.SendsTriggerRumble);
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
    /// Also re-publishes the compact frame on each wake so the phone's feedback
    /// loop keeps running without a separate timer.</summary>
    private async Task ReportLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_disposed)
        {
            // Wait for an input packet or a periodic wake (vibration/telemetry).
            // 500ms timeout keeps the compact frame + audio drain running at ~2 Hz
            // when no input arrives, without fighting Windows timer throttling.
            if (!_inputReceived.Wait(500, ct))
            {
                // Periodic wake — re-publish the compact frame + test-tone state.
                if (_downlink.TestToneEnabled) _downlink.PublishTestToneState();
                if (Controller != null) _downlink.PublishCompactFrame();
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

            // Re-publish the compact frame on every input wake.
            if (Controller != null) _downlink.PublishCompactFrame();
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
                        Log.Error($"create controller {Mode} failed", ex);
                        try { OnControllerError?.Invoke(this, ex); } catch { }
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
            Log.Info($"session ready: '{Device.DeviceName}' {Device.Identity.EndpointLabel}, mode {Mode}");
            OnReady?.Invoke(this);
        }

        // Acknowledge the phone's hello so an already-existing session (e.g.
        // one that survived a watchdog timeout) can re-sync on the auto-reconnect
        // path — otherwise the phone never receives any packet and stays stuck.
        _ = SendAsync(NetworkService.BuildServerHello());
        _downlink.PublishLedState();
        _downlink.PublishTestToneState();
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
        byte? largeMotor = null;
        byte? smallMotor = null;
        foreach (var (k, v) in e.Fields)
        {
            var key = k.ToLowerInvariant();
            if (key is "leftmotor" or "largemotor" or "leftactuator" or "leftlinear")
                largeMotor = Convert.ToByte(v);
            else if (key is "rightmotor" or "smallmotor" or "rightactuator" or "rightlinear")
                smallMotor = Convert.ToByte(v);
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

        if (largeMotor.HasValue) _downlink.SetLargeMotor(largeMotor.Value);
        if (smallMotor.HasValue) _downlink.SetSmallMotor(smallMotor.Value);

        if (_downlink.UpdateLed(newColor, newPlayerLed))
        {
            _downlink.PublishLedState();
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
            _downlink.SetLeftTriggerEffect(newLeftTriggerEffect);
            triggersUpdated = true;
        }
        if (newRightTriggerEffect != null && rightValid)
        {
            _downlink.SetRightTriggerEffect(newRightTriggerEffect);
            triggersUpdated = true;
        }

        if (triggersUpdated)
        {
            _downlink.PublishTriggerEffects();
        }
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
        => _downlink.PushPcm(
            PcmSourceLayout.Ds4Stereo, output.Channels, output.SampleRateHz, output.BitsPerSample, pcm.Span);

    private void OnDualsenseAudioFrames(GKAudioOutput output, ReadOnlyMemory<byte> pcm)
        => _downlink.PushPcm(
            PcmSourceLayout.DualsenseFourChannel, output.Channels, output.SampleRateHz, output.BitsPerSample, pcm.Span);
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
            if (_kbMouseCreating || _disposed) return null;
            if (_kbMouseFailed)
            {
                // A transient USB/IP attach failure must not disable the
                // keyboard/mouse for the whole session; retry after a pause.
                if (DateTime.UtcNow < _kbMouseRetryAfterUtc) return null;
                _kbMouseFailed = false;
            }
            _kbMouseCreating = true;
        }

        Log.Info("creating virtual keyboard/mouse");

        _ = Task.Run(() =>
        {
            VirtualKeyboardMouse created;
            try
            {
                created = new VirtualKeyboardMouse(_gkContext);
            }
            catch (UsbipRebootRequiredException ex)
            {
                Log.Warn($"create virtual keyboard/mouse needs a reboot: {ex.Message}");
                MarkKeyboardMouseFailed();
                try { OnRebootRequired?.Invoke(this); } catch { }
                return;
            }
            catch (UsbipInstallRequiredException ex)
            {
                Log.Warn($"create virtual keyboard/mouse skipped: USB/IP transport not ready: {ex.Message}");
                MarkKeyboardMouseFailed();
                try { OnUsbipInstallRequired?.Invoke(this); } catch { }
                return;
            }
            catch (Exception ex)
            {
                Log.Error("create virtual keyboard/mouse failed", ex);
                MarkKeyboardMouseFailed();
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
            else Log.Info("virtual keyboard/mouse attached (HID keyboard + mouse)");
        });

        return null;
    }

    private void MarkKeyboardMouseFailed()
    {
        lock (_kbMouseLock)
        {
            _kbMouseCreating = false;
            _kbMouseFailed = true;
            _kbMouseRetryAfterUtc = DateTime.UtcNow.AddSeconds(10);
        }
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

            _downlink.ResetVibration();
            _downlink.ResetTriggerEffects();
            _downlink.ResetTriggerRumble();
            _downlink.ResetAudio();
            _downlink.ClearTriggerEffects();

            // 创建新控制器；成功前保留旧控制器。若创建失败，系统仍只有一个旧手柄，
            var nextPersona = PersonaCatalog.Create(mode);
            CreatedControllers newSet;
            try
            {
                newSet = await Task.Run(() => CreateControllers(nextPersona));
            }
            catch (UsbipRebootRequiredException ex)
            {
                Log.Warn($"switch to {mode} needs a reboot: {ex.Message}");
                try { OnRebootRequired?.Invoke(this); } catch { }
                return Mode;
            }
            catch (UsbipInstallRequiredException ex)
            {
                Log.Warn($"switch to {mode} skipped: USB/IP transport not ready: {ex.Message}");
                try { OnUsbipInstallRequired?.Invoke(this); } catch { }
                return Mode;
            }
            catch (Exception ex)
            {
                Log.Error($"switch controller mode {mode} failed; keeping {Mode}", ex);
                try { OnControllerError?.Invoke(this, ex); } catch { }
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

            _downlink.Configure(nextPersona.HasLed, nextPersona.SendsTriggerRumble);
            _downlink.ResetVibration();
            _downlink.ResetLed();
            _downlink.PublishLedState();
            _downlink.PublishTestToneState();

            if (!_readyFired)
            {
                _readyFired = true;
                Log.Info($"session ready: '{Device.DeviceName}' {Device.Identity.EndpointLabel}, mode {Mode}");
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
        _downlink.ResetAudio();
        _downlink.ClearTriggerEffects();

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
        // Only the usbip-win2 device tree has SetupAPI devnodes to uninstall;
        // Linux's vhci_hcd detach is handled entirely inside GKController.
        if (!PlatformInfo.IsWindows) return;
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
            => _session._downlink.SetVibration(largeMotor, smallMotor);

        public void SetTriggerRumble(byte left, byte right)
            => _session._downlink.SetTriggerRumble(left, right);

        public void SetTestTone(bool enabled)
            => _session._downlink.SetTestTone(enabled);

        public void SendHdRumble(in HdRumbleBands bands) => _session._downlink.PublishHdRumble(bands);
    }

    public void Dispose()
    {
        Disconnect();
        _cts.Dispose();
    }
}