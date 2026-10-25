using Google.Protobuf;

namespace GKME.Core.Transport;

/// <summary>WiFi transport: the downlink is queued onto the UDP send thread,
/// and the phone's packets arrive through the receive loop in
/// <see cref="NetworkService"/>, which routes them by remote endpoint. Nothing
/// to open or close per session.</summary>
public sealed class UdpSessionTransport : ISessionTransport
{
    private readonly Action<IMessage> _enqueue;

    public UdpSessionTransport(Action<IMessage> enqueue) => _enqueue = enqueue;

    public Task SendAsync(IMessage message)
    {
        _enqueue(message);
        return Task.CompletedTask;
    }

    public Task<bool> AttachAsync(GamepadSession session, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public void Detach() { }
}
