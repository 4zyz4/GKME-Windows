using System.Diagnostics.CodeAnalysis;
using GKME.Core.Transport;
using Google.Protobuf;

namespace GKME.Core;

/// <summary>Creates a session for an intake connect. The intake supplies the
/// downlink that carries ServerToClient messages to the phone; everything else
/// about construction stays with the caller.</summary>
public delegate GamepadSession SessionFactory(DiscoveredDevice device, Func<IMessage, Task> downlink);

/// <summary>
/// The one entry point for bringing a phone online, and the only place that
/// knows the session-lifetime rules:
///
/// <list type="bullet">
/// <item>connects for the same device key are serialised, so two clicks (or a
/// click racing an auto-reconnect) cannot each build a controller;</item>
/// <item>an already-live session is handed back instead of a second one;</item>
/// <item>the previous session's virtual controller is torn down and awaited
/// before a replacement may be created, and sessions retired by a manual
/// disconnect are drained the same way — this is what keeps Windows from ever
/// showing two virtual gamepads at once;</item>
/// <item>session events are wired, the session is registered, started, and its
/// transport attached.</item>
/// </list>
///
/// Transports sit behind <see cref="ISessionTransport"/> and only move bytes,
/// so a new way of reaching a phone is one adapter rather than a fourth copy of
/// the assembly ritual.
/// </summary>
public sealed class SessionIntake
{
    private readonly SessionFactory _createSession;
    private readonly Func<bool>? _isConnectionBlocked;

    // Guards _sessions, _transports and _connecting.
    private readonly object _connectLock = new();
    private readonly HashSet<string> _connecting = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GamepadSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<GamepadSession, ISessionTransport> _transports = new();

    // Sessions already evicted from the ledger whose virtual controller is still
    // being destroyed in the background, keyed by device key. A new connect
    // waits for them, so the old and new controller never coexist.
    private readonly object _retireLock = new();
    private readonly Dictionary<string, List<GamepadSession>> _retired = new(StringComparer.Ordinal);

    public SessionIntake(SessionFactory createSession, Func<bool>? isConnectionBlocked = null)
    {
        _createSession = createSession;
        _isConnectionBlocked = isConnectionBlocked;
    }

    public event Action<GamepadSession>? SessionStarted;
    public event Action<GamepadSession>? SessionEnded;
    /// <summary>Carries the device's <see cref="DiscoveredDevice.IpString"/>.</summary>
    public event Action<string>? SessionConnectionLost;
    public event Action<DiscoveredDevice>? DeviceInfoUpdated;
    public event Action<GamepadSession>? RebootRequired;
    public event Action<GamepadSession>? UsbipInstallRequired;

    public IReadOnlyCollection<GamepadSession> ActiveSessions
    {
        get { lock (_connectLock) return _sessions.Values.ToList(); }
    }

    public bool Contains(string key)
    {
        lock (_connectLock) return _sessions.ContainsKey(key);
    }

    public bool TryGet(string key, [NotNullWhen(true)] out GamepadSession? session)
    {
        lock (_connectLock) return _sessions.TryGetValue(key, out session);
    }

    /// <summary>True when <paramref name="session"/> is still the registered
    /// session for its device (i.e. nobody disconnected or replaced it).</summary>
    public bool IsCurrent(GamepadSession session)
    {
        lock (_connectLock)
            return _sessions.TryGetValue(session.Device.Key, out var current)
                && ReferenceEquals(current, session);
    }

    /// <summary>The transport bound to a live session, or null once it ended.</summary>
    public ISessionTransport? GetTransport(GamepadSession session)
    {
        lock (_connectLock)
            return _transports.TryGetValue(session, out var transport) ? transport : null;
    }

    /// <summary>
    /// Reserves <paramref name="key"/> for an exclusive operation (a transport
    /// re-attach) so it cannot race a user-initiated connect. Returns null when
    /// a connect or another re-attach already holds the key; dispose the result
    /// to release it.
    /// </summary>
    public IDisposable? TryReserve(string key)
    {
        lock (_connectLock)
        {
            if (!_connecting.Add(key)) return null;
            return new Reservation(this, key);
        }
    }

    /// <summary>
    /// Connects (or returns the live session for) <paramref name="device"/> over
    /// <paramref name="transport"/>. Returns null when the connection is refused
    /// (reboot pending, already connecting) or the transport cannot open its
    /// link; a half-established session is rolled back in that case.
    /// </summary>
    public async Task<GamepadSession?> ConnectAsync(DiscoveredDevice device, ISessionTransport transport)
    {
        // e.g. a USBip upgrade is waiting for a reboot: Windows will not load
        // the transport, so every new connection is refused up front.
        if (_isConnectionBlocked?.Invoke() == true) return null;

        var key = device.Key;
        GamepadSession? oldSession = null;

        lock (_connectLock)
        {
            if (_connecting.Contains(key)) return null;
            if (_sessions.TryGetValue(key, out var existing) && existing.IsConnected)
                return existing;
            _sessions.Remove(key, out oldSession);
            _connecting.Add(key);
        }

        try
        {
            // Tear the old controller down and wait for HIDMaestro to release
            // it; creating the replacement any earlier makes Windows briefly
            // show both gamepads.
            if (oldSession != null)
            {
                oldSession.Disconnect();
                await oldSession.WaitForCleanupAsync().ConfigureAwait(false);
            }

            // Sessions retired by a manual disconnect are no longer in the
            // ledger but their controller may still be disappearing.
            await DrainRetiredAsync(key).ConfigureAwait(false);

            var session = _createSession(device, transport.SendAsync);
            Wire(session, transport);

            lock (_connectLock)
            {
                // Register as soon as possible so a concurrent flow cannot build
                // a second controller. If one won the race, drop ours and reuse
                // the live session.
                if (_sessions.TryGetValue(key, out var current))
                {
                    session.Disconnect();
                    return current;
                }
                _sessions[key] = session;
                _transports[session] = transport;
            }

            session.Start();

            if (!await transport.AttachAsync(session).ConfigureAwait(false))
            {
                // Roll the half-established session back: the UI must never show
                // a connected device with no transport behind it.
                lock (_connectLock)
                {
                    if (_sessions.TryGetValue(key, out var current) && ReferenceEquals(current, session))
                    {
                        _sessions.Remove(key);
                        _transports.Remove(session);
                    }
                }
                transport.Detach();
                session.Disconnect();
                return null;
            }

            // Let the phone resume the handshake even if it has not sent a Hello
            // yet (e.g. it restarted its link during a reconnect).
            await session.SendServerHelloAsync().ConfigureAwait(false);
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

    /// <summary>Disconnects every session and waits for each virtual controller
    /// to be released.</summary>
    public async Task DisconnectAllAsync()
    {
        var snapshot = Snapshot();
        foreach (var session in snapshot) session.Disconnect();
        await Task.WhenAll(snapshot.Select(s => s.WaitForCleanupAsync())).ConfigureAwait(false);
        lock (_connectLock) _sessions.Clear();
    }

    /// <summary>Disconnects every session without waiting for teardown.</summary>
    public void DisconnectAll()
    {
        var snapshot = Snapshot();
        foreach (var session in snapshot) session.Disconnect();
        lock (_connectLock) _sessions.Clear();
    }

    private List<GamepadSession> Snapshot()
    {
        lock (_connectLock) return _sessions.Values.ToList();
    }

    private void Wire(GamepadSession session, ISessionTransport transport)
    {
        session.OnReady += s => SessionStarted?.Invoke(s);
        session.OnConnectionLost += s => SessionConnectionLost?.Invoke(s.Device.IpString);
        session.OnDeviceInfoUpdated += s => DeviceInfoUpdated?.Invoke(s.Device);
        session.OnRebootRequired += s => RebootRequired?.Invoke(s);
        session.OnUsbipInstallRequired += s => UsbipInstallRequired?.Invoke(s);
        session.OnDisconnected += s =>
        {
            lock (_connectLock)
            {
                // Only evict when this session still owns the key: a session that
                // lost the registration race must not remove the winner.
                if (_sessions.TryGetValue(s.Device.Key, out var current) && ReferenceEquals(current, s))
                    _sessions.Remove(s.Device.Key);
                _transports.Remove(s);
            }

            transport.Detach();
            Retire(s);
            SessionEnded?.Invoke(s);
        };
    }

    /// <summary>Registers a disconnected session whose virtual controller is
    /// still being destroyed, so the next connect for that device waits for it.</summary>
    private void Retire(GamepadSession session)
    {
        lock (_retireLock)
        {
            if (!_retired.TryGetValue(session.Device.Key, out var list))
                _retired[session.Device.Key] = list = new List<GamepadSession>();
            list.Add(session);
        }

        // Leave the register once cleanup completed, so released sessions are
        // not held forever.
        _ = Task.Run(async () =>
        {
            try { await session.WaitForCleanupAsync().ConfigureAwait(false); }
            catch { }
            finally
            {
                lock (_retireLock)
                {
                    if (_retired.TryGetValue(session.Device.Key, out var list))
                    {
                        list.Remove(session);
                        if (list.Count == 0) _retired.Remove(session.Device.Key);
                    }
                }
            }
        });
    }

    /// <summary>Waits for every retired session of <paramref name="key"/> to
    /// finish destroying its virtual controller. Idempotent.</summary>
    private async Task DrainRetiredAsync(string key)
    {
        List<GamepadSession>? list;
        lock (_retireLock)
        {
            if (!_retired.TryGetValue(key, out list)) return;
            _retired.Remove(key);
        }
        try { await Task.WhenAll(list.Select(s => s.WaitForCleanupAsync())).ConfigureAwait(false); }
        catch { }
    }

    private sealed class Reservation : IDisposable
    {
        private readonly SessionIntake _owner;
        private readonly string _key;
        private int _released;

        public Reservation(SessionIntake owner, string key)
        {
            _owner = owner;
            _key = key;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            lock (_owner._connectLock) _owner._connecting.Remove(_key);
        }
    }
}
