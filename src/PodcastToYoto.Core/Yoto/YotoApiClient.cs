using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PodcastToYoto.Yoto;

/// <summary>Thin client for the Yoto REST API (https://yoto.dev/api/).</summary>
public sealed class YotoApiClient
{
    public static readonly Uri DefaultBaseAddress = new("https://api.yotoplay.com/");

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly YotoTokenProvider _tokens;
    private readonly Uri _base;

    public YotoApiClient(HttpClient http, YotoTokenProvider tokens, Uri? baseAddress = null)
    {
        _http = http;
        _tokens = tokens;
        _base = baseAddress ?? DefaultBaseAddress;
    }

    // ---- Content -------------------------------------------------------------------------

    /// <summary>GET /content/mine — the user's MYO playlists (without chapters).</summary>
    public async Task<IReadOnlyList<YotoCardSummary>> GetMyContentAsync(CancellationToken ct = default)
    {
        using var doc = await SendJsonAsync(HttpMethod.Get, "content/mine", null, ct);
        var cards = doc.RootElement.TryGetProperty("cards", out var c) ? c : doc.RootElement;
        var list = new List<YotoCardSummary>();
        if (cards.ValueKind != JsonValueKind.Array) return list;

        foreach (var card in cards.EnumerateArray())
        {
            list.Add(new YotoCardSummary(
                card.GetProperty("cardId").GetString()!,
                card.TryGetProperty("title", out var t) ? t.GetString() : null,
                card.TryGetProperty("updatedAt", out var u) && u.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(u.GetString(), out var dt) ? dt : null,
                card.Clone()));
        }
        return list;
    }

    /// <summary>GET /content/{cardId} — full playlist including chapters. Returns the raw JSON document.</summary>
    public Task<JsonDocument> GetContentAsync(string cardId, CancellationToken ct = default) =>
        SendJsonAsync(HttpMethod.Get, $"content/{Uri.EscapeDataString(cardId)}", null, ct);

    /// <summary>POST /content — create (no cardId) or update (with cardId). Returns the resulting card JSON.</summary>
    public async Task<JsonElement> CreateOrUpdateContentAsync(YotoContentRequest request, CancellationToken ct = default)
    {
        var body = JsonContent.Create(request, options: Json);
        using var doc = await SendJsonAsync(HttpMethod.Post, "content", body, ct);
        return doc.RootElement.TryGetProperty("card", out var card) ? card.Clone() : doc.RootElement.Clone();
    }

    // ---- Media ---------------------------------------------------------------------------

    /// <summary>GET /media/transcode/audio/uploadUrl. UploadUrl is null when Yoto already has a file with this hash.</summary>
    public async Task<YotoUploadUrl> GetUploadUrlAsync(string sha256Hex, string? filename, CancellationToken ct = default)
    {
        var path = $"media/transcode/audio/uploadUrl?sha256={Uri.EscapeDataString(sha256Hex)}";
        if (!string.IsNullOrEmpty(filename)) path += $"&filename={Uri.EscapeDataString(filename)}";

        using var doc = await SendJsonAsync(HttpMethod.Get, path, null, ct);
        var upload = doc.RootElement.GetProperty("upload");
        var url = upload.TryGetProperty("uploadUrl", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
        return new YotoUploadUrl(upload.GetProperty("uploadId").GetString()!, url);
    }

    /// <summary>PUT the audio bytes to the pre-signed upload URL (no Yoto auth header).</summary>
    public async Task UploadAudioAsync(string uploadUrl, Stream audio, string contentType, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = new StreamContent(audio) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        if (audio.CanSeek) req.Content.Headers.ContentLength = audio.Length - audio.Position;

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            throw new YotoApiException($"Audio upload failed: {(int)resp.StatusCode}", (int)resp.StatusCode, text);
        }
    }

    /// <summary>Poll GET /media/upload/{uploadId}/transcoded until transcodedSha256 is present.</summary>
    public async Task<YotoTranscodedAudio> WaitForTranscodeAsync(string uploadId, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromMinutes(10));
        var delay = TimeSpan.FromMilliseconds(500);

        while (true)
        {
            using (var doc = await SendJsonAsync(HttpMethod.Get, $"media/upload/{Uri.EscapeDataString(uploadId)}/transcoded?loudnorm=false", null, ct, allowNotFound: true))
            {
                if (doc.RootElement.TryGetProperty("transcode", out var tc)
                    && tc.TryGetProperty("transcodedSha256", out var sha)
                    && sha.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(sha.GetString()))
                {
                    var info = tc.TryGetProperty("transcodedInfo", out var i) ? i.Clone() : default;
                    return new YotoTranscodedAudio(
                        sha.GetString()!,
                        ReadLong(info, "duration"),
                        ReadLong(info, "fileSize"),
                        ReadNode(info, "channels"),
                        ReadNode(info, "format"),
                        info);
                }
            }

            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException($"Transcode of upload {uploadId} did not complete in time.");

            await Task.Delay(delay, ct);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.5, 5000));
        }
    }

    /// <summary>POST /media/coverImage/user/me/upload?imageUrl=... — let Yoto fetch and convert an image. Returns mediaUrl.</summary>
    public async Task<string?> UploadCoverImageFromUrlAsync(string imageUrl, string coverType = "myo", CancellationToken ct = default)
    {
        var path = $"media/coverImage/user/me/upload?imageUrl={Uri.EscapeDataString(imageUrl)}&autoconvert=true&coverType={Uri.EscapeDataString(coverType)}";
        using var doc = await SendJsonAsync(HttpMethod.Post, path, null, ct);
        return doc.RootElement.TryGetProperty("coverImage", out var ci) && ci.TryGetProperty("mediaUrl", out var url)
            ? url.GetString()
            : null;
    }

    // ---- Icons ---------------------------------------------------------------------------

    /// <summary>GET /media/displayIcons/user/yoto — Yoto's public 16x16 icon library (needs user:icons:manage).</summary>
    public Task<IReadOnlyList<YotoIcon>> GetPublicIconsAsync(CancellationToken ct = default) =>
        GetIconsAsync("media/displayIcons/user/yoto", ct);

    /// <summary>GET /media/displayIcons/user/me — custom icons uploaded to this account (needs user:icons:manage).</summary>
    public Task<IReadOnlyList<YotoIcon>> GetUserIconsAsync(CancellationToken ct = default) =>
        GetIconsAsync("media/displayIcons/user/me", ct);

    private async Task<IReadOnlyList<YotoIcon>> GetIconsAsync(string path, CancellationToken ct)
    {
        using var doc = await SendJsonAsync(HttpMethod.Get, path, null, ct);
        var list = new List<YotoIcon>();
        if (!doc.RootElement.TryGetProperty("displayIcons", out var icons) || icons.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var i in icons.EnumerateArray())
        {
            var mediaId = i.TryGetProperty("mediaId", out var m) ? m.GetString() : null;
            if (string.IsNullOrEmpty(mediaId)) continue;

            var tags = new List<string>();
            if (i.TryGetProperty("publicTags", out var t) && t.ValueKind == JsonValueKind.Array)
                tags.AddRange(t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));

            list.Add(new YotoIcon(
                mediaId,
                i.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String ? title.GetString()! : "",
                i.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String ? url.GetString() : null,
                tags));
        }
        return list;
    }

    /// <summary>
    /// POST /media/displayIcons/user/me/upload — upload a custom 16x16 icon (PNG, or GIF with <paramref name="contentType"/>
    /// image/gif). With <paramref name="autoConvert"/> Yoto resizes the image itself (and flattens an animated GIF to a
    /// static PNG); without, it must already be 16x16, and GIFs keep their animation. Re-uploading identical bytes returns
    /// the same icon.
    /// </summary>
    public async Task<YotoCustomIcon> UploadCustomIconAsync(byte[] png, string? filename = null, bool autoConvert = false, CancellationToken ct = default,
        string contentType = "image/png")
    {
        var path = $"media/displayIcons/user/me/upload?autoConvert={(autoConvert ? "true" : "false")}";
        if (!string.IsNullOrEmpty(filename)) path += $"&filename={Uri.EscapeDataString(filename)}";

        var body = new ByteArrayContent(png);
        body.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var doc = await SendJsonAsync(HttpMethod.Post, path, body, ct);

        var icon = doc.RootElement.TryGetProperty("displayIcon", out var d) ? d : doc.RootElement;
        var mediaId = icon.TryGetProperty("mediaId", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        if (string.IsNullOrEmpty(mediaId))
            throw new YotoApiException("Icon upload returned no mediaId.", 200, doc.RootElement.GetRawText());
        return new YotoCustomIcon(mediaId, icon.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null);
    }

    // ---- Plumbing ------------------------------------------------------------------------

    private async Task<JsonDocument> SendJsonAsync(HttpMethod method, string relative, HttpContent? content, CancellationToken ct, bool allowNotFound = false)
    {
        const int maxAttempts = 5;
        byte[]? bufferedBody = content is null ? null : await content.ReadAsByteArrayAsync(ct);
        var contentType = content?.Headers.ContentType;

        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, new Uri(_base, relative));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _tokens.GetAccessTokenAsync(ct));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (bufferedBody is not null)
            {
                req.Content = new ByteArrayContent(bufferedBody);
                req.Content.Headers.ContentType = contentType;
            }

            using var resp = await _http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);

            if (resp.IsSuccessStatusCode)
                return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);

            if (allowNotFound && resp.StatusCode == HttpStatusCode.NotFound)
                return JsonDocument.Parse("{}");

            var retryable = resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500;
            if (!retryable || attempt >= maxAttempts)
                throw new YotoApiException($"{method} {relative} failed: {(int)resp.StatusCode} {Truncate(text)}", (int)resp.StatusCode, text);

            var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
            await Task.Delay(wait, ct);
        }
    }

    private static long? ReadLong(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var l) => l,
            JsonValueKind.Number => (long)Math.Round(v.GetDouble()),
            JsonValueKind.String when double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => (long)Math.Round(d),
            _ => null,
        };
    }

    private static JsonNode? ReadNode(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) ? JsonNode.Parse(v.GetRawText()) : null;

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500] + "…";
}
