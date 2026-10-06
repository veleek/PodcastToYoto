using System.Text.Json;
using PodcastToYoto.Yoto;

namespace PodcastToYoto.Web.Icons;

/// <summary>
/// Yoto's public icon library. Fetched lazily, kept in memory and in <c>{data}/icons.json</c>, and only re-fetched
/// after a week (or when refreshed from Settings). A stale copy is still used if a refresh fails.
/// </summary>
public sealed class IconCatalog(YotoApiClient yoto, TimeProvider time, ILogger<IconCatalog> logger, string cachePath)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromDays(7);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private IconIndex? _index;
    private bool _diskChecked;
    private DateTimeOffset _failedAt;

    /// <summary>When the cached library was fetched from Yoto (null = never).</summary>
    public DateTimeOffset? FetchedAt { get; private set; }

    public int Count => _index?.Count ?? 0;

    /// <summary>Why the library couldn't be loaded last time, if it couldn't.</summary>
    public string? LastError { get; private set; }

    /// <summary>The icon index, or null if it can't be loaded (e.g. missing scope). Throws only for auth problems.</summary>
    public async Task<IconIndex?> GetAsync(CancellationToken ct = default)
    {
        if (IsFresh()) return _index;

        await _lock.WaitAsync(ct);
        try
        {
            if (!_diskChecked)
            {
                _diskChecked = true;
                await LoadFromDiskAsync(ct);
                if (IsFresh()) return _index;
            }

            if (time.GetUtcNow() - _failedAt < RetryAfterFailure) return _index; // don't hammer the API
            return await FetchAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Re-download the library now (Settings → Refresh). Returns the new index, or null on failure.</summary>
    public async Task<IconIndex?> RefreshAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            _failedAt = default;
            return await FetchAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private bool IsFresh() => _index is not null && FetchedAt is { } at && time.GetUtcNow() - at < Ttl;

    private async Task<IconIndex?> FetchAsync(CancellationToken ct)
    {
        try
        {
            var icons = (await yoto.GetPublicIconsAsync(ct))
                .Select(i => new IconCandidate(i.MediaId, i.Title, i.Url, i.Tags))
                .ToList();
            _index = new IconIndex(icons);
            FetchedAt = time.GetUtcNow();
            LastError = null;
            await SaveToDiskAsync(icons, FetchedAt.Value, ct);
            logger.LogInformation("Fetched {Count} public Yoto icons", _index.Count);
            return _index;
        }
        // YotoAuthException is deliberately not caught: the caller (sync) reports it as "needs re-login", and caching it
        // as a failure would hide icons for a while after reconnecting.
        catch (Exception ex) when (ex is YotoApiException or HttpRequestException)
        {
            _failedAt = time.GetUtcNow();
            LastError = ex is YotoApiException { StatusCode: 403 }
                ? "Not allowed to read Yoto's icon library (needs the user:icons:manage scope). Reconnect Yoto on the dashboard."
                : $"Couldn't load Yoto icons: {ex.Message}";
            logger.LogWarning(ex, "Icon library unavailable; using {State}", _index is null ? "default icons" : "the cached copy");
            return _index; // stale copy if we have one
        }
    }

    private sealed record CacheFile(DateTimeOffset FetchedAt, List<IconCandidate> Icons);

    private async Task LoadFromDiskAsync(CancellationToken ct)
    {
        if (!File.Exists(cachePath)) return;
        try
        {
            await using var fs = File.OpenRead(cachePath);
            var cache = await JsonSerializer.DeserializeAsync<CacheFile>(fs, Json, ct);
            if (cache is null || cache.Icons.Count == 0) return;
            _index = new IconIndex(cache.Icons);
            FetchedAt = cache.FetchedAt;
            logger.LogInformation("Loaded {Count} Yoto icons from cache (fetched {FetchedAt:u})", _index.Count, FetchedAt);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            logger.LogWarning(ex, "Ignoring unreadable icon cache {Path}", cachePath);
        }
    }

    private async Task SaveToDiskAsync(List<IconCandidate> icons, DateTimeOffset fetchedAt, CancellationToken ct)
    {
        try
        {
            var tmp = cachePath + ".tmp";
            await using (var fs = File.Create(tmp))
                await JsonSerializer.SerializeAsync(fs, new CacheFile(fetchedAt, icons), Json, ct);
            File.Move(tmp, cachePath, overwrite: true);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Couldn't write icon cache {Path}", cachePath);
        }
    }
}
