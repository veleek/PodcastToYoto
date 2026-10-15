using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace PodcastToYoto.Web.Subscriptions;

/// <summary>A podcast found by a directory search; <see cref="FeedUrl"/> is what gets previewed and subscribed to.</summary>
public sealed record PodcastSearchResult(string Title, string? Author, string FeedUrl, string? ArtworkUrl, int? EpisodeCount);

public sealed class PodcastIndexOptions
{
    public const string Section = "PodcastIndex";

    /// <summary>API key and secret from https://api.podcastindex.org/signup. When both are set, search uses Podcast Index instead of Apple.</summary>
    public string? Key { get; set; }
    public string? Secret { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Key) && !string.IsNullOrWhiteSpace(Secret);
}

/// <summary>
/// Finds podcasts by name. Uses Podcast Index when <see cref="PodcastIndexOptions"/> has a key, otherwise the Apple Podcasts
/// (iTunes Search) directory, which needs no key but allows only ~20 calls a minute. Results are cached for an hour.
/// </summary>
public sealed class PodcastSearch(IHttpClientFactory httpFactory, IOptions<PodcastIndexOptions> podcastIndex, IMemoryCache cache,
    TimeProvider time)
{
    public const string HttpClientName = "podcastsearch";
    public const int MaxResults = 20;

    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(1);

    /// <summary>Name of the directory being searched, for the UI.</summary>
    public string Source => podcastIndex.Value.IsConfigured ? "Podcast Index" : "Apple Podcasts";

    public async Task<IReadOnlyList<PodcastSearchResult>> SearchAsync(string term, CancellationToken ct = default)
    {
        term = term.Trim();
        if (term.Length == 0) return [];

        var key = $"podcastsearch:{Source}:{term.ToLowerInvariant()}";
        if (cache.TryGetValue(key, out IReadOnlyList<PodcastSearchResult>? cached) && cached is not null) return cached;

        var results = podcastIndex.Value.IsConfigured ? await SearchPodcastIndexAsync(term, ct) : await SearchITunesAsync(term, ct);
        cache.Set(key, results, CacheFor);
        return results;
    }

    private async Task<IReadOnlyList<PodcastSearchResult>> SearchITunesAsync(string term, CancellationToken ct)
    {
        var url = $"https://itunes.apple.com/search?media=podcast&entity=podcast&limit={MaxResults}&term={Uri.EscapeDataString(term)}";
        using var response = await httpFactory.CreateClient(HttpClientName).GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return ParseITunes(await response.Content.ReadAsStringAsync(ct));
    }

    private async Task<IReadOnlyList<PodcastSearchResult>> SearchPodcastIndexAsync(string term, CancellationToken ct)
    {
        var o = podcastIndex.Value;
        var url = $"https://api.podcastindex.org/api/1.0/search/byterm?max={MaxResults}&q={Uri.EscapeDataString(term)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var now = time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        request.Headers.Add("X-Auth-Key", o.Key);
        request.Headers.Add("X-Auth-Date", now);
        request.Headers.TryAddWithoutValidation("Authorization", PodcastIndexAuthorization(o.Key!, o.Secret!, now));
        using var response = await httpFactory.CreateClient(HttpClientName).SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return ParsePodcastIndex(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Podcast Index's auth header: lowercase hex SHA-1 of key + secret + the X-Auth-Date value.</summary>
    public static string PodcastIndexAuthorization(string key, string secret, string unixTime) =>
        Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(key + secret + unixTime)));

    /// <summary>Parses an iTunes Search response. Results without a feed URL (Apple-only shows) are dropped.</summary>
    public static IReadOnlyList<PodcastSearchResult> ParseITunes(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var items) || items.ValueKind != JsonValueKind.Array) return [];
        return Collect(items.EnumerateArray().Select(r => (
            Str(r, "collectionName") ?? Str(r, "trackName"),
            Str(r, "artistName"),
            Str(r, "feedUrl"),
            Str(r, "artworkUrl600") ?? Str(r, "artworkUrl100"),
            Int(r, "trackCount"))));
    }

    /// <summary>Parses a Podcast Index search response. Feeds flagged dead are dropped.</summary>
    public static IReadOnlyList<PodcastSearchResult> ParsePodcastIndex(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("feeds", out var items) || items.ValueKind != JsonValueKind.Array) return [];
        return Collect(items.EnumerateArray()
            .Where(f => Int(f, "dead") is null or 0)
            .Select(f => (
                Str(f, "title"),
                Str(f, "author") ?? Str(f, "ownerName"),
                Str(f, "url"),
                Str(f, "artwork") ?? Str(f, "image"),
                Int(f, "episodeCount"))));
    }

    private static List<PodcastSearchResult> Collect(
        IEnumerable<(string? Title, string? Author, string? FeedUrl, string? Artwork, int? Episodes)> items)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<PodcastSearchResult>();
        foreach (var (title, author, feedUrl, artwork, episodes) in items)
        {
            if (feedUrl is null || !Uri.TryCreate(feedUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) continue;
            if (!seen.Add(feedUrl)) continue;
            results.Add(new PodcastSearchResult(title ?? feedUrl, author, feedUrl, artwork, episodes));
        }
        return results;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
