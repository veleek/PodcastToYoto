using System.Net;
using System.Security.Cryptography;

namespace PodcastToYoto.Feeds;

public sealed record DownloadedFile(string Path, string Sha256Hex, long Length, string ContentType) : IDisposable
{
    public void Dispose()
    {
        try { File.Delete(Path); } catch (IOException) { }
    }
}

/// <summary>
/// Streams an enclosure to a temp file while hashing it. Follows redirects manually because podcast
/// tracking prefixes often bounce https→http, which HttpClient refuses to auto-follow.
/// The HttpClient passed in should be built on a handler with AllowAutoRedirect = false.
/// </summary>
public sealed class EpisodeDownloader(HttpClient http, string tempDirectory)
{
    private const int MaxRedirects = 10;

    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
    };

    public async Task<DownloadedFile> DownloadAsync(string url, CancellationToken ct = default)
    {
        Directory.CreateDirectory(tempDirectory);
        var current = new Uri(url);

        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, current);
            req.Headers.UserAgent.ParseAdd("PodcastToYoto/0.1");
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            if (IsRedirect(resp.StatusCode))
            {
                var location = resp.Headers.Location ?? throw new HttpRequestException($"Redirect without Location from {current}");
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }

            resp.EnsureSuccessStatusCode();

            var path = System.IO.Path.Combine(tempDirectory, $"{Guid.NewGuid():N}.audio");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            var buffer = new byte[81920];

            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(path))
            {
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    total += read;
                }
            }

            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "audio/mpeg";
            return new DownloadedFile(path, Convert.ToHexStringLower(hash.GetHashAndReset()), total, contentType);
        }

        throw new HttpRequestException($"Too many redirects downloading {url}");
    }

    private static bool IsRedirect(HttpStatusCode code) =>
        code is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}
