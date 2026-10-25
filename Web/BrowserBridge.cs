using System;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GamepadEmu.Protocol;
using GKME.Core;
using GKME.Localization;
using Google.Protobuf;

namespace GKME.Web;

/// <summary>
/// The browser side of the protocol: it upgrades a connection to a WebSocket,
/// turns frames into session traffic, and keeps the browser's card on the
/// desktop in step with the connection.
///
/// <para>It sits below <see cref="StaticHost"/> (which hands it an upgraded
/// socket and nothing else) and above <see cref="SessionIntake"/> (which owns
/// session lifetime): the browser is just another transport, reached through
/// <see cref="NetworkService.ConnectWebAsync"/> like UDP or USB. Publishing the
/// browser as a device — and unpublishing it when the connection or its session
/// ends — is a consequence of that call, not something this module has to drive.</para>
/// </summary>
internal sealed class BrowserBridge : IBrowserSocketHandler
{
    private readonly NetworkService _service;

    public BrowserBridge(NetworkService service) => _service = service;

    public async Task HandleAsync(Stream stream, HttpRequest request, IPAddress peer,
        CancellationToken cancellationToken)
    {
        if (!request.Headers.TryGetValue("sec-websocket-key", out var key) || string.IsNullOrEmpty(key))
        {
            await HttpResponse.WriteAsync(stream, 400, "Bad Request", "text/plain",
                Encoding.UTF8.GetBytes("Missing Sec-WebSocket-Key."));
            return;
        }

        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 101 Switching Protocols\r\n");
        sb.Append("Upgrade: websocket\r\n");
        sb.Append("Connection: Upgrade\r\n");
        sb.Append("Sec-WebSocket-Accept: ").Append(BrowserWire.AcceptKey(key)).Append("\r\n\r\n");
        var handshake = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(handshake, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        using var ws = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null,
            keepAliveInterval: TimeSpan.FromSeconds(20));

        var device = DescribeClient(request, peer);

        var sendLock = new SemaphoreSlim(1, 1);

        Task SendToBrowser(IMessage msg) =>
            SendFrameAsync(ws, sendLock, NetworkService.TypeServerToClient, msg.ToByteArray());

        var session = await _service.ConnectWebAsync(device, SendToBrowser);

        if (session == null)
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.InternalServerError, "connect failed", cancellationToken); } catch { }
            return;
        }

        try
        {
            using var acc = new MemoryStream();
            var buf = new byte[65536];
            while (ws.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), cancellationToken);
                }
                catch (OperationCanceledException) { break; }
                catch (WebSocketException) { break; }

                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.MessageType != WebSocketMessageType.Binary) continue;

                acc.Write(buf, 0, result.Count);
                if (!result.EndOfMessage) continue;

                var frame = acc.ToArray();
                acc.SetLength(0);
                if (!BrowserWire.TryDecode(frame, out var type, out var payload)) continue;

                try
                {
                    switch (type)
                    {
                        case NetworkService.TypeClientToServer:
                            session.HandleClientToServer(ClientToServer.Parser.ParseFrom(payload));
                            break;
                        case NetworkService.TypeGamepadInput:
                            session.ReportGamepadInput(GamepadInput.Parser.ParseFrom(payload));
                            break;
                    }
                }
                catch
                {
                    // Malformed frame — drop it; the connection stays alive.
                }
            }
        }
        catch { }
        finally
        {
            // Ending the session also unpublishes the browser's card.
            try { session.Disconnect(); } catch { }
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch { }
        }
    }

    /// <summary>The browser's device card: a stable client id from the page, and
    /// the peer address it connected from.</summary>
    private static DiscoveredDevice DescribeClient(HttpRequest request, IPAddress peer)
    {
        request.Query.TryGetValue("id", out var clientId);
        request.Query.TryGetValue("name", out var clientName);
        if (string.IsNullOrWhiteSpace(clientId)) clientId = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(clientName)) clientName = Localizer.Instance.Get("Web_ClientName");

        return new DiscoveredDevice
        {
            DeviceName = clientName!,
            MacAddress = clientId!,
            IpAddress = peer,
            IsWeb = true,
        };
    }

    private static async Task SendFrameAsync(WebSocket ws, SemaphoreSlim sendLock, byte type, byte[] payload)
    {
        if (ws.State != WebSocketState.Open) return;
        var frame = BrowserWire.Encode(type, payload);

        await sendLock.WaitAsync();
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Binary, true, CancellationToken.None);
        }
        catch { }
        finally
        {
            sendLock.Release();
        }
    }
}
