using System.Net;
using System.Net.Sockets;
using System.Text;

namespace YotoSpike;

/// <summary>
/// Minimal one-shot HTTP listener on 127.0.0.1 for the OAuth redirect. Uses TcpListener rather than
/// HttpListener so it works on Windows without a URL ACL / admin rights.
/// </summary>
internal static class LoopbackCallback
{
    public static async Task<Dictionary<string, string>> WaitAsync(int port, string path, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        try
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(ct);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);

                var requestLine = await reader.ReadLineAsync(ct) ?? "";
                // Drain headers.
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(ct))) { }

                var parts = requestLine.Split(' ');
                var target = parts.Length > 1 ? parts[1] : "/";
                var q = target.IndexOf('?');
                var reqPath = q >= 0 ? target[..q] : target;

                if (!reqPath.Equals(path, StringComparison.OrdinalIgnoreCase))
                {
                    await WriteAsync(stream, "404 Not Found", "Not found", ct);
                    continue;
                }

                var query = ParseQuery(q >= 0 ? target[(q + 1)..] : "");
                var ok = query.ContainsKey("code");
                await WriteAsync(stream, "200 OK",
                    ok ? "Yoto login complete. You can close this tab and return to the terminal."
                       : $"Login failed: {WebUtility.HtmlEncode(query.GetValueOrDefault("error_description") ?? query.GetValueOrDefault("error") ?? "unknown")}",
                    ct);
                return query;
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task WriteAsync(Stream stream, string status, string message, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=utf-8><title>YotoSpike</title><p style=\"font:16px sans-serif\">{message}</p>");
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    private static Dictionary<string, string> ParseQuery(string qs)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in qs.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var k = Uri.UnescapeDataString((eq >= 0 ? pair[..eq] : pair).Replace('+', ' '));
            var v = eq >= 0 ? Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')) : "";
            result[k] = v;
        }
        return result;
    }
}
