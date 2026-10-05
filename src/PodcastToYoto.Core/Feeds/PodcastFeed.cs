namespace PodcastToYoto.Feeds;

public sealed record PodcastFeed(
    string Title,
    string? Author,
    string? Description,
    string? ImageUrl,
    IReadOnlyList<PodcastEpisode> Episodes);

public sealed record PodcastEpisode(
    string Guid,
    string Title,
    DateTimeOffset? Published,
    string EnclosureUrl,
    string? EnclosureType,
    long? EnclosureLength,
    TimeSpan? Duration,
    string? EpisodeType,
    string? ImageUrl,
    string? Description);
