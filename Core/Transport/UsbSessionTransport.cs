using System.Net;
using System.Net.Sockets;
using GamepadEmu.Protocol;
using GKME.Core.Usb;
using Google.Protobuf;

namespace GKME.Core.Transport;

/// <summary>
/// USB transport: an <c>adb forward tcp:0 -&gt; tcp:37284</c> plus one TCP link to
/// that loopback port. The link can be lost (cable replug, phone restart) and
/// re-opened later without touching the session or its virtual controller —
/// <see cref="AttachAsync"/> is therefore callable more than once. Every
/// resource this adapter owns (socket, port forward) is released by
/// <see cref="Detach"/> or when the link closes on its own.
/// </summary>
public sealed class UsbSessionTransport : ISessionTransport
{
    private readonly DiscoveredDevice _device;
    private readonly AdbManager _adb;
    private readonly int _connectTimeoutMs;
    private readonly object _sync = new();

    private UsbConnection? _conn;
    private int _forwardPort;
    private int _detached;

    public UsbSessionTransport(DiscoveredDevice device, AdbManager adb, int connectTimeoutMs)
    {
        _device = device;
        _adb = adb;
        _connectTimeoutMs = connectTimeoutMs;
    }

    /// <summary>True while a link is bound to this transport.</summary>
    public bool IsLinkUp
    {
        get { lock (_sync) return _conn != null; }
    }

    public Task SendAsync(IMessage message)
    {
        UsbConnection? conn;
        lock (_sync) conn = _conn;
        return conn == null
            ? Task.CompletedTask
            : conn.SendAsync(NetworkService.TypeServerToClient, message.ToByteArray());
    }

    public async Task<bool> AttachAsync(GamepadSession session, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _detached) != 0) return false;

        var serial = _device.AdbSerial;
        if (string.IsNullOrEmpty(serial)) return false;
        if (!_adb.Available && !_adb.TryInitialize()) return false;
        _adb.EnsureServer();

        var localPort = _adb.AddForward(serial, AdbManager.DevicePort);
        if (localPort == null) return false;

        TcpClient? client = null;
        UsbConnection? created = null;
        try
        {
            client = new TcpClient();
            var connectTask = client.ConnectAsync(IPAddress.Loopback, localPort.Value);
            var completed = await Task.WhenAny(connectTask, Task.Delay(_connectTimeoutMs, cancellationToken))
                .ConfigureAwait(false);
            if (completed != connectTask)
            {
                try { client.Dispose(); } catch { }
                _adb.RemoveForward(serial, localPort.Value);
                return false;
            }
            await connectTask.ConfigureAwait(false);
            client.NoDelay = true;

            created = new UsbConnection(client);
            var link = created;
            link.OnClientToServer += session.HandleClientToServer;
            link.OnGamepadInput += session.ReportGamepadInput;
            link.OnClosed += () => OnLinkClosed(link);

            lock (_sync)
            {
                // Re-check under the lock: Detach may have run while the link was
                // being opened, in which case it already cleared nothing and this
                // link would be orphaned.
                if (Volatile.Read(ref _detached) == 0)
                {
                    _conn = link;
                    _forwardPort = localPort.Value;
                }
                else
                {
                    link.Dispose();
                }
            }

            if (Volatile.Read(ref _detached) != 0)
            {
                _adb.RemoveForward(serial, localPort.Value);
                return false;
            }

            _ = link.StartReceiveLoopAsync();
            return true;
        }
        catch
        {
            created?.Dispose();
            try { client?.Dispose(); } catch { }
            _adb.RemoveForward(serial, localPort.Value);
            return false;
        }
    }

    public void Detach()
    {
        if (Interlocked.Exchange(ref _detached, 1) != 0) return;

        UsbConnection? conn;
        int port;
        lock (_sync)
        {
            conn = _conn;
            _conn = null;
            port = _forwardPort;
            _forwardPort = 0;
        }

        conn?.Dispose();
        RemoveForward(port);
    }

    /// <summary>The link dropped on its own (phone gone, service restarted).
    /// Forget the socket and its port forward but keep the transport usable, so
    /// the session can be re-attached when the device comes back.</summary>
    private void OnLinkClosed(UsbConnection conn)
    {
        UsbConnection? toDispose = null;
        int port;
        lock (_sync)
        {
            if (!ReferenceEquals(_conn, conn)) return; // superseded link
            _conn = null;
            toDispose = conn;
            port = _forwardPort;
            _forwardPort = 0;
        }

        toDispose.Dispose();
        RemoveForward(port);
    }

    private void RemoveForward(int port)
    {
        var serial = _device.AdbSerial;
        if (port != 0 && !string.IsNullOrEmpty(serial))
            _adb.RemoveForward(serial, port);
    }
}
