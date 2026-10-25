using GamepadEmu.Protocol;
using Google.Protobuf;

namespace GKME.Core.Transport;

/// <summary>
/// In-process transport: no socket, no adb, no browser — just a byte lane.
/// Downlink messages are recorded in <see cref="Sent"/>, and uplink messages
/// are injected with <see cref="Receive"/> / <see cref="ReceiveInput"/>. This
/// is the adapter that makes session lifetime (connect, replace, retire,
/// disconnect) testable without a phone attached.
/// </summary>
public sealed class InMemorySessionTransport : ISessionTransport
{
    private readonly object _sync = new();
    private readonly List<IMessage> _sent = new();
    private GamepadSession? _session;

    /// <summary>Every message the session has sent downlink, in order.</summary>
    public IReadOnlyList<IMessage> Sent
    {
        get { lock (_sync) return _sent.ToList(); }
    }

    public Task SendAsync(IMessage message)
    {
        lock (_sync) _sent.Add(message);
        return Task.CompletedTask;
    }

    public Task<bool> AttachAsync(GamepadSession session, CancellationToken cancellationToken = default)
    {
        lock (_sync) _session = session;
        return Task.FromResult(true);
    }

    public void Detach()
    {
        lock (_sync) _session = null;
    }

    /// <summary>Delivers a ClientToServer message as if the phone had sent it.</summary>
    public void Receive(ClientToServer message)
    {
        GamepadSession? session;
        lock (_sync) session = _session;
        session?.HandleClientToServer(message);
    }

    /// <summary>Delivers a gamepad input frame as if the phone had sent it.</summary>
    public void ReceiveInput(GamepadInput input)
    {
        GamepadSession? session;
        lock (_sync) session = _session;
        session?.ReportGamepadInput(input);
    }

    /// <summary>Drops the recorded downlink log.</summary>
    public void Clear()
    {
        lock (_sync) _sent.Clear();
    }
}
