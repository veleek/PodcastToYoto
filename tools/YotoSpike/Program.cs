using System.Diagnostics;
using System.Text.Json;
using PodcastToYoto.Feeds;
using PodcastToYoto.Yoto;
using YotoSpike;

// YotoSpike — throwaway CLI for validating Yoto API assumptions (docs/spikes.md).
//
//   login   [--port 8787]                      Browser login (PKCE, loopback redirect), stores tokens
//   refresh                                    Force a token refresh; verifies refresh-token rotation
//   list                                       List MYO playlists (GET /content/mine)
//   get     <cardId>                           Dump full playlist JSON (GET /content/{cardId})
//   feed    [--feed URL] [--count N]           Parse the RSS feed and show newest episodes
//   push    [--card ID] [--count N] [--feed URL] [--title T] [--no-cover] [--dry-run]
//                                              Download/upload newest N episodes and create or update a playlist
//   streams --base URL [--card ID] [--run R] [--title T]
//                                              Create/update a playlist of stream tracks pointing at tools/StreamProbe
//
// Global: --client-id ID (or env YOTO_CLIENT_ID), --data DIR (default ./.yoto-spike)

const string DefaultFeed = "https://feeds.megaphone.fm/EUAET6896760602"; // Silly Stories for Kids

var cli = Cli.Parse(args);
if (cli.Command is null or "help" or "--help" or "-h")
{
    PrintHelp();
    return 0;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;

var dataDir = Path.GetFullPath(cli.Get("data") ?? ".yoto-spike");
Directory.CreateDirectory(dataDir);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    WriteIndented = true,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
};

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

try
{
    switch (cli.Command)
    {
        case "feed":
            return await FeedCommand();
        case "login":
            return await LoginCommand();
        case "refresh":
            return await RefreshCommand();
        case "list":
            return await ListCommand();
        case "get":
            return await GetCommand();
        case "push":
            return await PushCommand();
        case "streams":
            return await StreamsCommand();
        default:
            Console.Error.WriteLine($"Unknown command '{cli.Command}'.");
            PrintHelp();
            return 2;
    }
}
catch (YotoApiException ex)
{
    Console.Error.WriteLine($"Yoto API error {ex.StatusCode}: {ex.Message}");
    if (!string.IsNullOrEmpty(ex.Body)) Console.Error.WriteLine(ex.Body);
    return 1;
}
catch (YotoAuthException ex)
{
    Console.Error.WriteLine($"Auth error: {ex.Message}");
    return 1;
}

// ---------------------------------------------------------------------------------------------

YotoOAuthOptions OAuth()
{
    var clientId = cli.Get("client-id") ?? Environment.GetEnvironmentVariable("YOTO_CLIENT_ID");
    if (string.IsNullOrWhiteSpace(clientId))
        throw new YotoAuthException("Client ID required: pass --client-id or set YOTO_CLIENT_ID.");
    return new YotoOAuthOptions { ClientId = clientId, Scopes = cli.Get("scopes") ?? YotoOAuthOptions.DefaultScopes };
}

(YotoAuthClient auth, YotoTokenProvider tokens, YotoApiClient api) CreateYoto()
{
    var options = OAuth();
    var auth = new YotoAuthClient(http, options);
    var tokens = new YotoTokenProvider(auth, new FileTokenStore(Path.Combine(dataDir, "tokens.json")), options);
    return (auth, tokens, new YotoApiClient(http, tokens));
}

async Task<PodcastFeed> LoadFeed()
{
    var url = cli.Get("feed") ?? DefaultFeed;
    using var resp = await http.GetAsync(url, ct);
    resp.EnsureSuccessStatusCode();
    await using var s = await resp.Content.ReadAsStreamAsync(ct);
    return PodcastFeedParser.Parse(s);
}

async Task<int> FeedCommand()
{
    var feed = await LoadFeed();
    var count = cli.GetInt("count") ?? 10;
    Console.WriteLine($"{feed.Title} — {feed.Author}  ({feed.Episodes.Count} episodes)");
    Console.WriteLine($"Image: {feed.ImageUrl}");
    foreach (var e in feed.Episodes.Take(count))
        Console.WriteLine($"  {e.Published:yyyy-MM-dd}  {e.Duration,-9:hh\\:mm\\:ss}  {e.EnclosureLength / 1_000_000.0,6:F1} MB  {e.EpisodeType,-7}  {e.Title}");

    var all = feed.Episodes.Take(count).ToList();
    var secs = all.Sum(e => e.Duration?.TotalSeconds ?? 0);
    var bytes = all.Sum(e => e.EnclosureLength ?? 0);
    Console.WriteLine($"Newest {all.Count}: {TimeSpan.FromSeconds(secs):h\\:mm\\:ss} total, {bytes / 1_000_000.0:F0} MB source audio");
    return 0;
}

async Task<int> LoginCommand()
{
    var (auth, tokens, _) = CreateYoto();
    var port = cli.GetInt("port") ?? 8787;
    var redirect = $"http://127.0.0.1:{port}/callback";
    var pkce = PkcePair.Create();
    var state = PkcePair.CreateState();
    var url = auth.BuildAuthorizeUrl(redirect, state, pkce);

    Console.WriteLine($"Redirect URI (must be registered in the Yoto dev portal): {redirect}");
    Console.WriteLine("Opening browser. If it doesn't open, visit:");
    Console.WriteLine(url.AbsoluteUri);
    try { Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }); } catch { /* headless */ }

    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeout.CancelAfter(TimeSpan.FromMinutes(5));
    var query = await LoopbackCallback.WaitAsync(port, "/callback", timeout.Token);

    if (!query.TryGetValue("code", out var code))
        throw new YotoAuthException($"Login failed: {query.GetValueOrDefault("error")} {query.GetValueOrDefault("error_description")}");
    if (query.GetValueOrDefault("state") != state)
        throw new YotoAuthException("State mismatch on callback.");

    var t = await auth.ExchangeCodeAsync(code, pkce.Verifier, redirect, ct);
    await tokens.SetTokensAsync(t, ct);
    Console.WriteLine($"Logged in. Access token expires {t.ExpiresAt:u}. Scopes: {t.Scope}");
    Console.WriteLine($"Tokens saved to {Path.Combine(dataDir, "tokens.json")}");
    return 0;
}

async Task<int> RefreshCommand()
{
    var (_, tokens, _) = CreateYoto();
    var before = await new FileTokenStore(Path.Combine(dataDir, "tokens.json")).LoadAsync(ct);
    var after = await tokens.ForceRefreshAsync(ct);
    Console.WriteLine($"Refreshed. New expiry {after.ExpiresAt:u} (lifetime ≈ {(after.ExpiresAt - DateTimeOffset.UtcNow).TotalHours:F1} h).");
    Console.WriteLine($"Refresh token rotated: {before?.RefreshToken != after.RefreshToken}");
    Console.WriteLine($"Scopes: {after.Scope}");
    return 0;
}

async Task<int> ListCommand()
{
    var (_, _, api) = CreateYoto();
    var cards = await api.GetMyContentAsync(ct);
    Console.WriteLine($"{cards.Count} MYO playlists:");
    foreach (var c in cards.OrderByDescending(c => c.UpdatedAt))
        Console.WriteLine($"  {c.CardId,-12} {c.UpdatedAt:yyyy-MM-dd HH:mm}  {c.Title}");
    await File.WriteAllTextAsync(Path.Combine(dataDir, "content-mine.json"),
        JsonSerializer.Serialize(cards.Select(c => c.Raw), json), ct);
    return 0;
}

async Task<int> GetCommand()
{
    var cardId = cli.Positional.ElementAtOrDefault(0) ?? throw new ArgumentException("usage: get <cardId>");
    var (_, _, api) = CreateYoto();
    using var doc = await api.GetContentAsync(cardId, ct);
    var path = Path.Combine(dataDir, $"content-{cardId}.json");
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(doc.RootElement, json), ct);

    var card = doc.RootElement.TryGetProperty("card", out var c) ? c : doc.RootElement;
    Console.WriteLine($"{card.GetProperty("cardId")}  {card.GetProperty("title")}");
    if (card.TryGetProperty("content", out var content) && content.TryGetProperty("chapters", out var chapters))
    {
        foreach (var ch in chapters.EnumerateArray())
            Console.WriteLine($"  [{ch.GetProperty("key")}] {ch.GetProperty("title")}");
    }
    Console.WriteLine($"Full JSON: {path}");
    return 0;
}

async Task<int> PushCommand()
{
    var feed = await LoadFeed();
    var count = cli.GetInt("count") ?? 3;
    var cardId = cli.Get("card");
    var dryRun = cli.Has("dry-run");
    var episodes = feed.Episodes.Take(count).ToList();

    Console.WriteLine($"{(cardId is null ? "Creating new" : $"Updating {cardId}")} playlist with {episodes.Count} newest episodes of '{feed.Title}'.");

    var (_, _, api) = CreateYoto();
    var uploader = new YotoAudioUploader(api);
    var cachePath = Path.Combine(dataDir, "uploads.json");
    var cache = File.Exists(cachePath)
        ? JsonSerializer.Deserialize<Dictionary<string, YotoTranscodedAudio>>(await File.ReadAllTextAsync(cachePath, ct), json) ?? new()
        : new Dictionary<string, YotoTranscodedAudio>();

    using var dlHttp = new HttpClient(EpisodeDownloader.CreateHandler()) { Timeout = TimeSpan.FromMinutes(10) };
    var downloader = new EpisodeDownloader(dlHttp, Path.Combine(dataDir, "tmp"));
    var entries = new List<PlaylistEntry>();

    foreach (var ep in episodes)
    {
        if (!cache.TryGetValue(ep.Guid, out var audio))
        {
            if (dryRun)
            {
                Console.WriteLine($"  [dry-run] would upload: {ep.Title}");
                continue;
            }

            var sw = Stopwatch.StartNew();
            Console.Write($"  ↓ {ep.Title} ... ");
            using var file = await downloader.DownloadAsync(ep.EnclosureUrl, ct);
            Console.Write($"{file.Length / 1_000_000.0:F1} MB in {sw.Elapsed.TotalSeconds:F0}s; ↑ upload+transcode ... ");
            sw.Restart();
            audio = await uploader.UploadAsync(file, $"{Sanitize(ep.Title)}.mp3", ct);
            Console.WriteLine($"done in {sw.Elapsed.TotalSeconds:F0}s → {audio.TrackUrl} ({audio.Duration}s, {audio.FileSize} B)");

            cache[ep.Guid] = audio;
            await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(cache, json), ct);
        }
        else
        {
            Console.WriteLine($"  = cached: {ep.Title} → {audio.TrackUrl}");
        }
        entries.Add(new PlaylistEntry(ep.Title, audio));
    }

    string? coverUrl = null;
    if (!cli.Has("no-cover") && feed.ImageUrl is not null && !dryRun)
    {
        try
        {
            coverUrl = await api.UploadCoverImageFromUrlAsync(feed.ImageUrl, ct: ct);
            Console.WriteLine($"Cover uploaded: {coverUrl}");
        }
        catch (YotoApiException ex)
        {
            Console.WriteLine($"Cover upload failed ({ex.StatusCode}); continuing without. {ex.Body}");
        }
    }

    var request = PlaylistBuilder.Build(
        cli.Get("title") ?? feed.Title, entries, cardId, coverUrl, feed.Description, feed.Author);

    var reqPath = Path.Combine(dataDir, "last-request.json");
    await File.WriteAllTextAsync(reqPath, JsonSerializer.Serialize(request, json), ct);
    var totalMb = (request.Metadata?.Media?.FileSize ?? 0) / 1_000_000.0;
    Console.WriteLine($"Playlist: {entries.Count} chapters, {TimeSpan.FromSeconds(request.Metadata?.Media?.Duration ?? 0):h\\:mm\\:ss}, {totalMb:F1} MB transcoded. Request body: {reqPath}");

    if (dryRun) return 0;

    var card = await api.CreateOrUpdateContentAsync(request, ct);
    var resultId = card.GetProperty("cardId").GetString();
    await File.WriteAllTextAsync(Path.Combine(dataDir, "last-response.json"), JsonSerializer.Serialize(card, json), ct);
    Console.WriteLine($"POST /content OK → cardId {resultId}" + (cardId is not null ? $" (same as requested: {resultId == cardId})" : ""));
    return 0;
}

// Stream-track spike (docs/stream-triggers.md): each chapter exercises one player behaviour against StreamProbe.
async Task<int> StreamsCommand()
{
    var baseUrl = cli.Get("base")?.TrimEnd('/') ?? throw new YotoAuthException("--base https://<probe host> is required.");
    var run = cli.Get("run") ?? DateTime.Now.ToString("MMddHHmm");
    (string Title, string Path)[] tracks =
    [
        ("Plain A", $"/p/{run}/01.mp3?card={run}&ch=01"),  // query string kept?
        ("Plain B", $"/p/{run}/02.mp3"),                    // fetched before it's played (prefetch)?
        ("Redirect", $"/p/{run}/03.mp3?redirect=1"),        // 302 followed?
        ("Slow 5 s", $"/p/{run}/04.mp3?delay=5000"),        // time to first byte tolerated?
        ("Slow 20 s", $"/p/{run}/05.mp3?delay=20000"),
        ("Missing", $"/p/{run}/06.mp3?status=404"),         // what the player does on an error
        ("Trigger", $"/t/{run}-lights/07.mp3"),             // fires the HA webhook
    ];

    var chapters = tracks.Select((t, i) =>
    {
        var key = (i + 1).ToString("00");
        return new YotoChapter
        {
            Key = key,
            Title = t.Title,
            OverlayLabel = (i + 1).ToString(),
            Tracks = [new YotoTrack { Key = "01", Title = t.Title, TrackUrl = baseUrl + t.Path, Type = "stream", Format = "mp3", OverlayLabel = (i + 1).ToString() }],
        };
    }).ToList();
    var request = new YotoContentRequest
    {
        CardId = cli.Get("card"),
        Title = cli.Get("title") ?? $"Stream probe {run}",
        Content = new YotoContentBody { Chapters = chapters },
        Metadata = new YotoMetadata { Description = "Stream-track spike: every chapter is a stream from StreamProbe." },
    };

    var (_, _, api) = CreateYoto();
    await File.WriteAllTextAsync(Path.Combine(dataDir, "last-request.json"), JsonSerializer.Serialize(request, json), ct);
    var card = await api.CreateOrUpdateContentAsync(request, ct);
    await File.WriteAllTextAsync(Path.Combine(dataDir, "last-response.json"), JsonSerializer.Serialize(card, json), ct);
    Console.WriteLine($"POST /content OK → cardId {card.GetProperty("cardId").GetString()}, run '{run}'");
    foreach (var t in tracks) Console.WriteLine($"  {t.Title,-10} {baseUrl}{t.Path}");
    return 0;
}

static string Sanitize(string s)
{
    var chars = s.Where(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_').ToArray();
    var clean = new string(chars).Trim();
    return clean.Length == 0 ? "episode" : clean;
}

static void PrintHelp() => Console.WriteLine("""
    YotoSpike — validate Yoto API assumptions

      login   [--port 8787]
      refresh
      list
      get     <cardId>
      feed    [--feed URL] [--count N]
      push    [--card ID] [--count N] [--feed URL] [--title T] [--no-cover] [--dry-run]
      streams --base URL [--card ID] [--run R] [--title T]

    Global: --client-id ID (or env YOTO_CLIENT_ID)  --data DIR (default ./.yoto-spike)  --scopes "..."
    """);
