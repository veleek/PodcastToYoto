using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PodcastToYoto.Yoto;

// Request models for POST /content. Unknown/loosely-typed values (channels, format) are kept as
// JsonNode so whatever the transcoder reports is passed through untouched.

public sealed class YotoContentRequest
{
    /// <summary>Omit to create a new playlist; set to update an existing one in place.</summary>
    public string? CardId { get; set; }
    public required string Title { get; set; }
    public required YotoContentBody Content { get; set; }
    public YotoMetadata? Metadata { get; set; }
}

public sealed class YotoContentBody
{
    public required List<YotoChapter> Chapters { get; set; }
    public JsonObject? Config { get; set; }
    public string? PlaybackType { get; set; }
}

public sealed class YotoChapter
{
    public required string Key { get; set; }
    public required string Title { get; set; }
    public string? OverlayLabel { get; set; }
    public required List<YotoTrack> Tracks { get; set; }
    public YotoDisplay? Display { get; set; }
}

public sealed class YotoTrack
{
    public required string Key { get; set; }
    public required string Title { get; set; }
    public required string TrackUrl { get; set; }
    public long? Duration { get; set; }
    public long? FileSize { get; set; }
    public JsonNode? Channels { get; set; }
    public JsonNode? Format { get; set; }
    public string Type { get; set; } = "audio";
    public string? OverlayLabel { get; set; }
    public YotoDisplay? Display { get; set; }
}

public sealed class YotoDisplay
{
    [JsonPropertyName("icon16x16")]
    public string? Icon16x16 { get; set; }
}

public sealed class YotoMetadata
{
    public string? Description { get; set; }
    public string? Author { get; set; }
    public YotoCover? Cover { get; set; }
    public YotoMediaInfo? Media { get; set; }
}

public sealed class YotoCover
{
    public string? ImageL { get; set; }
}

public sealed class YotoMediaInfo
{
    public long Duration { get; set; }
    public long FileSize { get; set; }
}

/// <summary>Result of a completed transcode; everything needed to reference the audio in a track.</summary>
public sealed record YotoTranscodedAudio(
    string TranscodedSha256,
    long? Duration,
    long? FileSize,
    JsonNode? Channels,
    JsonNode? Format,
    JsonElement RawInfo)
{
    public string TrackUrl => $"yoto:#{TranscodedSha256}";
}

public sealed record YotoUploadUrl(string UploadId, string? UploadUrl);

/// <summary>A 16x16 display icon. Referenced in content as <c>yoto:#{MediaId}</c>.</summary>
public sealed record YotoIcon(string MediaId, string Title, string? Url, IReadOnlyList<string> Tags)
{
    public string Ref => $"yoto:#{MediaId}";
}

public sealed record YotoCardSummary(string CardId, string? Title, DateTimeOffset? UpdatedAt, JsonElement Raw);

public sealed class YotoApiException(string message, int statusCode, string? body) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string? Body { get; } = body;
}
