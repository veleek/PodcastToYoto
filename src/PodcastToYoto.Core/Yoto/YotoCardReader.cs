using System.Text.Json;

namespace PodcastToYoto.Yoto;

/// <summary>
/// One track of an existing Yoto playlist. <see cref="Sha"/> is set for uploaded audio (<c>trackUrl</c> "yoto:#sha");
/// null for anything else, such as stream tracks (an https URL fetched at play time).
/// </summary>
public sealed record YotoCardTrack(
    int Index,
    string? Title,
    string? TrackUrl,
    string? Type,
    string? Sha,
    long? Duration,
    long? FileSize,
    string? ChannelsJson,
    string? FormatJson,
    string? IconRef);

/// <summary>Reads the chapters/tracks of a <c>GET /content/{cardId}</c> response.</summary>
public static class YotoCardReader
{
    public const string RefPrefix = "yoto:#";

    /// <summary>The card object: the response's <c>card</c> property, or the root itself.</summary>
    public static JsonElement Card(JsonDocument doc) =>
        doc.RootElement.TryGetProperty("card", out var c) ? c : doc.RootElement;

    public static string? Title(JsonElement card) => Str(card, "title");

    public static string? CoverUrl(JsonElement card) =>
        card.TryGetProperty("metadata", out var md) && md.ValueKind == JsonValueKind.Object
        && md.TryGetProperty("cover", out var cover) && cover.ValueKind == JsonValueKind.Object
            ? Str(cover, "imageL")
            : null;

    /// <summary>Every track in playlist order. A track without a title or icon takes its chapter's.</summary>
    public static IEnumerable<YotoCardTrack> Tracks(JsonElement card)
    {
        if (!card.TryGetProperty("content", out var content) || !content.TryGetProperty("chapters", out var chapters)
            || chapters.ValueKind != JsonValueKind.Array)
            yield break;

        var index = 0;
        foreach (var ch in chapters.EnumerateArray())
        {
            if (!ch.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array) continue;
            var chapterTitle = Str(ch, "title");
            var chapterIcon = Icon(ch);

            foreach (var t in tracks.EnumerateArray())
            {
                var url = Str(t, "trackUrl");
                var sha = url is not null && url.StartsWith(RefPrefix, StringComparison.Ordinal) && url.Length > RefPrefix.Length
                    ? url[RefPrefix.Length..]
                    : null;
                yield return new YotoCardTrack(
                    index++,
                    Str(t, "title") ?? chapterTitle,
                    url,
                    Str(t, "type"),
                    sha,
                    Num(t, "duration"),
                    Num(t, "fileSize"),
                    Raw(t, "channels"),
                    Raw(t, "format"),
                    Icon(t) ?? chapterIcon);
            }
        }
    }

    private static string? Icon(JsonElement e) =>
        e.TryGetProperty("display", out var d) && d.ValueKind == JsonValueKind.Object ? Str(d, "icon16x16") : null;

    private static string? Raw(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? v.GetRawText() : null;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (long)Math.Round(v.GetDouble()) : null;
}
