using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GamepadEmu.Protocol;
using Google.Protobuf;
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
    private CancellationTokenSource? _listenerCts;
    private Task? _listenerTask;
    private UdpClient? _listener;
    private bool _disposed;

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

    private void RouteClientToServer(IPEndPoint remoteEp, byte[] payload)
    {
        try
        {
            var cts = ClientToServer.Parser.ParseFrom(payload);
            var key = $"{new IPEndPoint(remoteEp.Address, Port)}";

            if (_sessions.TryGetValue(key, out var session))
            {
                session.HandleClientToServer(cts);
            }
        }
        catch { }
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
            // Disconnect existing session for this device if any
            if (_sessions.TryRemove(device.Key, out var oldSession))
            {
                oldSession.Disconnect();
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

            var session = new GamepadSession(device, msg => SendToPhoneAsync(phoneEp, TypeServerToClient, msg), _viGEm);
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
        _viGEm.Dispose();
    }
}
