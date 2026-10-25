using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using GamepadEmu.Protocol;
using GKME.Core.Transport;
using GKME.Core.Usb;
using Google.Protobuf;
using GKMD;
using GKMD.Internal.Usbip;

namespace GKME.Core;

public class NetworkService : IDisposable
{
    public const int Port = 37284;
    private const string BroadcastPrefix = "GKME|";

    public const byte TypeClientToServer = 0x00;
    public const byte TypeServerToClient = 0x01;
    public const byte TypeGamepadInput = 0x02;

    private readonly ConcurrentDictionary<string, DiscoveredDevice> _discovered = new();
    private readonly GKContext _gkContext = new();

    // Every session-lifetime rule (serialise, replace, retire, drain, register)
    // lives here; transports only move bytes.
    private readonly SessionIntake _intake;

    // ── USB/ADB transport ──
    private readonly AdbManager _adb = new();
    private readonly object _usbLock = new();
    private readonly Dictionary<string, DiscoveredDevice> _usbDiscovered = new();
    private Thread? _usbScanThread;

    public bool HidMaestroAvailable { get; private set; }

    /// <summary>True once an adb executable has been located. USB discovery only
    /// produces cards while this is true.</summary>
    public bool UsbAdbAvailable => _adb.Available;
    public string? UsbAdbError => _adb.LastError;

    /// <summary>Resolves a previously persisted controller mode for a device
    /// (keyed by <see cref="DiscoveredDevice.ModeKey"/>), or null when none is
    /// saved. Used by every connection path (manual + auto-reconnect) so the
    /// initial controller matches what the user last picked.</summary>
    public Func<DiscoveredDevice, ControllerMode?>? SavedModeResolver { get; set; }

    static NetworkService()
    {
        // HIDMaestro's TimeoutScale only honors [0.1, 100.0]; anything below the
        // floor is silently ignored (Factor falls back to 1.0). 0.1 is the floor
        // and shrinks every internal device-removal wait budget by 10x.
        Environment.SetEnvironmentVariable("GKMD_TIMEOUT_SCALE", "0.1");
    }

    public NetworkService()
    {
        _intake = new SessionIntake(CreateSession, () => RebootRequired);
        _intake.SessionStarted += s => OnSessionStarted?.Invoke(s);
        _intake.SessionEnded += s => OnSessionEnded?.Invoke(s);
        _intake.SessionConnectionLost += ip => OnSessionConnectionLost?.Invoke(ip);
        _intake.DeviceInfoUpdated += d => OnDeviceInfoUpdated?.Invoke(d);
        _intake.RebootRequired += _ => { RebootRequired = true; OnRebootRequired?.Invoke(); };
        _intake.UsbipInstallRequired += _ => OnUsbipInstallRequired?.Invoke();
    }

    /// <summary>Builds the session object the intake will drive. The controller
    /// persona is picked from the device's persisted mode, so every path
    /// (manual, USB, web, auto-reconnect) starts with what the user chose.</summary>
    private GamepadSession CreateSession(DiscoveredDevice device, Func<IMessage, Task> downlink)
    {
        var initialMode = SavedModeResolver?.Invoke(device) ?? ControllerMode.Ds4;
        return new GamepadSession(
            device,
            downlink,
            OnAudioSendStart,
            _gkContext,
            HidMaestroAvailable,
            initialMode);
    }

    private UdpClient? _udpClient;
    private Thread? _recvThread;
    private Thread? _sendThread;
    private volatile bool _stopped;
    private readonly ConcurrentQueue<SendItem> _sendQueue = new();
    private readonly AutoResetEvent _sendEvent = new(false);
    private bool _disposed;
    private volatile byte _sendPriority;
    private readonly object _sendPriorityLock = new();

    /// <summary>Send frequency for the dedicated send thread.</summary>
    private enum SendPriority : byte
    {
        /// <summary>Idle — poll ~100ms (10 Hz). Used when only input is present.</summary>
        Idle = 0,
        /// <summary>Audio PCM is being drained — poll ~8ms (125 Hz).</summary>
        Audio,
    }

    private sealed class SendItem
    {
        public required IPEndPoint Endpoint;
        public required byte Type;
        public required byte[] Payload;
    }

    public bool InitializeHidMaestro()
    {
        try
        {
            var log = Path.Combine(AppContext.BaseDirectory, "gkme_error.log");
            System.IO.File.AppendAllText(log, $"[InitHidMaestro] starting, BaseDir={AppContext.BaseDirectory}\n");
            
            int loaded = _gkContext.LoadDefaultProfiles();
            if (loaded == 0)
            {
                System.IO.File.AppendAllText(log, "[InitHidMaestro] LoadDefaultProfiles returned 0\n");
                return false;
            }

            PatchTimeoutScale(_gkContext);

            // Always ensure the SDK's embedded driver version is deployed.
            // InstallDriver() has a same-version fast path (~40-60 ms) and only
            // runs the full pipeline when the installed driver actually differs.
            _gkContext.InstallDriver();

            HidMaestroAvailable = true;
            RebootRequired = false;
            UsbipInstallRequired = false;
            return true;
        }
        catch (UsbipRebootRequiredException)
        {
            // The upgrade was applied but only a reboot will load it. Surface
            // a prompt rather than treating it as a driver failure.
            try { System.IO.File.AppendAllText("gkme_error.log", "[InitHidMaestro] USBip upgrade requires a reboot\n"); } catch { }
            HidMaestroAvailable = false;
            RebootRequired = true;
            OnRebootRequired?.Invoke();
            return false;
        }
        catch (UsbipInstallRequiredException ex)
        {
            // Nothing is installed (or it is too old): the user chooses when
            // to install. Surface a prompt rather than a driver failure.
            try { System.IO.File.AppendAllText("gkme_error.log", $"[InitHidMaestro] USBip install required (upgrade={ex.IsUpgrade})\n"); } catch { }
            HidMaestroAvailable = false;
            UsbipInstallRequired = true;
            OnUsbipInstallRequired?.Invoke();
            return false;
        }
        catch (Exception ex)
        {
            try { System.IO.File.AppendAllText("gkme_error.log", $"[InitHidMaestro] EXCEPTION: {ex}\n"); } catch { }
            HidMaestroAvailable = false;
            return false;
        }
    }

    private static void PatchTimeoutScale(GKContext ctx)
    {
        try
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var seen = new HashSet<object>();
            PatchTimeoutScaleRecursive(ctx, flags, seen);
        }
        catch { }
    }

    private static void PatchTimeoutScaleRecursive(object obj, BindingFlags flags, HashSet<object> seen)
    {
        if (obj == null || !seen.Add(obj)) return;
#pragma warning disable IL2075
        var t = obj.GetType();
#pragma warning restore IL2075

#pragma warning disable IL2072
        SetFieldInt(t, obj, "perAttemptTimeoutMs", 500, flags);
        SetFieldInt(t, obj, "timeoutMs", 500, flags);
        SetFieldInt(t, obj, "callbackTimeoutMs", 500, flags);

        foreach (var f in t.GetFields(flags))
        {
            var val = f.GetValue(obj);
            if (val != null && f.FieldType.IsClass && f.FieldType.Namespace?.StartsWith("GKMD") == true)
                PatchTimeoutScaleRecursive(val, flags, seen);
        }
#pragma warning restore IL2072
    }

    private static void SetFieldInt(Type t, object obj, string name, int value, BindingFlags flags)
    {
#pragma warning disable IL2072
        var f = t.GetField(name, flags);
#pragma warning restore IL2072
        if (f != null && f.FieldType == typeof(int))
            f.SetValue(obj, value);
    }

    public IReadOnlyCollection<DiscoveredDevice> DiscoveredDevices
    {
        get
        {
            var now = DateTime.UtcNow;
            var list = _discovered.Values.Where(d => (now - d.LastSeen).TotalSeconds < 15).ToList();
            lock (_usbLock) list.AddRange(_usbDiscovered.Values);
            return list;
        }
    }

    public IReadOnlyCollection<GamepadSession> ActiveSessions => _intake.ActiveSessions;

    public event Action<DiscoveredDevice>? OnDeviceDiscovered;
    /// <summary>Raised when a previously discovered USB device disappears —
    /// its cable was unplugged, or its GKME service stopped listening.</summary>
    public event Action<DiscoveredDevice>? OnDeviceLost;
    /// <summary>Raised when a session's Hello fills in the device name / MAC
    /// (notably for manual IP connections that were not seen via broadcast).</summary>
    public event Action<DiscoveredDevice>? OnDeviceInfoUpdated;
    public event Action<GamepadSession>? OnSessionStarted;
    public event Action<GamepadSession>? OnSessionEnded;
    public event Action<string>? OnSessionConnectionLost;
    /// <summary>Raised when the USBip transport needs a reboot before
    /// controllers can be created. The UI shows a reboot prompt.</summary>
    public event Action? OnRebootRequired;

    /// <summary>Sticky flag: the USBip upgrade was applied but only a reboot
    /// will load it. Set during startup, before a UI can subscribe, so the
    /// view model re-checks it once the window exists.</summary>
    public bool RebootRequired { get; private set; }

    /// <summary>Set the sticky reboot-required flag when a transport change
    /// applied outside initialization (the install dialog ran an installer
    /// that asked for a reboot). While set, every new connection is refused;
    /// the caller raises its own reboot prompt.</summary>
    public void MarkRebootRequired() => RebootRequired = true;

    /// <summary>Raised when usbip-win2 must be installed or upgraded. The
    /// UI shows the install prompt.</summary>
    public event Action? OnUsbipInstallRequired;

    /// <summary>Sticky flag: usbip-win2 is missing or too old. Set during
    /// startup so a view model that subscribes later still sees it.</summary>
    public bool UsbipInstallRequired { get; private set; }

    private static string GetSessionKey(IPEndPoint remoteEp) =>
        $"{new IPEndPoint(remoteEp.Address, Port)}";

    internal static ServerToClient BuildServerHello() => new()
    {
        ServerHello = new ServerHello
        {
            ProtocolVersion = 1,
            HostName = Environment.MachineName,
            MaxDownlinkRateHz = 1000,
            RecommendedUplinkIntervalUs = 0,
        }
    };

    public void StartDiscovery()
    {
        if (_recvThread != null) return;
        _stopped = false;
        _udpClient = new UdpClient(new IPEndPoint(IPAddress.Any, Port));
        _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveTimeout, 200);
        _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.SendTimeout, 200);

        // 收包线程：专用 Thread，高于正常优先级，直接上报 GamepadInput
        _recvThread = new Thread(RecvLoop)
        {
            IsBackground = true,
            Name = "GKNetRecv",
            Priority = ThreadPriority.AboveNormal,
        };
        _recvThread.Start();

        // 发包线程：专用 Thread，高于正常优先级，从队列取包发 UDP
        _sendThread = new Thread(SendLoop)
        {
            IsBackground = true,
            Name = "GKNetSend",
            Priority = ThreadPriority.AboveNormal,
        };
        _sendThread.Start();

        StartUsbScan();
    }

    private void StartUsbScan()
    {
        if (_usbScanThread != null) return;
        _usbScanThread = new Thread(UsbScanLoop)
        {
            IsBackground = true,
            Name = "GKUsbScan",
            Priority = ThreadPriority.BelowNormal,
        };
        _usbScanThread.Start();
    }

    /// <summary>Polls `adb devices`, then probes each authorized phone for the
    /// real device name, ANDROID_ID and a listening GKME service. Only phones
    /// whose service is actually accepting connections are published — the USB
    /// equivalent of a phone that has started broadcasting on WiFi. Retries
    /// locating adb (it may be installed / dropped in after launch) without
    /// spamming the log.</summary>
    private void UsbScanLoop()
    {
        while (!_stopped && !_adb.Available)
        {
            try
            {
                if (_adb.TryInitialize() && _adb.EnsureServer()) break;
            }
            catch { }
            Thread.Sleep(3000);
        }
        if (!_adb.Available) return;

        while (!_stopped)
        {
            try
            {
                var devices = _adb.ListDevices();
                var seen = new HashSet<string>();
                foreach (var d in devices)
                {
                    // Only a fully authorized device can be running the service.
                    if (!d.IsReady) continue;

                    var probe = _adb.ProbeUsbDevice(d.Serial);
                    // Service not started yet — keep the card hidden so the USB
                    // list matches the WiFi list (only live services show up).
                    if (!probe.ServiceListening) continue;

                    var key = $"usb:{d.Serial}";
                    seen.Add(key);
                    DiscoveredDevice? publish = null;
                    DiscoveredDevice current;
                    lock (_usbLock)
                    {
                        if (!_usbDiscovered.TryGetValue(key, out var dev))
                        {
                            dev = new DiscoveredDevice
                            {
                                DeviceName = probe.DeviceName ?? d.DisplayName,
                                MacAddress = probe.AndroidId ?? "",
                                IpAddress = IPAddress.Loopback,
                                IsUsb = true,
                                AdbSerial = d.Serial,
                                AdbState = d.State,
                            };
                            _usbDiscovered[key] = dev;
                            publish = dev;
                        }
                        else
                        {
                            dev.LastSeen = DateTime.UtcNow;
                            dev.AdbState = d.State;
                            dev.DeviceName = probe.DeviceName ?? d.DisplayName;
                            if (!string.IsNullOrWhiteSpace(probe.AndroidId))
                                dev.MacAddress = probe.AndroidId!;
                        }
                        current = dev;
                    }
                    if (publish != null)
                        OnDeviceDiscovered?.Invoke(publish);

                    // The phone is reachable again but its session's transport may
                    // have dropped (cable replugged, service restarted). The virtual
                    // controller survived that loss; re-open the link so the same
                    // session resumes — the USB equivalent of a phone resuming its
                    // WiFi broadcast after a brief dropout.
                    if (_intake.Contains(key))
                        TryReattachUsbSession(key, current);
                }

                List<DiscoveredDevice>? lost = null;
                lock (_usbLock)
                {
                    foreach (var key in _usbDiscovered.Keys.ToList())
                    {
                        if (seen.Contains(key)) continue;
                        if (_usbDiscovered.Remove(key, out var gone))
                            (lost ??= new List<DiscoveredDevice>()).Add(gone);
                    }
                }
                if (lost != null)
                {
                    foreach (var gone in lost)
                        OnDeviceLost?.Invoke(gone);
                }
            }
            catch { }
            Thread.Sleep(1500);
        }
    }

    public void StopDiscovery()
    {
        _stopped = true;
        _sendEvent.Set();
        try { _udpClient?.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveTimeout, 1); } catch { }
        try { _udpClient?.Close(); } catch { }
        _udpClient = null;

        if (_recvThread != null)
        {
            _recvThread.Join();
            _recvThread = null;
        }
        if (_sendThread != null)
        {
            _sendThread.Join();
            _sendThread = null;
        }
        if (_usbScanThread != null)
        {
            try { _usbScanThread.Join(2500); } catch { }
            _usbScanThread = null;
        }
    }

    /// <summary>收包线程：recv UDP → 解析 → 直接调用 session 上报，无队列。</summary>
    private void RecvLoop()
    {
        var buf = new byte[65535];
        while (!_stopped)
        {
            try
            {
                IPEndPoint remoteEp = new(IPAddress.Any, 0);
                var data = _udpClient!.Receive(ref remoteEp);
                if (data.Length == 0) continue;

                // Plain-text discovery broadcast from phone
                var text = Encoding.UTF8.GetString(data);
                if (text.StartsWith(BroadcastPrefix))
                {
                    HandleDiscovery(new IPEndPoint(remoteEp.Address, Port), text);
                    continue;
                }

                var type = data[0];
                var payload = data.Skip(1).ToArray();

                switch (type)
                {
                    case TypeClientToServer:
                        RouteClientToServer(new IPEndPoint(remoteEp.Address, Port), payload);
                        break;
                    case TypeGamepadInput:
                        RouteGamepadInput(new IPEndPoint(remoteEp.Address, Port), payload);
                        break;
                }
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
            {
                // ReceiveTimeout fired — loop back and check _stopped
                continue;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>发包线程：从队列取包发 UDP，根据优先级动态调整 poll 频率。</summary>
    /// <para>Vibration/Audio 时 ~8ms (125Hz) poll；空闲时 ~100ms (10Hz) poll。</para>
    private void SendLoop()
    {
        var sw = new System.Diagnostics.Stopwatch();
        sw.Start();

        while (!_stopped)
        {
            int intervalMs;
            byte p;
            lock (_sendPriorityLock) p = _sendPriority;
            intervalMs = p > (byte)SendPriority.Idle ? 8 : 100;

            if (!_sendEvent.WaitOne(intervalMs, !_stopped))
            {
                // Timeout — drain whatever is queued.
                DrainQueue();
                continue;
            }

            // Signal fired — drain all queued items.
            DrainQueue();
        }

        // Final drain on shutdown.
        DrainQueue();
    }

    /// <summary>Bump send priority.</summary>
    private void SetSendPriority(SendPriority priority)
    {
        lock (_sendPriorityLock)
        {
            if (_sendPriority < (byte)priority) _sendPriority = (byte)priority;
        }
    }

    private void DrainQueue()
    {
        while (_sendQueue.TryDequeue(out var item))
        {
            try
            {
                var data = new byte[1 + item.Payload.Length];
                data[0] = item.Type;
                Buffer.BlockCopy(item.Payload, 0, data, 1, item.Payload.Length);
                try { _udpClient!.Send(data, data.Length, item.Endpoint); }
                catch { }
            }
            catch { }
        }
    }

    /// <summary>Mark audio drain active — raises priority to Audio.</summary>
    private void OnAudioSendStart()
    {
        SetSendPriority(SendPriority.Audio);
        _sendEvent.Set();
    }

    private void HandleDiscovery(IPEndPoint remoteEp, string msg)
    {
        // Broadcast format: "GKME|<device name>|<MAC>"
        var parts = msg.Split('|', 3);
        var deviceName = parts.Length > 1 ? parts[1].Trim() : "";
        var mac = parts.Length > 2 ? parts[2].Trim() : "";

        var phoneEp = new IPEndPoint(remoteEp.Address, Port);
        var key = GetSessionKey(remoteEp);

        var device = _discovered.GetOrAdd(key, _ => new DiscoveredDevice
        {
            DeviceName = deviceName,
            MacAddress = mac,
            IpAddress = phoneEp.Address,
            Port = Port,
        });
        device.DeviceName = deviceName;
        device.MacAddress = mac;
        device.LastSeen = DateTime.UtcNow;

        // Phone restarted its broadcast — send ServerHello so the phone can
        // resume the handshake even if it hasn't sent a Hello yet.
        if (_intake.Contains(key))
        {
            EnqueueSend(phoneEp, TypeServerToClient, BuildServerHello());
        }

        OnDeviceDiscovered?.Invoke(device);
    }

    private void RouteClientToServer(IPEndPoint remoteEp, byte[] payload)
    {
        var key = GetSessionKey(remoteEp);
        try
        {
            var cts = ClientToServer.Parser.ParseFrom(payload);
            if (_intake.TryGet(key, out var session))
            {
                session.HandleClientToServer(cts);
            }
            // 无 session 时不自动创建虚拟手柄：PC 端只能通过用户手动点"连接"建立，
            // 否则手机 app 一打开、广播 Hello，就会在用户未选择的情况下冒出虚拟手柄。
        }
        catch { }
    }

    private void RouteGamepadInput(IPEndPoint remoteEp, byte[] payload)
    {
        try
        {
            var input = GamepadInput.Parser.ParseFrom(payload);
            var key = GetSessionKey(remoteEp);

            if (_intake.TryGet(key, out var session))
            {
                // 直接上报，无队列
                session.ReportGamepadInput(input);
            }
        }
        catch { }
    }

    /// <summary>Queue a send to the dedicated send thread. Non-blocking.</summary>
    private void EnqueueSend(IPEndPoint ep, byte type, IMessage msg)
    {
        if (_disposed) return;
        var payload = msg.ToByteArray();
        var item = new SendItem { Endpoint = ep, Type = type, Payload = payload };
        _sendQueue.Enqueue(item);
        _sendEvent.Set();
    }

    public Task<GamepadSession?> ConnectAsync(DiscoveredDevice device, int connectTimeoutMs = 5000)
    {
        if (device.IsUsb) return ConnectUsbAsync(device, connectTimeoutMs);

        // The intake performs the reboot check, serialises connects, reuses a
        // live session, drains retired ones, registers and starts.
        var phoneEp = new IPEndPoint(device.IpAddress, Port);
        var transport = new UdpSessionTransport(
            msg => EnqueueSend(phoneEp, TypeServerToClient, msg));
        return _intake.ConnectAsync(device, transport);
    }

    /// <summary>Creates a session for a browser client served by the embedded
    /// GKME-Web server. Mirrors the WiFi connect path, but the transport is a
    /// WebSocket bridge instead of UDP: <paramref name="sendToBrowser"/> is the
    /// session's downlink (ServerToClient messages), while the caller feeds the
    /// browser's Hello / GamepadInput into the returned session.</summary>
    public Task<GamepadSession?> ConnectWebAsync(DiscoveredDevice device, Func<IMessage, Task> sendToBrowser)
        => _intake.ConnectAsync(device, new WebSessionTransport(sendToBrowser));

    /// <summary>Publishes a web client as a discovered device so the desktop UI
    /// shows a card for it (the browser is never seen via UDP broadcast).</summary>
    public void AnnounceDevice(DiscoveredDevice device) => OnDeviceDiscovered?.Invoke(device);

    /// <summary>Marks a web client as gone so its card is dropped.</summary>
    public void AnnounceDeviceLost(DiscoveredDevice device) => OnDeviceLost?.Invoke(device);

    /// <summary>Connects to a phone over USB/ADB. Sets up a `tcp:0 -> tcp:37284`
    /// forward and opens a TCP connection to the assigned loopback port; the
    /// phone accepts it in its USB TCP server. The session's device is the
    /// loopback endpoint, not the scan card.</summary>
    private Task<GamepadSession?> ConnectUsbAsync(DiscoveredDevice device, int connectTimeoutMs)
    {
        if (string.IsNullOrEmpty(device.AdbSerial)) return Task.FromResult<GamepadSession?>(null);

        var liveDevice = new DiscoveredDevice
        {
            DeviceName = device.DeviceName,
            MacAddress = device.MacAddress,
            IpAddress = IPAddress.Loopback,
            Port = AdbManager.DevicePort,
            IsUsb = true,
            AdbSerial = device.AdbSerial,
            AdbState = device.AdbState,
        };

        // The transport resolves its link lazily, so a re-attached USB link keeps
        // flowing through the same session without re-creating it.
        var transport = new UsbSessionTransport(liveDevice, _adb, connectTimeoutMs);
        return _intake.ConnectAsync(liveDevice, transport);
    }

    /// <summary>Re-attaches the USB transport for a session whose link dropped,
    /// without destroying its emulated controller. Runs off the scan loop; the
    /// intake reservation makes it mutually exclusive with a manual connect and
    /// with a second re-attach.</summary>
    private void TryReattachUsbSession(string key, DiscoveredDevice device)
    {
        _ = Task.Run(async () =>
        {
            using var reservation = _intake.TryReserve(key);
            if (reservation == null) return;

            if (!_intake.TryGet(key, out var session) || !session.IsConnected) return;
            if (_intake.GetTransport(session) is not UsbSessionTransport { IsLinkUp: false } transport) return;

            try
            {
                if (!await transport.AttachAsync(session).ConfigureAwait(false)) return;

                // The session may have been disconnected while the link was being
                // opened (user pressed disconnect); don't leave an orphan link.
                if (!_intake.IsCurrent(session)) transport.Detach();
            }
            catch { }
        });
    }

    public async Task SendToPhoneAsync(IPEndPoint ep, byte type, IMessage msg)
    {
        if (_disposed) return;
        EnqueueSend(ep, type, msg);
    }

    public async Task SendDisconnectAsync(DiscoveredDevice device)
    {
        if (!_intake.TryGet(device.Key, out var session)) return;

        // Tell the phone first (the downlink still belongs to the transport),
        // then let the intake evict and retire the session. Releasing the link
        // is the transport's job and happens on disconnect.
        await session.SendAsync(new ServerToClient
        {
            Disconnect = new Disconnect { Reason = "User disconnected" }
        });
        session.Disconnect();
    }

    public Task DisconnectAllAsync() => _intake.DisconnectAllAsync();

    public void DisconnectAll() => _intake.DisconnectAll();

    public void Refresh()
    {
        StopDiscovery();
        DisconnectAll();
        _discovered.Clear();
        StartDiscovery();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopDiscovery();
        DisconnectAll();
        _adb.Dispose();
        _gkContext.Dispose();
    }
}