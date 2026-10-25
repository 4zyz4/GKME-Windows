using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace GKME.Web;

/// <summary>
/// One parsed HTTP/1.1 request header block: method, path, query and headers.
/// Only what this host needs — there is no body support, because both the
/// static file route and the WebSocket upgrade are GET-only.
/// </summary>
public sealed class HttpRequest
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

/// <summary>Writes one HTTP/1.1 response with this host's cache policy.</summary>
public static class HttpResponse
{
    public static async Task WriteAsync(Stream stream, int status, string reason,
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
}
