using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GKME.Core;
using GKME.Localization;

namespace GKME.Web;

/// <summary>
/// The one seam between the HTTP host and the browser bridge.
/// <see cref="StaticHost"/> stops being an HTTP server at this point: it hands
/// over the connection, the parsed request that asked for it and the peer
/// address, and whoever implements this owns the socket until it closes.
///
/// Nothing about HTTP crosses this seam, and nothing about WebSocket framing
/// crosses back — which is what lets each side be reasoned about (and tested)
/// on its own.
/// </summary>
public interface IBrowserSocketHandler
{
    Task HandleAsync(Stream stream, HttpRequest request, IPAddress peer, CancellationToken cancellationToken);
}

/// <summary>
/// The embedded HTTP/1.1 host: it accepts connections, keeps them alive across
/// requests, serves the GKME-Web front end from disk, and routes the WebSocket
/// upgrade to a <see cref="IBrowserSocketHandler"/>.
///
/// It knows nothing about gamepads or the protobuf protocol — that starts on the
/// other side of the seam, in <see cref="BrowserBridge"/>.
/// </summary>
public sealed class StaticHost : IDisposable
{
    public const int HttpPort = 37285;

    private readonly IBrowserSocketHandler _browser;
    private readonly string? _webRootOverride;
    private readonly int _port;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _httpListener;
    private bool _disposed;

    /// <param name="browser">Who takes the socket after a <c>/ws</c> upgrade.</param>
    /// <param name="webRoot">Directory to serve; when null the usual locations
    /// next to the executable (and the dev layout) are probed.</param>
    /// <param name="port">Listening port; defaults to the advertised
    /// <see cref="HttpPort"/>. Overridable so a host can be exercised on its own.</param>
    public StaticHost(IBrowserSocketHandler browser, string? webRoot = null, int port = HttpPort)
    {
        _browser = browser;
        _webRootOverride = webRoot;
        _port = port;
    }

    public string? WebRoot { get; private set; }
    public bool Running { get; private set; }
    public string LastError { get; private set; } = "";

    /// <summary>Raised with the primary LAN URL (http) once the server is up.</summary>
    public event Action<string>? OnStarted;

    public void Start()
    {
        if (Running) return;
        try
        {
            WebRoot = _webRootOverride
                ?? ResolveWebRoot()
                ?? throw new DirectoryNotFoundException(Localizer.Instance.Get("Web_FolderNotFound"));
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return;
        }

        try
        {
            _httpListener = new TcpListener(IPAddress.Any, _port);
            _httpListener.Start();
            _ = Task.Run(() => AcceptLoopAsync(_httpListener));
        }
        catch (Exception ex)
        {
            LastError = Localizer.Instance.Format("Web_PortUnavailable", _port, ex.Message);
            _httpListener = null;
        }

        Running = _httpListener != null;
        if (Running)
            OnStarted?.Invoke(PrimaryUrl());
    }

    public string PrimaryUrl() => BuildUrl(GetPrimaryLanAddress());

    /// <summary>Builds the controller URL for a specific interface address.</summary>
    public static string BuildUrl(string address) => $"http://{address}:{HttpPort}/";

    /// <summary>Lists usable IPv4 LAN interfaces (skipping loopback and
    /// link-local addresses) so the user can pick which one to advertise.</summary>
    public static IReadOnlyList<LanInterfaceInfo> EnumerateLanInterfaces()
    {
        var list = new List<LanInterfaceInfo>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var address = ua.Address.ToString();
                    if (address.StartsWith("169.254.")) continue;
                    list.Add(new LanInterfaceInfo { Name = ni.Name, Address = address });
                }
            }
        }
        catch { }
        return list;
    }

    private async Task AcceptLoopAsync(TcpListener listener)
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

            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        Stream? stream = null;
        var clientAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address
            ?? IPAddress.Loopback;
        try
        {
            client.NoDelay = true;
            stream = client.GetStream();

            // HTTP/1.1 keep-alive: serve requests on the same socket until the
            // peer closes or upgrades to a WebSocket.
            while (!_cts.IsCancellationRequested)
            {
                var header = await ReadRequestHeaderAsync(stream, _cts.Token);
                if (header == null) break;

                var req = HttpRequest.Parse(header);
                if (req == null || !string.Equals(req.Method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    await HttpResponse.WriteAsync(stream, 405, "Method Not Allowed", "text/plain",
                        Encoding.UTF8.GetBytes("Only GET is supported."));
                    break;
                }

                if (string.Equals(req.Path, "/ws", StringComparison.OrdinalIgnoreCase))
                {
                    // The bridge owns the socket from here on.
                    await _browser.HandleAsync(stream, req, clientAddress, _cts.Token);
                    return;
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
            await HttpResponse.WriteAsync(stream, 403, "Forbidden", "text/plain",
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
                await HttpResponse.WriteAsync(stream, 404, "Not Found", "text/plain",
                    Encoding.UTF8.GetBytes("Not found: " + path));
                return;
            }
        }

        byte[] bytes;
        try { bytes = await File.ReadAllBytesAsync(full, _cts.Token); }
        catch
        {
            await HttpResponse.WriteAsync(stream, 500, "Internal Server Error", "text/plain",
                Encoding.UTF8.GetBytes("Read error."));
            return;
        }

        var ext = Path.GetExtension(full);
        var mime = MimeTypes.TryGetValue(ext, out var m) ? m : "application/octet-stream";
        // Only cache immutable assets. Scripts/styles must revalidate so a web
        // update is picked up without a manual hard-refresh.
        var cacheable = ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp"
            or ".svg" or ".ico" or ".woff" or ".woff2" or ".ttf";
        await HttpResponse.WriteAsync(stream, 200, "OK", mime, bytes, cache: cacheable);
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

    // ────────────────────────── Paths ──────────────────────────

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
        try { _cts.Dispose(); } catch { }
    }
}
