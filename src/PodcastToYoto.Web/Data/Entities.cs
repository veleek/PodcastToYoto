namespace PodcastToYoto.Web.Data;

public enum AccountStatus { Disconnected, Connected, NeedsReauth }
public enum PlaylistOrder { NewestFirst, OldestFirst }
public enum SyncTrigger { Schedule, Manual, Startup }
public enum SyncResult { Running, NoChange, Updated, Failed, DryRun }

/// <summary>The single connected Yoto account (Id is always 1).</summary>
public sealed class YotoAccount
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>Data-Protection encrypted.</summary>
    public string? ProtectedAccessToken { get; set; }

    /// <summary>Data-Protection encrypted.</summary>
    public string? ProtectedRefreshToken { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }
    public string? Scope { get; set; }
    public AccountStatus Status { get; set; } = AccountStatus.Disconnected;
    public string? LastError { get; set; }
    public DateTimeOffset? ConnectedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Subscription
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string FeedUrl { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Target Yoto playlist; null until created on first sync.</summary>
    public string? YotoCardId { get; set; }

    public string? TitleOverride { get; set; }
    public int RecentCount { get; set; } = 25;
    public int MaxTracks { get; set; } = 100;
    public PlaylistOrder Order { get; set; } = PlaylistOrder.NewestFirst;
    public bool IncludeTrailers { get; set; }

    /// <summary>Null → global default.</summary>
    public TimeSpan? PollInterval { get; set; }

    public string? CoverSourceUrl { get; set; }
    public string? CoverMediaUrl { get; set; }

    public string? FeedETag { get; set; }
    public string? FeedLastModified { get; set; }
    public DateTimeOffset? LastPolledAt { get; set; }

    public string? LastPublishedHash { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }
    public SyncResult? LastSyncResult { get; set; }
    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public List<Episode> Episodes { get; set; } = [];
}

public sealed class Episode
{
    public int Id { get; set; }
    public int SubscriptionId { get; set; }
    public Subscription? Subscription { get; set; }

    public required string Guid { get; set; }
    public required string Title { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public required string EnclosureUrl { get; set; }
    public long? EnclosureLength { get; set; }
    public int? DurationSeconds { get; set; }
    public string? EpisodeType { get; set; }
    public string? ImageUrl { get; set; }

    public bool IsFavourite { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset? MissingFromFeedSince { get; set; }

    public int? AudioId { get; set; }
    public Audio? Audio { get; set; }
}

/// <summary>An audio file Yoto has transcoded. One row per distinct source file, shared across subscriptions.</summary>
public sealed class Audio
{
    public int Id { get; set; }
    public required string SourceSha256 { get; set; }
    public required string TranscodedSha256 { get; set; }
    public long? DurationSeconds { get; set; }
    public long? FileSize { get; set; }

    /// <summary>Raw JSON values as reported by the transcoder (e.g. "stereo", "opus").</summary>
    public string? ChannelsJson { get; set; }
    public string? FormatJson { get; set; }

    public DateTimeOffset UploadedAt { get; set; }
}

public sealed class SyncRun
{
    public int Id { get; set; }
    public int SubscriptionId { get; set; }
    public Subscription? Subscription { get; set; }

    public SyncTrigger Trigger { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public SyncResult Result { get; set; } = SyncResult.Running;
    public int NewEpisodes { get; set; }
    public int Uploaded { get; set; }
    public string? Message { get; set; }
}
