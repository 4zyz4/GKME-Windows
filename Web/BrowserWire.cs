using System;
using System.Security.Cryptography;
using System.Text;

namespace GKME.Web;

/// <summary>
/// The browser wire format, in one place. Two things differ between the browser
/// and the Android client: the WebSocket upgrade handshake, and the fact that
/// each binary frame carries its protocol type in byte 0 —
/// <c>[1 byte type][protobuf payload]</c>, using the same type constants as the
/// UDP transport (<see cref="Core.NetworkService.TypeClientToServer"/> /
/// <see cref="Core.NetworkService.TypeGamepadInput"/> uplink,
/// <see cref="Core.NetworkService.TypeServerToClient"/> downlink).
///
/// Pure functions, no sockets: the handshake key and the frame layout can both
/// be checked against their specs without a browser.
/// </summary>
public static class BrowserWire
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    /// <summary>RFC 6455 <c>Sec-WebSocket-Accept</c>: base64(SHA1(key + GUID)).</summary>
    public static string AcceptKey(string clientKey) =>
        Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(clientKey + WebSocketGuid)));

    /// <summary>Builds the binary frame sent to the browser.</summary>
    public static byte[] Encode(byte type, byte[] payload)
    {
        var frame = new byte[payload.Length + 1];
        frame[0] = type;
        if (payload.Length > 0) Buffer.BlockCopy(payload, 0, frame, 1, payload.Length);
        return frame;
    }

    /// <summary>Splits a binary frame received from the browser. Returns false
    /// for an empty frame, which carries no type byte.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> frame, out byte type, out byte[] payload)
    {
        type = 0;
        payload = Array.Empty<byte>();
        if (frame.Length < 1) return false;

        type = frame[0];
        if (frame.Length > 1)
        {
            payload = new byte[frame.Length - 1];
            frame[1..].CopyTo(payload);
        }
        return true;
    }
}
