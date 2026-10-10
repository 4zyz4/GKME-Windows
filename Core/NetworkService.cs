using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using GamepadEmu.Protocol;
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
    private readonly ConcurrentDictionary<string, GamepadSession> _sessions = new();
    private readonly GKContext _gkContext = new();

    // ── USB/ADB transport ──
    private readonly AdbManager _adb = new();
    private readonly object _usbLock = new();
    private readonly Dictionary<string, DiscoveredDevice> _usbDiscovered = new();
    private readonly Dictionary<string, UsbConnection> _usbConnections = new();
    // adb-forward local port currently bound to each USB session key. Tracked
    // separately from the connection so it can be swapped when the transport
    // is re-attached after a dropped link (the assigned port changes).
    private readonly Dictionary<string, int> _usbForwardPorts = new();
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

    public IReadOnlyCollection<GamepadSession> ActiveSessions => _sessions.Values.ToList();

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
                    if (_sessions.ContainsKey(key))
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
        if (_sessions.TryGetValue(key, out var session))
        {
            EnqueueSend(phoneEp, TypeServerToClient, BuildServerHello());
        }

        OnDeviceDiscovered?.Invoke(device);
    }

    private readonly object _connectLock = new();
    private readonly HashSet<string> _connecting = new();

    // 已从 _sessions 移除、但虚拟手柄仍在后台清理中的 session（key = 设备 key）。
    // 新连接必须先等这些旧手柄彻底销毁，保证同一 IP 同时只出现一个虚拟手柄
    // （"断开 → 手机自动重连" / "反复手动连接断开" 时容易触发双柄并存）。
    private readonly object _retireLock = new();
    private readonly Dictionary<string, List<GamepadSession>> _retired = new();

    private void RouteClientToServer(IPEndPoint remoteEp, byte[] payload)
    {
        var key = GetSessionKey(remoteEp);
        try
        {
            var cts = ClientToServer.Parser.ParseFrom(payload);
            if (_sessions.TryGetValue(key, out var session))
            {
                session.HandleClientToServer(cts);
            }
            // 无 session 时不自动创建虚拟手柄：PC 端只能通过用户手动点"连接"建立，
            // 否则手机 app 一打开、广播 Hello，就会在用户未选择的情况下冒出虚拟手柄。
        }
        catch { }
    }

    /// <summary>登记一个已断开、但虚拟手柄仍在后台销毁中的 session。新连接在
    /// <see cref="DrainRetiredAsync"/> 里等待它彻底清理完，避免新旧手柄并存。</summary>
    private void Retire(GamepadSession s)
    {
        lock (_retireLock)
        {
            if (!_retired.TryGetValue(s.Device.Key, out var list))
                _retired[s.Device.Key] = list = new List<GamepadSession>();
            list.Add(s);
        }

        // 清理完成后自行脱离登记，避免长期持有已释放的引用。
        _ = Task.Run(async () =>
        {
            try { await s.WaitForCleanupAsync(); }
            catch { }
            finally
            {
                lock (_retireLock)
                {
                    if (_retired.TryGetValue(s.Device.Key, out var l))
                    {
                        l.Remove(s);
                        if (l.Count == 0) _retired.Remove(s.Device.Key);
                    }
                }
            }
        });
    }

    /// <summary>等待指定设备的全部已退役 session 完成虚拟手柄销毁（幂等）。</summary>
    private async Task DrainRetiredAsync(string key)
    {
        List<GamepadSession>? list;
        lock (_retireLock)
        {
            if (!_retired.TryGetValue(key, out list)) return;
            _retired.Remove(key);
        }
        try { await Task.WhenAll(list.Select(s => s.WaitForCleanupAsync())); }
        catch { }
    }

    private void RouteGamepadInput(IPEndPoint remoteEp, byte[] payload)
    {
        try
        {
            var input = GamepadInput.Parser.ParseFrom(payload);
            var key = GetSessionKey(remoteEp);

            if (_sessions.TryGetValue(key, out var session))
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

    public async Task<GamepadSession?> ConnectAsync(DiscoveredDevice device, int connectTimeoutMs = 5000)
    {
        // Reboot pending: Windows will not load the transport until the next
        // boot, so refuse every new connection instead of letting it half-
        // establish and then fail while creating a controller.
        if (RebootRequired) return null;

        if (device.IsUsb) return await ConnectUsbAsync(device, connectTimeoutMs);

        var key = device.Key;
        GamepadSession? oldSession = null;

        // 串行化同一手机的所有连接入口（手动连接、设备卡片、自动重连），
        // 避免并发各建一个 session / 虚拟控制器。
        lock (_connectLock)
        {
            if (_connecting.Contains(key)) return null;
            if (_sessions.TryGetValue(key, out var existing) && existing.IsConnected)
                return existing;
            _sessions.TryRemove(key, out oldSession);
            _connecting.Add(key);
        }

        try
        {
            // 拆旧控制器（HIDMaestro teardown 慢，等它彻底释放再建新的，
            // 否则 Windows 会短暂同时显示两个手柄）。只影响同一时刻唯一的连接流程。
            if (oldSession != null)
            {
                oldSession.Disconnect();
                await oldSession.WaitForCleanupAsync();
            }

            // 手动断开 / DisconnectAll 留下的 session 不在 _sessions 里，但虚拟手柄
            // 可能仍在后台清理。等它们全部释放后再建新控制器，保证同一 IP 同时
            // 只有一个虚拟手柄（"断开 → 自动重连" 场景最容易触发双柄并存）。
            await DrainRetiredAsync(key);

            var phoneEp = new IPEndPoint(device.IpAddress, Port);

            var initialMode = SavedModeResolver?.Invoke(device) ?? ControllerMode.Ds4;
            var session = new GamepadSession(
                device,
                msg => { EnqueueSend(phoneEp, TypeServerToClient, msg); return Task.CompletedTask; },
                OnAudioSendStart,
                _gkContext,
                HidMaestroAvailable,
                initialMode);
            session.OnReady += s =>
            {
                OnSessionStarted?.Invoke(s);
            };
            session.OnConnectionLost += s =>
            {
                OnSessionConnectionLost?.Invoke(s.Device.IpString);
            };
            session.OnDeviceInfoUpdated += s =>
            {
                OnDeviceInfoUpdated?.Invoke(s.Device);
            };
            session.OnRebootRequired += _ => { RebootRequired = true; OnRebootRequired?.Invoke(); };
            session.OnUsbipInstallRequired += _ => OnUsbipInstallRequired?.Invoke();
            session.OnDisconnected += s =>
            {
                _sessions.TryRemove(s.Device.Key, out _);
                // 旧手柄仍在后台销毁中；新连接必须等它释放，否则 Windows 里会
                // 同时出现旧+新两个虚拟手柄。
                Retire(s);
                OnSessionEnded?.Invoke(s);
            };

            lock (_connectLock)
            {
                // 尽快注册 session 防止并发的连接流程各建一个 session / 虚拟控制器。
                // 等待期间另一个流程抢先把 session 建好了：销毁本流程刚建的，
                // 复用已注册的那个，避免出现两个虚拟控制器。
                if (_sessions.TryGetValue(key, out var current))
                {
                    session.Disconnect();
                    return current;
                }
                _sessions[key] = session;
            }

            session.Start();

            // 注册后再发送 ServerHello，让手机端可以完成握手。
            EnqueueSend(phoneEp, TypeServerToClient, BuildServerHello());

            return session;
        }
        catch
        {
            return null;
        }
        finally
        {
            lock (_connectLock) _connecting.Remove(key);
        }
    }

    /// <summary>Connects to a phone over USB/ADB. Sets up a `tcp:0 -> tcp:37284`
    /// forward and opens a TCP connection to the assigned loopback port; the
    /// phone accepts it in its USB TCP server.</summary>
    private async Task<GamepadSession?> ConnectUsbAsync(DiscoveredDevice device, int connectTimeoutMs)
    {
        if (RebootRequired) return null;

        var key = device.Key;
        GamepadSession? oldSession = null;

        lock (_connectLock)
        {
            if (_connecting.Contains(key)) return null;
            // A live session already owns this device: its transport is either up
            // or being re-attached by the scan loop, so just hand it back.
            if (_sessions.TryGetValue(key, out var existing) && existing.IsConnected)
                return existing;
            _sessions.TryRemove(key, out oldSession);
            _connecting.Add(key);
        }

        try
        {
            if (oldSession != null)
            {
                oldSession.Disconnect();
                await oldSession.WaitForCleanupAsync();
            }
            await DrainRetiredAsync(key);

            if (string.IsNullOrEmpty(device.AdbSerial)) return null;

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

            var initialMode = SavedModeResolver?.Invoke(liveDevice) ?? ControllerMode.Ds4;
            var session = new GamepadSession(
                liveDevice,
                // Resolve the live transport per send so a re-attached link keeps
                // flowing through the same session without re-creating it.
                msg => SendUsbAsync(key, msg),
                OnAudioSendStart,
                _gkContext,
                HidMaestroAvailable,
                initialMode);

            session.OnReady += s => OnSessionStarted?.Invoke(s);
            session.OnConnectionLost += s => OnSessionConnectionLost?.Invoke(s.Device.IpString);
            session.OnDeviceInfoUpdated += s => OnDeviceInfoUpdated?.Invoke(s.Device);
            session.OnRebootRequired += _ => { RebootRequired = true; OnRebootRequired?.Invoke(); };
            session.OnUsbipInstallRequired += _ => OnUsbipInstallRequired?.Invoke();
            session.OnDisconnected += s =>
            {
                _sessions.TryRemove(s.Device.Key, out _);
                Retire(s);
                OnSessionEnded?.Invoke(s);
            };

            lock (_connectLock)
            {
                if (_sessions.TryGetValue(key, out var current))
                {
                    session.Disconnect();
                    return current;
                }
                _sessions[key] = session;
            }

            session.Start();

            if (!await OpenUsbTransportAsync(key, liveDevice, session, connectTimeoutMs))
            {
                // Could not open the link — roll the just-registered session back
                // so the UI does not show a connected device with no transport.
                lock (_connectLock) _sessions.TryRemove(key, out _);
                session.Disconnect();
                return null;
            }

            return session;
        }
        catch
        {
            return null;
        }
        finally
        {
            lock (_connectLock) _connecting.Remove(key);
        }
    }

    /// <summary>Sends a message to the phone over the USB link currently bound to
    /// <paramref name="key"/>, or drops it when no link is up. Resolving the
    /// connection lazily lets a session survive a transport swap.</summary>
    private Task SendUsbAsync(string key, IMessage msg)
    {
        var conn = GetUsbConnection(key);
        return conn == null
            ? Task.CompletedTask
            : conn.SendAsync(TypeServerToClient, msg.ToByteArray());
    }

    private UsbConnection? GetUsbConnection(string key)
    {
        lock (_usbLock)
        {
            return _usbConnections.TryGetValue(key, out var c) ? c : null;
        }
    }

    private (UsbConnection? conn, int? port) RemoveUsbConnection(string key)
    {
        UsbConnection? conn = null;
        int? port = null;
        lock (_usbLock)
        {
            if (_usbConnections.TryGetValue(key, out var c)) { _usbConnections.Remove(key); conn = c; }
            if (_usbForwardPorts.TryGetValue(key, out var p)) { _usbForwardPorts.Remove(key); port = p; }
        }
        return (conn, port);
    }

    /// <summary>Opens (or re-opens) the adb forward + TCP link for an existing
    /// session. Binds the new <see cref="UsbConnection"/>'s events to the live
    /// session and records the assigned forward port so it can be removed again.</summary>
    private async Task<bool> OpenUsbTransportAsync(string key, DiscoveredDevice device, GamepadSession session, int connectTimeoutMs)
    {
        if (string.IsNullOrEmpty(device.AdbSerial)) return false;
        if (!_adb.Available && !_adb.TryInitialize()) return false;
        _adb.EnsureServer();

        var localPort = _adb.AddForward(device.AdbSerial, AdbManager.DevicePort);
        if (localPort == null) return false;

        TcpClient? client = null;
        UsbConnection? usbConn = null;
        try
        {
            client = new TcpClient();
            var connectTask = client.ConnectAsync(IPAddress.Loopback, localPort.Value);
            var completed = await Task.WhenAny(connectTask, Task.Delay(connectTimeoutMs));
            if (completed != connectTask)
            {
                try { client.Dispose(); } catch { }
                _adb.RemoveForward(device.AdbSerial, localPort.Value);
                return false;
            }
            await connectTask;
            client.NoDelay = true;

            usbConn = new UsbConnection(client);
            var conn = usbConn;
            var port = localPort.Value;
            var serial = device.AdbSerial;

            conn.OnClientToServer += cts => session.HandleClientToServer(cts);
            conn.OnGamepadInput += input => session.ReportGamepadInput(input);
            conn.OnClosed += () => OnUsbConnectionClosed(key, conn, serial, port);

            lock (_usbLock)
            {
                _usbConnections[key] = conn;
                _usbForwardPorts[key] = port;
            }

            _ = conn.StartReceiveLoopAsync();
            await conn.SendAsync(TypeServerToClient, BuildServerHello().ToByteArray());
            return true;
        }
        catch
        {
            usbConn?.Dispose();
            try { client?.Dispose(); } catch { }
            _adb.RemoveForward(device.AdbSerial, localPort.Value);
            return false;
        }
    }

    /// <summary>Called when a USB link drops. Cleans up the transport but does NOT
    /// tear the session down: exactly like a phone that stops broadcasting over
    /// WiFi, the emulated controller is kept (the session watchdog neutralises its
    /// inputs) and the scan loop re-attaches the link when the device returns.</summary>
    private void OnUsbConnectionClosed(string key, UsbConnection conn, string? serial, int port)
    {
        bool removedMine = false;
        UsbConnection? toDispose = null;
        lock (_usbLock)
        {
            if (_usbConnections.TryGetValue(key, out var c) && ReferenceEquals(c, conn))
            {
                _usbConnections.Remove(key);
                removedMine = true;
                toDispose = c;
            }
        }
        toDispose?.Dispose();

        if (!removedMine) return;

        int? forwardPort = null;
        lock (_usbLock)
        {
            if (_usbForwardPorts.TryGetValue(key, out var p)) { _usbForwardPorts.Remove(key); forwardPort = p; }
        }
        if (!string.IsNullOrEmpty(serial) && forwardPort != null)
            _adb.RemoveForward(serial, forwardPort.Value);
    }

    /// <summary>Re-attaches the USB transport for a session whose link dropped,
    /// without destroying its emulated controller. Runs off the scan loop; guarded
    /// so it never races a manual connect or a second re-attach.</summary>
    private void TryReattachUsbSession(string key, DiscoveredDevice device)
    {
        _ = Task.Run(async () =>
        {
            GamepadSession? session;
            lock (_connectLock)
            {
                if (_connecting.Contains(key)) return;
                if (!_sessions.TryGetValue(key, out session) || !session.IsConnected) return;
                if (GetUsbConnection(key) != null) return; // link already live
                _connecting.Add(key);
            }
            try
            {
                if (!await OpenUsbTransportAsync(key, device, session, 5000)) return;

                // The session may have been disconnected while we opened the link
                // (user pressed disconnect); don't leave an orphan transport.
                lock (_connectLock)
                {
                    if (!_sessions.TryGetValue(key, out var cur) || !ReferenceEquals(cur, session))
                    {
                        var (c, p) = RemoveUsbConnection(key);
                        c?.Dispose();
                        if (p != null && !string.IsNullOrEmpty(device.AdbSerial))
                            _adb.RemoveForward(device.AdbSerial, p.Value);
                    }
                }
            }
            catch { }
            finally
            {
                lock (_connectLock) _connecting.Remove(key);
            }
        });
    }

    public async Task SendToPhoneAsync(IPEndPoint ep, byte type, IMessage msg)
    {
        if (_disposed) return;
        EnqueueSend(ep, type, msg);
    }

    public async Task SendDisconnectAsync(DiscoveredDevice device)
    {
        var key = device.Key;
        if (!_sessions.TryGetValue(key, out var session)) return;

        var packet = new ServerToClient
        {
            Disconnect = new Disconnect { Reason = "User disconnected" }
        };
        if (device.IsUsb)
        {
            await session.SendAsync(packet);
            var (conn, port) = RemoveUsbConnection(key);
            conn?.Dispose();
            if (!string.IsNullOrEmpty(device.AdbSerial) && port != null)
                _adb.RemoveForward(device.AdbSerial, port.Value);
        }
        else
        {
            EnqueueSend(new IPEndPoint(device.IpAddress, Port), TypeServerToClient, packet);
        }
        session.Disconnect();
    }

    public async Task DisconnectAllAsync()
    {
        var tasks = new List<Task>();
        foreach (var session in _sessions.Values)
        {
            CleanupUsbConnection(session);
            session.Disconnect();
            tasks.Add(session.WaitForCleanupAsync());
        }
        _sessions.Clear();
        await Task.WhenAll(tasks);
    }

    public void DisconnectAll()
    {
        foreach (var session in _sessions.Values)
        {
            CleanupUsbConnection(session);
            session.Disconnect();
        }
        _sessions.Clear();
    }

    private void CleanupUsbConnection(GamepadSession session)
    {
        if (!session.Device.IsUsb) return;
        var (conn, port) = RemoveUsbConnection(session.Device.Key);
        conn?.Dispose();
        if (!string.IsNullOrEmpty(session.Device.AdbSerial) && port != null)
            _adb.RemoveForward(session.Device.AdbSerial, port.Value);
    }

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