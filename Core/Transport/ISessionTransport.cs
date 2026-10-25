using Google.Protobuf;

namespace GKME.Core.Transport;

/// <summary>
/// One way of carrying bytes to and from a phone: UDP broadcast, adb-forwarded
/// TCP, or the browser WebSocket bridge.
///
/// The seam is deliberately narrow and byte-oriented: <see cref="SessionIntake"/>
/// owns every rule about session lifetime (one virtual controller per device,
/// teardown ordering, retire/drain), while a transport only knows how to send a
/// downlink message, how to open its link once, and how to release it. Adding a
/// transport therefore means adding one adapter, never another copy of the
/// assembly ritual.
/// </summary>
public interface ISessionTransport
{
    /// <summary>Downlink — delivers one ServerToClient message to the phone.
    /// Must be safe to call after <see cref="Detach"/> (drops silently).</summary>
    Task SendAsync(IMessage message);

    /// <summary>Opens the link and routes the phone's inbound messages into
    /// <paramref name="session"/>. Called once per connection; the USB adapter
    /// also uses it to re-open a dropped link on the same session. Returns
    /// false when the link could not be established.</summary>
    Task<bool> AttachAsync(GamepadSession session, CancellationToken cancellationToken = default);

    /// <summary>Releases the link (socket, port forward, ...). Idempotent;
    /// called once when the session ends.</summary>
    void Detach();
}
