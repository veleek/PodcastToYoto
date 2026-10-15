namespace PodcastToYoto.Web.Data;

public enum AccountStatus { Disconnected, Connected, NeedsReauth }
/// <summary>
/// Track order: by release date, or by title (ignoring leading emoji and punctuation, numbers in numeric order).
/// <see cref="Interleaved"/> is for playlists only: one track from each source in turn, each source in its own order.
/// </summary>
public enum PlaylistOrder { NewestFirst, OldestFirst, TitleAscending, TitleDescending, Interleaved }

/// <summary>When a podcast's own logo icon (generated from its artwork and uploaded to Yoto) is shown on the player.</summary>
public enum LogoIconMode { Off, Fallback, Always }
public enum SyncTrigger { Schedule, Manual, Startup }

/// <summary>Where a <see cref="Subscription"/>'s tracks come from: a podcast feed, or an existing Yoto (MYO) playlist.</summary>
public enum SourceKind { Podcast, YotoPlaylist }

/// <summary>
/// How a Yoto playlist source picks its non-favourite tracks: the first <see cref="Subscription.RecentCount"/> in its order,
/// or a weekly rotation through the whole source (<see cref="Subscription.RotationNumber"/>).
/// </summary>
public enum TrackSelection { First, RotateWeekly }
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

/// <summary>A Yoto playlist (MYO card) kept up to date from one or more podcast subscriptions.</summary>
public sealed class Playlist
{
    public int Id { get; set; }

    /// <summary>Title the Yoto playlist is published with.</summary>
    public required string Name { get; set; }

    /// <summary>Polled and synced automatically.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Target Yoto playlist; null until created on first sync.</summary>
    public string? YotoCardId { get; set; }

    /// <summary>Cap on tracks across all podcasts (Yoto's limit is 100).</summary>
    public int MaxTracks { get; set; } = 100;

    /// <summary>
    /// Release-date order of the tracks (within each podcast when <see cref="GroupByPodcast"/>), or
    /// <see cref="PlaylistOrder.Interleaved"/>, which ignores <see cref="GroupByPodcast"/>.
    /// </summary>
    public PlaylistOrder Order { get; set; } = PlaylistOrder.NewestFirst;

    /// <summary>Keep each podcast's tracks together, podcasts in <see cref="Subscription.Position"/> order.</summary>
    public bool GroupByPodcast { get; set; }

    /// <summary>Null → global default.</summary>
    public TimeSpan? PollInterval { get; set; }

    /// <summary>Cover image to use instead of the first podcast's artwork. Null = first podcast's artwork.</summary>
    public string? CoverImageUrl { get; set; }

    /// <summary>The image <see cref="CoverMediaUrl"/> was uploaded from; re-uploaded when the desired cover changes.</summary>
    public string? CoverSourceUrl { get; set; }
    public string? CoverMediaUrl { get; set; }

    public string? LastPublishedHash { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }
    public SyncResult? LastSyncResult { get; set; }
    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public List<Subscription> Subscriptions { get; set; } = [];

    /// <summary>The image the playlist cover should be made from.</summary>
    public string? DesiredCoverUrl => CoverImageUrl ?? Subscriptions
        .Where(s => s.Enabled && s.CoverSourceUrl is not null)
        .OrderBy(s => s.Position).ThenBy(s => s.Id)
        .Select(s => s.CoverSourceUrl)
        .FirstOrDefault();
}

/// <summary>
/// One source feeding a <see cref="Playlist"/>, with its own episode selection rules: a podcast feed, or an existing Yoto
/// playlist whose tracks are reused (<see cref="Kind"/>).
/// </summary>
public sealed class Subscription
{
    public int Id { get; set; }

    public SourceKind Kind { get; set; } = SourceKind.Podcast;
    public bool IsYotoPlaylist => Kind == SourceKind.YotoPlaylist;

    /// <summary>The Yoto playlist the tracks are read from (<see cref="SourceKind.YotoPlaylist"/> only).</summary>
    public string? SourceCardId { get; set; }
    public int PlaylistId { get; set; }
    public Playlist? Playlist { get; set; }

    /// <summary>Order within the playlist: preference order, and group order when grouping by podcast.</summary>
    public int Position { get; set; }

    public required string Name { get; set; }
    public required string FeedUrl { get; set; }

    /// <summary>Included in the playlist. Excluded podcasts aren't polled or published.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Podcasts: the newest N episodes. Yoto playlist sources: the first N tracks in <see cref="Order"/>. Both on top of
    /// the favourites (picked tracks).
    /// </summary>
    public int RecentCount { get; set; } = 25;

    /// <summary>Most episodes this podcast contributes (recent + favourites).</summary>
    public int MaxTracks { get; set; } = 100;

    /// <summary>
    /// This podcast's own episode order, used when the playlist groups by podcast and <see cref="UseOwnOrder"/> is set;
    /// otherwise the playlist's order applies. Yoto playlist sources always use their own order (the date orders mean the
    /// source playlist's track order, and its reverse); it also decides which tracks are the first <see cref="RecentCount"/>.
    /// </summary>
    public PlaylistOrder Order { get; set; } = PlaylistOrder.NewestFirst;

    public bool UseOwnOrder { get; set; }

    /// <summary>Yoto playlist sources: first N, or a weekly rotation of N (see <see cref="Sync.TrackRotation"/>).</summary>
    public TrackSelection Selection { get; set; } = TrackSelection.First;

    /// <summary>The current rotation (0 = none yet); its tracks have this <see cref="Episode.RotationNumber"/>.</summary>
    public int RotationNumber { get; set; }

    /// <summary>When the current rotation's week began; null = rotate on the next sync.</summary>
    public DateTimeOffset? RotationStartedAt { get; set; }

    public bool IncludeTrailers { get; set; }

    /// <summary>
    /// Opt-out of icon matching: true = leave the player's default icon. (Stored inverted so existing rows,
    /// which get the column default false, keep icon matching on.)
    /// </summary>
    public bool UseDefaultIcon { get; set; }

    public LogoIconMode LogoIcon { get; set; } = LogoIconMode.Off;

    /// <summary>Custom 16x16 icon generated from the podcast's artwork and uploaded to Yoto.</summary>
    public string? LogoIconMediaId { get; set; }

    /// <summary>Where Yoto serves <see cref="LogoIconMediaId"/> from (for display).</summary>
    public string? LogoIconUrl { get; set; }

    /// <summary>The feed's artwork.</summary>
    public string? CoverSourceUrl { get; set; }

    public string? FeedETag { get; set; }
    public string? FeedLastModified { get; set; }
    public DateTimeOffset? LastPolledAt { get; set; }

    /// <summary>Last feed error ("Feed: …"), cleared by a successful poll.</summary>
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

    /// <summary>
    /// Icon chosen by hand (Yoto media id); overrides automatic emoji matching. Null = automatic.
    /// <see cref="PodcastDefaultIcon"/> = skip the emoji match and use the podcast's default (its logo icon, or none);
    /// <see cref="NoIcon"/> = no icon at all (the player's default).
    /// </summary>
    public string? IconMediaId { get; set; }

    public const string PodcastDefaultIcon = "podcast-default";
    public const string NoIcon = "none";
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset? MissingFromFeedSince { get; set; }

    /// <summary>Position in the source Yoto playlist (Yoto playlist sources only).</summary>
    public int? SourceIndex { get; set; }

    /// <summary>The icon the track has in its source Yoto playlist ("yoto:#…"); its default icon here.</summary>
    public string? SourceIconRef { get; set; }

    /// <summary>The last rotation that put this track on the playlist (<see cref="Subscription.RotationNumber"/>); null = never.</summary>
    public int? RotationNumber { get; set; }

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
    public int PlaylistId { get; set; }
    public Playlist? Playlist { get; set; }

    public SyncTrigger Trigger { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public SyncResult Result { get; set; } = SyncResult.Running;
    public int NewEpisodes { get; set; }
    public int Uploaded { get; set; }
    public string? Message { get; set; }
}
