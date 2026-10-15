using System.Collections.Concurrent;
using System.Text.Json;
using PodcastToYoto.Web.Options;
using PodcastToYoto.Yoto;
using SkiaSharp;

namespace PodcastToYoto.Web.Icons;

/// <summary>The results of a yotoicons.com search so far (pages are fetched one at a time, on request).</summary>
public sealed record YotoIconsResults(string Query, IReadOnlyList<IconCandidate> Icons, int? Total, int Pages, bool HasMore, DateTimeOffset FetchedAt);

/// <summary>
/// Community icons from yotoicons.com, which has no API, so search results are scraped from its HTML.
/// Everything fetched is kept in <c>{data}/yotoicons/</c>: search results per query (<c>index.json</c>) and the PNGs
/// (<c>images/{id}.png</c>, or <c>.gif</c> for GIFs). Cached searches and images are never fetched again unless a search is refreshed, and
/// every cached icon also shows up in the regular icon pickers (via <see cref="Extend"/>).
/// An icon only goes to Yoto when it's picked: it's uploaded as a custom icon once, and the media id is remembered.
/// </summary>
public sealed class YotoIconsLibrary(IHttpClientFactory httpFactory, YotoApiClient yoto, TimeProvider time, DataPaths paths,
    ILogger<YotoIconsLibrary> logger)
{
    public const string HttpClientName = "yotoicons";
    public const string SiteUrl = "https://www.yotoicons.com/";

    /// <summary>Media id prefix for cached icons that haven't been uploaded to Yoto yet.</summary>
    public const string Prefix = "yotoicons:";

    private const int MaxImageBytes = 256 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private readonly string _dir = Path.Combine(paths.Root, "yotoicons");
    private string IndexPath => Path.Combine(_dir, "index.json");
    /// <summary>Cached image: <c>{id}.gif</c> for GIFs (kept as they are, so animations survive), <c>{id}.png</c> otherwise.</summary>
    private string ImagePath(int id, bool gif = false) => Path.Combine(_dir, "images", $"{id}.{(gif ? "gif" : "png")}");

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConcurrentDictionary<int, Lazy<Task<string>>> _downloads = new();
    private State? _state;

    // ---- Persistent state ------------------------------------------------------------------

    private sealed class CachedIcon
    {
        public int Id { get; set; }
        public string? Title { get; set; }
        public string? Category { get; set; }
        public List<string> Tags { get; set; } = [];
        public string? Artist { get; set; }
        public int? Downloads { get; set; }

        /// <summary>Searches that returned this icon, so it can be found locally by them even if it has no tags.</summary>
        public List<string> FoundBy { get; set; } = [];

        public string? YotoMediaId { get; set; }
        public string? YotoUrl { get; set; }
    }

    private sealed class CachedSearch
    {
        public string Query { get; set; } = "";
        public List<int> Ids { get; set; } = [];
        public int Pages { get; set; }
        public int? Total { get; set; }
        public bool Exhausted { get; set; }
        public DateTimeOffset FetchedAt { get; set; }
    }

    private sealed class State
    {
        public Dictionary<int, CachedIcon> Icons { get; set; } = [];
        public Dictionary<string, CachedSearch> Searches { get; set; } = new(StringComparer.Ordinal);
    }

    /// <summary>Whether the pickers' icon library includes cached yotoicons.com icons (remembered until restart).</summary>
    public bool ShowInLibrary { get; set; } = true;

    /// <summary>Number of icons cached from yotoicons.com (for Settings).</summary>
    public int Count => _state?.Icons.Count ?? 0;

    /// <summary>What's in the cache, for Settings.</summary>
    public sealed record Stats(int Icons, int Images, long ImageBytes, int Searches, int OnYoto, DateTimeOffset? LastSearch, string Directory);

    public async Task<Stats> GetStatsAsync(CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        var images = new DirectoryInfo(Path.Combine(_dir, "images"));
        var files = images.Exists ? images.GetFiles("*.*").Where(f => f.Extension is ".png" or ".gif").ToList() : [];
        lock (_state!)
        {
            return new Stats(_state.Icons.Count, files.Count, files.Sum(f => f.Length), _state.Searches.Count,
                _state.Icons.Values.Count(i => i.YotoMediaId is not null),
                _state.Searches.Count == 0 ? null : _state.Searches.Values.Max(s => s.FetchedAt), _dir);
        }
    }

    // ---- Browsing --------------------------------------------------------------------------

    /// <summary><paramref name="index"/> plus every cached yotoicons.com icon (browsable, never auto-matched).</summary>
    public async Task<IconIndex?> ExtendAsync(IconIndex? index, CancellationToken ct = default)
    {
        if (index is null) return null;
        await EnsureLoadedAsync(ct);
        return Extend(index);
    }

    /// <summary>Synchronous <see cref="ExtendAsync"/> for after the cache is loaded (it always is once a search ran).</summary>
    public IconIndex Extend(IconIndex index)
    {
        var state = _state;
        if (state is null || state.Icons.Count == 0) return index;
        lock (state) return index.WithExtras(state.Icons.Values.Select(ToCandidate).ToList());
    }

    /// <summary>A search's cached results, or null if it hasn't been run (no network).</summary>
    public async Task<YotoIconsResults?> GetCachedAsync(string query, CancellationToken ct = default)
    {
        var key = Normalise(query);
        if (key.Length == 0) return null;
        await EnsureLoadedAsync(ct);
        lock (_state!) return _state.Searches.TryGetValue(key, out var s) ? ToResults(s) : null;
    }

    /// <summary>Cached results for <paramref name="query"/>, or its first page from the site if it was never searched.</summary>
    public async Task<YotoIconsResults> SearchAsync(string query, CancellationToken ct = default) =>
        await GetCachedAsync(query, ct) ?? await FetchPageAsync(Normalise(query), restart: true, ct);

    /// <summary>Fetch the next page of a search from the site.</summary>
    public Task<YotoIconsResults> LoadMoreAsync(string query, CancellationToken ct = default) =>
        FetchPageAsync(Normalise(query), restart: false, ct);

    /// <summary>Forget a search's cached pages and fetch its first page again. Cached images are kept.</summary>
    public Task<YotoIconsResults> RefreshAsync(string query, CancellationToken ct = default) =>
        FetchPageAsync(Normalise(query), restart: true, ct);

    private async Task<YotoIconsResults> FetchPageAsync(string key, bool restart, CancellationToken ct)
    {
        if (key.Length == 0) throw new ArgumentException("Enter something to search for.", nameof(key));
        await EnsureLoadedAsync(ct);

        await _lock.WaitAsync(ct);
        try
        {
            CachedSearch search;
            lock (_state!)
            {
                if (restart || !_state.Searches.TryGetValue(key, out search!))
                    search = new CachedSearch { Query = key };
            }
            if (search.Exhausted) return ToResults(search);

            var page = search.Pages + 1;
            var http = httpFactory.CreateClient(HttpClientName);
            var html = await http.GetStringAsync($"icons?tag={Uri.EscapeDataString(key)}&page={page}", ct);
            var parsed = YotoIconsParser.Parse(html);
            logger.LogInformation("yotoicons.com “{Query}” page {Page}: {Count} icons (total {Total})", key, page, parsed.Icons.Count, parsed.Total);

            lock (_state)
            {
                var added = 0;
                foreach (var entry in parsed.Icons)
                {
                    var icon = _state.Icons.TryGetValue(entry.Id, out var existing) ? existing : _state.Icons[entry.Id] = new CachedIcon { Id = entry.Id };
                    icon.Title = entry.Title ?? icon.Title;
                    icon.Category = entry.Category ?? icon.Category;
                    if (entry.Tags.Count > 0) icon.Tags = entry.Tags.ToList();
                    icon.Artist = entry.Artist ?? icon.Artist;
                    icon.Downloads = entry.Downloads ?? icon.Downloads;
                    if (!icon.FoundBy.Contains(key)) icon.FoundBy.Add(key);
                    if (!search.Ids.Contains(entry.Id)) { search.Ids.Add(entry.Id); added++; }
                }

                search.Pages = page;
                search.Total = parsed.Total ?? search.Total;
                search.FetchedAt = time.GetUtcNow();
                // A short page, or one with nothing new (the site repeats the last page past the end), means we're done.
                search.Exhausted = parsed.Icons.Count < YotoIconsParser.PageSize || added == 0
                    || (search.Total is { } total && search.Ids.Count >= total);
                _state.Searches[key] = search;
            }

            await SaveAsync(ct);
            return ToResults(search);
        }
        finally
        {
            _lock.Release();
        }
    }

    // ---- Images ----------------------------------------------------------------------------

    /// <summary>Path of the icon's PNG, downloading it the first time. Null if the id isn't a cached icon.</summary>
    public async Task<string?> GetImagePathAsync(int id, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        lock (_state!) if (!_state.Icons.ContainsKey(id)) return null;

        if (ImagePath(id) is var png && File.Exists(png)) return png;
        if (ImagePath(id, gif: true) is var gif && File.Exists(gif)) return gif;

        // One download per icon even when a grid asks for it from several requests at once.
        var lazy = _downloads.GetOrAdd(id, _ => new Lazy<Task<string>>(() => DownloadAsync(id)));
        try
        {
            return await lazy.Value.WaitAsync(ct);
        }
        finally
        {
            if (lazy.Value.IsCompleted) _downloads.TryRemove(id, out _);
        }
    }

    private async Task<string> DownloadAsync(int id)
    {
        var http = httpFactory.CreateClient(HttpClientName);
        using var resp = await http.GetAsync($"static/uploads/{id}.png");
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        if (bytes.Length > MaxImageBytes)
            throw new InvalidOperationException($"yotoicons.com icon {id} is too large ({bytes.Length:N0} bytes).");

        // Uploads keep whatever format the artist used (some ".png"s are really GIFs or JPEGs) and browsers don't care.
        // Yoto takes PNGs and GIFs (animated ones too), so keep those and convert anything else to PNG.
        var gif = IsGif(bytes);
        var path = ImagePath(id, gif);
        var png = IsPng(bytes) || gif ? bytes : ToPng(bytes) ?? throw new InvalidOperationException(
            $"yotoicons.com icon {id} isn't an image ({resp.Content.Headers.ContentType?.MediaType ?? "no content type"}, " +
            $"{bytes.Length:N0} bytes, starts \"{Preview(bytes)}\").");
        if (png != bytes) logger.LogInformation("yotoicons.com icon {Id} was {Type}; converted to PNG", id, resp.Content.Headers.ContentType?.MediaType);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, png);
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    private static bool IsPng(byte[] b) => b.Length > 8 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G';
    private static bool IsGif(byte[] b) => b.Length > 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8';

    /// <summary>Content type of a cached image, from its extension.</summary>
    public static string ContentType(string path) => path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ? "image/gif" : "image/png";

    /// <summary>The image (first frame, for GIFs) re-encoded as PNG, or null if Skia can't decode it.</summary>
    public static byte[]? ToPng(byte[] bytes)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(bytes)); // null for anything that isn't an image
        if (codec is null) return null;
        using var bitmap = SKBitmap.Decode(codec);
        if (bitmap is null) return null;
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray();
    }

    private static string Preview(byte[] b) =>
        new(b.Take(16).Select(x => x is >= 32 and < 127 ? (char)x : '.').ToArray());

    // ---- Uploading -------------------------------------------------------------------------

    /// <summary>
    /// The Yoto icon for a picked candidate: candidates that aren't from yotoicons.com come back as they are; a cached
    /// yotoicons.com icon is uploaded to the Yoto account the first time (as a custom icon) and remembered.
    /// </summary>
    public async Task<IconCandidate> EnsureOnYotoAsync(IconCandidate icon, CancellationToken ct = default)
    {
        if (!icon.MediaId.StartsWith(Prefix, StringComparison.Ordinal)
            || !int.TryParse(icon.MediaId.AsSpan(Prefix.Length), out var id)) return icon;

        await EnsureLoadedAsync(ct);
        CachedIcon cached;
        lock (_state!)
        {
            if (!_state.Icons.TryGetValue(id, out cached!)) throw new InvalidOperationException($"yotoicons.com icon {id} isn't cached.");
            if (cached.YotoMediaId is not null) return ToCandidate(cached);
        }

        var path = await GetImagePathAsync(id, ct) ?? throw new InvalidOperationException("Icon image missing.");
        var image = await File.ReadAllBytesAsync(path, ct);
        var type = ContentType(path);
        // Yoto keeps a GIF's animation only without autoConvert, which needs it to be 16x16 already; anything bigger is
        // resized by Yoto (and an animated GIF flattened to its first frame).
        var exact = Is16x16(image);
        if (!exact && type == "image/gif")
            logger.LogWarning("yotoicons.com icon {Id} is a GIF but not 16x16; Yoto will resize it and drop any animation", id);
        var uploaded = await yoto.UploadCustomIconAsync(image, $"yotoicons-{id}{Path.GetExtension(path)}", autoConvert: !exact, ct, type);
        logger.LogInformation("Uploaded yotoicons.com icon {Id} to Yoto as {MediaId}", id, uploaded.MediaId);

        lock (_state)
        {
            cached.YotoMediaId = uploaded.MediaId;
            cached.YotoUrl = uploaded.Url;
        }
        await SaveAsync(ct);
        return ToCandidate(cached);
    }

    private static bool Is16x16(byte[] png)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(png));
        return codec is { Info: { Width: LogoIconGenerator.Size, Height: LogoIconGenerator.Size } };
    }

    // ---- Helpers ---------------------------------------------------------------------------

    /// <summary>Lower-case, single-spaced, without a leading "@" (the site's search matches tags and artists alike).</summary>
    public static string Normalise(string? query) =>
        string.Join(' ', (query ?? "").Trim().TrimStart('@').ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static IconCandidate ToCandidate(CachedIcon i)
    {
        var tags = i.Tags
            .Concat(i.Category is null ? [] : [i.Category])
            .Concat(i.Artist is null ? [] : [i.Artist])
            .Concat(i.FoundBy)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new IconCandidate(
            i.YotoMediaId ?? $"{Prefix}{i.Id}",
            string.IsNullOrWhiteSpace(i.Title) ? $"yotoicons #{i.Id}" : i.Title,
            i.YotoUrl ?? $"/yotoicons/{i.Id}.png", // the local cache until it's on Yoto
            tags,
            $"{(i.Artist is null ? "" : $"@{i.Artist} on ")}yotoicons.com{(i.YotoMediaId is null ? "" : " (on your Yoto account)")}");
    }

    private YotoIconsResults ToResults(CachedSearch s)
    {
        lock (_state!)
        {
            var icons = s.Ids.Select(id => _state.Icons.TryGetValue(id, out var i) ? ToCandidate(i) : null).OfType<IconCandidate>().ToList();
            return new YotoIconsResults(s.Query, icons, s.Total, s.Pages, !s.Exhausted, s.FetchedAt);
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_state is not null) return;
        await _lock.WaitAsync(ct);
        try
        {
            if (_state is not null) return;
            State? state = null;
            if (File.Exists(IndexPath))
            {
                try
                {
                    await using var fs = File.OpenRead(IndexPath);
                    state = await JsonSerializer.DeserializeAsync<State>(fs, Json, ct);
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    logger.LogWarning(ex, "Ignoring unreadable yotoicons cache {Path}", IndexPath);
                }
            }
            state ??= new State();
            state.Searches = new Dictionary<string, CachedSearch>(state.Searches, StringComparer.Ordinal);
            _state = state;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            byte[] bytes;
            lock (_state!) bytes = JsonSerializer.SerializeToUtf8Bytes(_state, Json);
            var tmp = IndexPath + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            File.Move(tmp, IndexPath, overwrite: true);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Couldn't write yotoicons cache {Path}", IndexPath);
        }
    }
}
