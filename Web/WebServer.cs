using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GamepadEmu.Protocol;
using GKME.Core;
using Google.Protobuf;

namespace GKME.Web;

/// <summary>
/// Embedded HTTP(S) + WebSocket server that hosts the GKME-Web controller app
/// and bridges browser input to a <see cref="GamepadSession"/>.
///
/// <para>The browser speaks the exact same protobuf protocol as the Android
/// client; only the framing differs. Each WebSocket binary frame is
/// <c>[1 byte type][protobuf payload]</c> — the same type constants used by the
/// UDP transport (<see cref="NetworkService.TypeClientToServer"/> /
/// <see cref="NetworkService.TypeGamepadInput"/> uplink,
/// <see cref="NetworkService.TypeServerToClient"/> downlink).</para>
///
/// <para>A self-signed certificate is generated at startup so phones on the LAN
/// get a secure context (required for DeviceMotion / Vibration). Browsers will
/// warn about the untrusted certificate; accepting it once is enough.</para>
/// </summary>
public sealed class WebServer : IDisposable
{
    public const int HttpPort = 37285;
    public const int HttpsPort = 37286;

    private readonly NetworkService _service;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _httpListener;
    private TcpListener? _httpsListener;
    private X509Certificate2? _cert;
    private bool _disposed;

    public string? WebRoot { get; private set; }
    public bool Running { get; private set; }
    public string LastError { get; private set; } = "";

    /// <summary>Raised with the primary LAN URL (https) once the server is up.</summary>
    public event Action<string>? OnStarted;

    public WebServer(NetworkService service)
    {
        _service = service;
    }

    public void Start()
    {
        if (Running) return;
        try
        {
            WebRoot = ResolveWebRoot() ?? throw new DirectoryNotFoundException("GKME-Web 文件夹未找到");
            _cert = LoadOrCreateCertificate();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return;
        }

        try
        {
            _httpsListener = new TcpListener(IPAddress.Any, HttpsPort);
            _httpsListener.Start();
            _ = Task.Run(() => AcceptLoopAsync(_httpsListener, tls: true));
        }
        catch (Exception ex)
        {
            LastError = "HTTPS 端口 " + HttpsPort + " 不可用: " + ex.Message;
            _httpsListener = null;
        }

        try
        {
            _httpListener = new TcpListener(IPAddress.Any, HttpPort);
            _httpListener.Start();
            _ = Task.Run(() => AcceptLoopAsync(_httpListener, tls: false));
        }
        catch (Exception ex)
        {
            if (LastError.Length == 0)
                LastError = "HTTP 端口 " + HttpPort + " 不可用: " + ex.Message;
            _httpListener = null;
        }

        Running = _httpsListener != null || _httpListener != null;
        if (Running)
            OnStarted?.Invoke(PrimaryUrl());
    }

    public string PrimaryUrl()
    {
        var ip = GetPrimaryLanAddress();
        return $"https://{ip}:{HttpsPort}/";
    }

    private async Task AcceptLoopAsync(TcpListener listener, bool tls)
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { continue; }

            _ = Task.Run(() => HandleClientAsync(client, tls));
        }
    }

    private async Task HandleClientAsync(TcpClient client, bool tls)
    {
        Stream? stream = null;
        try
        {
            client.NoDelay = true;
            stream = client.GetStream();

            if (tls)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsServerAsync(_cert!, clientCertificateRequired: false,
                    checkCertificateRevocation: false);
                stream = ssl;
            }

            // HTTP/1.1 keep-alive: serve requests on the same socket until the
            // peer closes or upgrades to a WebSocket.
            while (!_cts.IsCancellationRequested)
            {
                var header = await ReadRequestHeaderAsync(stream, _cts.Token);
                if (header == null) break;

                var req = HttpRequest.Parse(header);
                if (req == null || !string.Equals(req.Method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteResponseAsync(stream, 405, "Method Not Allowed", "text/plain",
                        Encoding.UTF8.GetBytes("Only GET is supported."));
                    break;
                }

                if (string.Equals(req.Path, "/ws", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleWebSocketAsync(stream, req);
                    return; // the stream now belongs to the WebSocket
                }

                await ServeStaticAsync(stream, req);
            }
        }
        catch (IOException) { }
        catch (SocketException) { }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "gkme_web.log"),
                    $"{DateTime.UtcNow:O} client error: {ex}\n");
            }
            catch { }
        }
        finally
        {
            try { stream?.Dispose(); } catch { }
            try { client.Close(); } catch { }
        }
    }

    // ────────────────────────── WebSocket bridge ──────────────────────────

    private async Task HandleWebSocketAsync(Stream stream, HttpRequest req)
    {
        if (!req.Headers.TryGetValue("sec-websocket-key", out var key) || string.IsNullOrEmpty(key))
        {
            await WriteResponseAsync(stream, 400, "Bad Request", "text/plain",
                Encoding.UTF8.GetBytes("Missing Sec-WebSocket-Key."));
            return;
        }

        var accept = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));

        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 101 Switching Protocols\r\n");
        sb.Append("Upgrade: websocket\r\n");
        sb.Append("Connection: Upgrade\r\n");
        sb.Append("Sec-WebSocket-Accept: ").Append(accept).Append("\r\n\r\n");
        var handshake = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(handshake, _cts.Token);
        await stream.FlushAsync(_cts.Token);

        using var ws = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null,
            keepAliveInterval: TimeSpan.FromSeconds(20));

        req.Query.TryGetValue("id", out var clientId);
        req.Query.TryGetValue("name", out var clientName);
        if (string.IsNullOrWhiteSpace(clientId)) clientId = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(clientName)) clientName = "Web 浏览器";

        var device = new DiscoveredDevice
        {
            DeviceName = clientName!,
            MacAddress = clientId!,
            IpAddress = IPAddress.Loopback,
            IsWeb = true,
        };

        var sendLock = new SemaphoreSlim(1, 1);

        Task SendToBrowser(IMessage msg) => SendFrameAsync(ws, sendLock, NetworkService.TypeServerToClient, msg.ToByteArray());

        _service.AnnounceDevice(device);
        var session = await _service.ConnectWebAsync(device, SendToBrowser);

        if (session == null)
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.InternalServerError, "connect failed", _cts.Token); } catch { }
            _service.AnnounceDeviceLost(device);
            return;
        }

        try
        {
            using var acc = new MemoryStream();
            var buf = new byte[65536];
            while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), _cts.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (WebSocketException) { break; }

                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.MessageType != WebSocketMessageType.Binary) continue;

                acc.Write(buf, 0, result.Count);
                if (!result.EndOfMessage) continue;

                var frame = acc.ToArray();
                acc.SetLength(0);
                if (frame.Length < 1) continue;

                byte type = frame[0];
                var payload = new byte[frame.Length - 1];
                if (payload.Length > 0) Buffer.BlockCopy(frame, 1, payload, 0, payload.Length);

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
            try { session.Disconnect(); } catch { }
            _service.AnnounceDeviceLost(device);
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch { }
        }
    }

    private static async Task SendFrameAsync(WebSocket ws, SemaphoreSlim sendLock, byte type, byte[] payload)
    {
        if (ws.State != WebSocketState.Open) return;
        var frame = new byte[payload.Length + 1];
        frame[0] = type;
        if (payload.Length > 0) Buffer.BlockCopy(payload, 0, frame, 1, payload.Length);

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

    // ────────────────────────── Static files ──────────────────────────

    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".htm"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".map"] = "application/json; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".ico"] = "image/x-icon",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf",
        [".wasm"] = "application/wasm",
        [".txt"] = "text/plain; charset=utf-8",
        [".webmanifest"] = "application/manifest+json",
    };

    private async Task ServeStaticAsync(Stream stream, HttpRequest req)
    {
        var root = WebRoot!;
        var path = req.Path;
        if (string.IsNullOrEmpty(path) || path == "/") path = "/index.html";

        var relative = Uri.UnescapeDataString(path.TrimStart('/'));
        var full = Path.GetFullPath(Path.Combine(root, relative));

        // Path-traversal guard: the resolved file must stay inside the web root.
        var rootFull = Path.GetFullPath(root);
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            await WriteResponseAsync(stream, 403, "Forbidden", "text/plain",
                Encoding.UTF8.GetBytes("Forbidden."));
            return;
        }

        if (Directory.Exists(full))
            full = Path.Combine(full, "index.html");

        if (!File.Exists(full))
        {
            // SPA-ish fallback: unknown extension-less routes serve index.html.
            var fallback = Path.Combine(rootFull, "index.html");
            if (!Path.HasExtension(relative) && File.Exists(fallback))
                full = fallback;
            else
            {
                await WriteResponseAsync(stream, 404, "Not Found", "text/plain",
                    Encoding.UTF8.GetBytes("Not found: " + path));
                return;
            }
        }

        byte[] bytes;
        try { bytes = await File.ReadAllBytesAsync(full, _cts.Token); }
        catch
        {
            await WriteResponseAsync(stream, 500, "Internal Server Error", "text/plain",
                Encoding.UTF8.GetBytes("Read error."));
            return;
        }

        var ext = Path.GetExtension(full);
        var mime = MimeTypes.TryGetValue(ext, out var m) ? m : "application/octet-stream";
        // Only cache immutable assets. Scripts/styles must revalidate so a web
        // update is picked up without a manual hard-refresh.
        var cacheable = ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp"
            or ".svg" or ".ico" or ".woff" or ".woff2" or ".ttf";
        await WriteResponseAsync(stream, 200, "OK", mime, bytes, cache: cacheable);
    }

    private static async Task WriteResponseAsync(Stream stream, int status, string reason,
        string contentType, byte[] body, bool cache = false)
    {
        var header = new StringBuilder();
        header.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
        header.Append("Content-Type: ").Append(contentType).Append("\r\n");
        header.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        header.Append("Cache-Control: ").Append(cache ? "public, max-age=3600" : "no-cache").Append("\r\n");
        header.Append("Connection: keep-alive\r\n");
        header.Append("\r\n");

        var headerBytes = Encoding.ASCII.GetBytes(header.ToString());
        await stream.WriteAsync(headerBytes);
        if (body.Length > 0) await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    // ────────────────────────── HTTP parsing ──────────────────────────

    private static async Task<string?> ReadRequestHeaderAsync(Stream stream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new byte[4096];
        while (sb.Length < 65536)
        {
            int n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct);
            if (n <= 0) return sb.Length == 0 ? null : sb.ToString();
            sb.Append(Encoding.ASCII.GetString(buf, 0, n));
            if (sb.ToString().Contains("\r\n\r\n")) return sb.ToString();
        }
        return null;
    }

    private sealed class HttpRequest
    {
        public string Method = "";
        public string Path = "";
        public string QueryString = "";
        public Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Query = new(StringComparer.OrdinalIgnoreCase);

        public static HttpRequest? Parse(string header)
        {
            var lines = header.Split("\r\n");
            if (lines.Length == 0) return null;
            var requestLine = lines[0].Split(' ');
            if (requestLine.Length < 2) return null;

            var req = new HttpRequest { Method = requestLine[0] };
            var target = requestLine[1];
            int q = target.IndexOf('?');
            if (q >= 0)
            {
                req.Path = target.Substring(0, q);
                req.QueryString = target.Substring(q + 1);
                foreach (var pair in req.QueryString.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = pair.Split('=', 2);
                    var k = Uri.UnescapeDataString(kv[0]);
                    var v = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
                    req.Query[k] = v;
                }
            }
            else
            {
                req.Path = target;
            }

            for (int i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Length == 0) break;
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var k = line.Substring(0, colon).Trim();
                var v = line.Substring(colon + 1).Trim();
                req.Headers[k] = v;
            }

            return req;
        }
    }

    // ────────────────────────── Paths & certificate ──────────────────────────

    private static string? ResolveWebRoot()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.Combine(baseDir, "web"),
            Path.Combine(baseDir, "GKME-Web"),
            // Dev layout: GKME-Windows\bin\<cfg>\net10.0-windows\win-x64\ → DEV\GKME-Web
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "GKME-Web")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "..", "GKME-Web")),
        };

        foreach (var c in candidates)
        {
            if (File.Exists(Path.Combine(c, "index.html")))
                return c;
        }
        return null;
    }

    private static X509Certificate2 LoadOrCreateCertificate()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GKME");
        Directory.CreateDirectory(dir);
        var pfxPath = Path.Combine(dir, "web-cert.pfx");

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=GKME Web", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName(Environment.MachineName);
        san.AddIpAddress(IPAddress.Loopback);
        foreach (var ip in GetAllLocalAddresses())
            san.AddIpAddress(ip);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));

        using var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));

        // SslStream on Windows needs a persisted private key container; an
        // ephemeral key fails the credential handoff. Persist to a PFX next to
        // the user's local app data and load it back with a real key store.
        try
        {
            var pfx = cert.Export(X509ContentType.Pfx, "gkme");
            File.WriteAllBytes(pfxPath, pfx);
            return X509CertificateLoader.LoadPkcs12(pfx, "gkme",
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
        }
        catch
        {
            // Persisting failed (locked / read-only profile): fall back to an
            // in-memory exportable key.
            var pfx = cert.Export(X509ContentType.Pfx, "gkme");
            return X509CertificateLoader.LoadPkcs12(pfx, "gkme",
                X509KeyStorageFlags.Exportable);
        }
    }

    public static IEnumerable<IPAddress> GetAllLocalAddresses()
    {
        IPAddress[] addrs = Array.Empty<IPAddress>();
        try { addrs = Dns.GetHostAddresses(Dns.GetHostName()); }
        catch { }

        foreach (var a in addrs)
        {
            if (a.AddressFamily == AddressFamily.InterNetwork)
                yield return a;
        }
    }

    public static string GetPrimaryLanAddress()
    {
        foreach (var a in GetAllLocalAddresses())
        {
            var s = a.ToString();
            if (s.StartsWith("169.254.")) continue;
            return s;
        }
        return "127.0.0.1";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        try { _httpListener?.Stop(); } catch { }
        try { _httpsListener?.Stop(); } catch { }
        try { _cert?.Dispose(); } catch { }
        try { _cts.Dispose(); } catch { }
    }
}
