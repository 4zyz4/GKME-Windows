using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using GamepadEmu.Protocol;
using Google.Protobuf;
using HIDMaestro;

namespace GKME.Core;

public class NetworkService : IDisposable
{
    public const int Port = 37284;
    private const string BroadcastPrefix = "GAMEPAD_SERVER:";

    private const byte TypeClientToServer = 0x00;
    private const byte TypeServerToClient = 0x01;
    private const byte TypeGamepadInput = 0x02;

    private readonly ConcurrentDictionary<string, DiscoveredDevice> _discovered = new();
    private readonly ConcurrentDictionary<string, GamepadSession> _sessions = new();
    private readonly HMContext _hmContext = new();

    public bool HidMaestroAvailable { get; private set; }

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
        Environment.SetEnvironmentVariable("HIDMAESTRO_TIMEOUT_SCALE", "0.1");
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
            
            // External profile override: profiles/ next to the app wins over
            // the embedded catalog for the same profile ID (external loads
            // register first, and LoadDefaultProfiles skips existing IDs).
            // Lets us fix a profile's USB strings (e.g. DualSense iProduct)
            // without rebuilding HIDMaestro.Core.
            var overrideDir = Path.Combine(AppContext.BaseDirectory, "profiles");
            if (Directory.Exists(overrideDir))
            {
                try { _hmContext.LoadProfilesFromDirectory(overrideDir); }
                catch (Exception ex) { System.IO.File.AppendAllText(log, $"[InitHidMaestro] LoadProfilesFromDirectory failed: {ex}\n"); }
            }

            int loaded = _hmContext.LoadDefaultProfiles();
            if (loaded == 0)
            {
                System.IO.File.AppendAllText(log, "[InitHidMaestro] LoadDefaultProfiles returned 0\n");
                return false;
            }

            PatchTimeoutScale(_hmContext);

            // Always ensure the SDK's embedded driver version is deployed.
            // InstallDriver() has a same-version fast path (~40-60 ms) and only
            // runs the full pipeline when the installed driver actually differs.
            _hmContext.InstallDriver();

            HidMaestroAvailable = true;
            return true;
        }
        catch (Exception ex)
        {
            try { System.IO.File.AppendAllText("gkme_error.log", $"[InitHidMaestro] EXCEPTION: {ex}\n"); } catch { }
            HidMaestroAvailable = false;
            return false;
        }
    }

    private static void PatchTimeoutScale(HMContext ctx)
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
            if (val != null && f.FieldType.IsClass && f.FieldType.Namespace?.StartsWith("HIDMaestro") == true)
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

    public IReadOnlyCollection<DiscoveredDevice> DiscoveredDevices =>
        _discovered.Values.Where(d => (DateTime.UtcNow - d.LastSeen).TotalSeconds < 15).ToList();

    public IReadOnlyCollection<GamepadSession> ActiveSessions => _sessions.Values.ToList();

    public event Action<DiscoveredDevice>? OnDeviceDiscovered;
    public event Action<GamepadSession>? OnSessionStarted;
    public event Action<GamepadSession>? OnSessionEnded;
    public event Action<string>? OnSessionConnectionLost;

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
            Name = "HMNetRecv",
            Priority = ThreadPriority.AboveNormal,
        };
        _recvThread.Start();

        // 发包线程：专用 Thread，高于正常优先级，从队列取包发 UDP
        _sendThread = new Thread(SendLoop)
        {
            IsBackground = true,
            Name = "HMNetSend",
            Priority = ThreadPriority.AboveNormal,
        };
        _sendThread.Start();
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
        var deviceName = msg[BroadcastPrefix.Length..];

        var phoneEp = new IPEndPoint(remoteEp.Address, Port);
        var key = GetSessionKey(remoteEp);

        var device = _discovered.GetOrAdd(key, _ => new DiscoveredDevice
        {
            DeviceName = deviceName,
            IpAddress = phoneEp.Address,
            Port = Port,
        });
        device.DeviceName = deviceName;
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
                _hmContext,
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
        EnqueueSend(new IPEndPoint(device.IpAddress, Port), TypeServerToClient, packet);
        session.Disconnect();
    }

    public async Task DisconnectAllAsync()
    {
        var tasks = new List<Task>();
        foreach (var session in _sessions.Values)
        {
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
            session.Disconnect();
        }
        _sessions.Clear();
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
        _hmContext.Dispose();
    }
}