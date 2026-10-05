using System.Net;

namespace PodcastToYoto.Feeds;

public sealed record FeedFetchResult(bool NotModified, PodcastFeed? Feed, string? ETag, string? LastModified);

/// <summary>Fetches and parses a feed, using conditional GET (ETag / Last-Modified) when validators are supplied.</summary>
public sealed class FeedFetcher(HttpClient http)
{
    public async Task<FeedFetchResult> FetchAsync(string url, string? etag = null, string? lastModified = null, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("PodcastToYoto/0.1");
        if (!string.IsNullOrEmpty(etag)) req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        if (!string.IsNullOrEmpty(lastModified)) req.Headers.TryAddWithoutValidation("If-Modified-Since", lastModified);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == HttpStatusCode.NotModified)
            return new FeedFetchResult(true, null, etag, lastModified);

        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var feed = await PodcastFeedParser.ParseAsync(stream, ct);

        return new FeedFetchResult(
            false,
            feed,
            resp.Headers.ETag?.ToString(),
            resp.Content.Headers.LastModified?.ToString("R"));
    }
}
