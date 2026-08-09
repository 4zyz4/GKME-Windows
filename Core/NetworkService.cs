using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using GamepadEmu.Protocol;
using Google.Protobuf;
using HIDMaestro;
using Nefarius.ViGEm.Client;

namespace GamepadEmuHost.Core;

public class NetworkService : IDisposable
{
    private const int Port = 37284;
    private const string BroadcastPrefix = "GAMEPAD_SERVER:";

    private const byte TypeClientToServer = 0x00;
    private const byte TypeServerToClient = 0x01;
    private const byte TypeGamepadInput = 0x02;

    private readonly ConcurrentDictionary<string, DiscoveredDevice> _discovered = new();
    private readonly ConcurrentDictionary<string, GamepadSession> _sessions = new();
    private readonly ViGEmClient _viGEm = new();
    private readonly HMContext _hmContext = new();

    public bool HidMaestroAvailable { get; private set; }

    static NetworkService()
    {
        // HIDMaestro's TimeoutScale only honors [0.1, 100.0]; anything below the
        // floor is silently ignored (Factor falls back to 1.0). 0.1 is the floor
        // and shrinks every internal device-removal wait budget by 10x.
        Environment.SetEnvironmentVariable("HIDMAESTRO_TIMEOUT_SCALE", "0.1");
    }

    public static bool IsVigemBusInstalled()
    {
        return File.Exists(@"C:\Windows\System32\drivers\ViGEmBus.sys");
    }
    private CancellationTokenSource? _listenerCts;
    private Task? _listenerTask;
    private UdpClient? _listener;
    private bool _disposed;

    public bool InitializeHidMaestro()
    {
        try
        {
            // External profile override: profiles/ next to the app wins over
            // the embedded catalog for the same profile ID (external loads
            // register first, and LoadDefaultProfiles skips existing IDs).
            // Lets us fix a profile's USB strings (e.g. DualSense iProduct)
            // without rebuilding HIDMaestro.Core.
            var overrideDir = Path.Combine(AppContext.BaseDirectory, "profiles");
            if (Directory.Exists(overrideDir))
            {
                try { _hmContext.LoadProfilesFromDirectory(overrideDir); }
                catch { }
            }

            int loaded = _hmContext.LoadDefaultProfiles();
            if (loaded == 0)
                return false;

            PatchTimeoutScale(_hmContext);

            // Always ensure the SDK's embedded driver version is deployed.
            // InstallDriver() has a same-version fast path (~40-60 ms) and only
            // runs the full pipeline when the installed driver actually differs.
            _hmContext.InstallDriver();

            HidMaestroAvailable = true;
            return true;
        }
        catch
        {
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
        var t = obj.GetType();

        SetFieldInt(t, obj, "perAttemptTimeoutMs", 500, flags);
        SetFieldInt(t, obj, "timeoutMs", 500, flags);
        SetFieldInt(t, obj, "callbackTimeoutMs", 500, flags);

        foreach (var f in t.GetFields(flags))
        {
            var val = f.GetValue(obj);
            if (val != null && f.FieldType.IsClass && f.FieldType.Namespace?.StartsWith("HIDMaestro") == true)
                PatchTimeoutScaleRecursive(val, flags, seen);
        }
    }

    private static void SetFieldInt(Type t, object obj, string name, int value, BindingFlags flags)
    {
        var f = t.GetField(name, flags);
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

    public void StartDiscovery()
    {
        if (_listenerCts != null) return;
        _listenerCts = new CancellationTokenSource();
        _listener = new UdpClient(new IPEndPoint(IPAddress.Any, Port));
        _listenerTask = Task.Run(() => ListenLoop(_listenerCts.Token));
    }

    public void StopDiscovery()
    {
        if (_listenerCts != null)
        {
            _listenerCts.Cancel();
            _listenerCts.Dispose();
            _listenerCts = null;
        }
        try { _listenerTask?.GetAwaiter().GetResult(); } catch { }
        _listenerTask = null;
        try { _listener?.Close(); } catch { }
        _listener = null;
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        var buf = new byte[65535];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _listener!.ReceiveAsync(ct);
                var data = result.Buffer;
                if (data.Length == 0) continue;

                // Plain-text discovery broadcast from phone
                var text = Encoding.UTF8.GetString(data);
                if (text.StartsWith(BroadcastPrefix))
                {
                    HandleDiscovery(result.RemoteEndPoint, text);
                    continue;
                }

                var type = data[0];
                var payload = data.AsSpan(1).ToArray();

                switch (type)
                {
                    case TypeClientToServer:
                        RouteClientToServer(result.RemoteEndPoint, payload);
                        break;
                    case TypeGamepadInput:
                        RouteGamepadInput(result.RemoteEndPoint, payload);
                        break;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UDP] SocketException: {ex.Message}");
                await Task.Delay(1000, ct);
            }
            catch
            {
                await Task.Delay(1000, ct);
            }
        }
    }

    private void HandleDiscovery(IPEndPoint remoteEp, string msg)
    {
        var deviceName = msg[BroadcastPrefix.Length..];

        var phoneEp = new IPEndPoint(remoteEp.Address, Port);
        var key = $"{phoneEp}";

        var device = _discovered.GetOrAdd(key, _ => new DiscoveredDevice
        {
            DeviceName = deviceName,
            IpAddress = phoneEp.Address,
            Port = Port,
        });
        device.DeviceName = deviceName;
        device.LastSeen = DateTime.UtcNow;

        OnDeviceDiscovered?.Invoke(device);
    }

    private readonly object _autoLock = new();
    private readonly HashSet<string> _autoConnecting = new();

    private void RouteClientToServer(IPEndPoint remoteEp, byte[] payload)
    {
        var key = $"{new IPEndPoint(remoteEp.Address, Port)}";
        try
        {
            var cts = ClientToServer.Parser.ParseFrom(payload);
            if (_sessions.TryGetValue(key, out var session))
            {
                session.HandleClientToServer(cts);
            }
            else if (cts.PayloadCase == ClientToServer.PayloadOneofCase.Hello)
            {
                // Phone is (re)connecting but no session exists (session was torn
                // down on this side). Rebuild it automatically so reconnect works
                // even after a PC-side teardown/restart.
                _ = AutoConnectAsync(remoteEp, key, cts.Hello.DeviceName);
            }
        }
        catch { }
    }

    private async Task AutoConnectAsync(IPEndPoint remoteEp, string key, string deviceName)
    {
        lock (_autoLock)
        {
            if (_sessions.ContainsKey(key) || !_autoConnecting.Add(key)) return;
        }
        try
        {
            var device = new DiscoveredDevice
            {
                DeviceName = string.IsNullOrWhiteSpace(deviceName)
                    ? remoteEp.Address.ToString()
                    : deviceName,
                IpAddress = remoteEp.Address,
            };
            await ConnectAsync(device);
        }
        finally
        {
            lock (_autoLock) _autoConnecting.Remove(key);
        }
    }

    private void RouteGamepadInput(IPEndPoint remoteEp, byte[] payload)
    {
        try
        {
            var input = GamepadInput.Parser.ParseFrom(payload);
            var key = $"{new IPEndPoint(remoteEp.Address, Port)}";

            if (_sessions.TryGetValue(key, out var session))
            {
                session.HandleUdpGamepadInput(input);
            }
        }
        catch { }
    }

    public async Task<GamepadSession?> ConnectAsync(DiscoveredDevice device, int connectTimeoutMs = 5000)
    {
        try
        {
            // Disconnect existing session for this device if any, and wait for
            // its controllers to be fully torn down before creating a new one.
            // Otherwise a slow HIDMaestro teardown (~seconds) overlaps with the
            // new session's controllers and Windows briefly shows two pads.
            if (_sessions.TryRemove(device.Key, out var oldSession))
            {
                oldSession.Disconnect();
                await oldSession.WaitForCleanupAsync();
            }

            var phoneEp = new IPEndPoint(device.IpAddress, Port);

            var hello = new ServerToClient
            {
                ServerHello = new ServerHello
                {
                    ProtocolVersion = 1,
                    HostName = Environment.MachineName,
                    MaxDownlinkRateHz = 1000,
                    RecommendedUplinkIntervalUs = 0,
                }
            };
            await SendToPhoneAsync(phoneEp, TypeServerToClient, hello);

            var session = new GamepadSession(device, msg => SendToPhoneAsync(phoneEp, TypeServerToClient, msg), _viGEm, _hmContext, HidMaestroAvailable);
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
                OnSessionEnded?.Invoke(s);
            };

            session.Start();
            _sessions[device.Key] = session;

            return session;
        }
        catch
        {
            return null;
        }
    }

    public async Task SendToPhoneAsync(IPEndPoint ep, byte type, IMessage msg)
    {
        if (_listener == null || _disposed) return;
        try
        {
            var payload = msg.ToByteArray();
            var data = new byte[1 + payload.Length];
            data[0] = type;
            Buffer.BlockCopy(payload, 0, data, 1, payload.Length);
            await _listener.SendAsync(data, data.Length, ep);
        }
        catch { }
    }

    public async Task SendDisconnectAsync(DiscoveredDevice device)
    {
        var key = device.Key;
        if (!_sessions.TryGetValue(key, out var session)) return;

        var packet = new ServerToClient
        {
            Disconnect = new Disconnect { Reason = "User disconnected" }
        };
        await SendToPhoneAsync(new IPEndPoint(device.IpAddress, Port), TypeServerToClient, packet);
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
        DisconnectAllAsync().GetAwaiter().GetResult();
        _viGEm.Dispose();
        _hmContext.Dispose();
    }
}
