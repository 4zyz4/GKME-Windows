using Google.Protobuf;

namespace GKME.Core.Transport;

/// <summary>Browser transport: the downlink writes a WebSocket frame back to the
/// browser; the browser's frames are pushed in by <see cref="Web.BrowserBridge"/>,
/// which owns the socket. Nothing to open or close per session here either.</summary>
public sealed class WebSessionTransport : ISessionTransport
{
    private readonly Func<IMessage, Task> _sendToBrowser;

    public WebSessionTransport(Func<IMessage, Task> sendToBrowser) => _sendToBrowser = sendToBrowser;

    public Task SendAsync(IMessage message) => _sendToBrowser(message);

    public Task<bool> AttachAsync(GamepadSession session, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public void Detach() { }
}
