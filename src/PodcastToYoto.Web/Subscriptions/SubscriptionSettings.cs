using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using PodcastToYoto.Web.Data;

namespace PodcastToYoto.Web.Subscriptions;

/// <summary>Editable settings of one podcast within a playlist (form model).</summary>
public sealed class SubscriptionSettings
{
    /// <summary>Podcast feed, or an existing Yoto playlist (see <see cref="ForYotoPlaylist"/>). Not editable.</summary>
    public SourceKind Kind { get; set; } = SourceKind.Podcast;

    /// <summary>The source Yoto playlist (<see cref="SourceKind.YotoPlaylist"/> only).</summary>
    public string? SourceCardId { get; set; }

    [JsonIgnore]
    public bool IsYotoPlaylist => Kind == SourceKind.YotoPlaylist;

    /// <summary>
    /// Settings for a Yoto playlist source. <see cref="FeedUrl"/> is the playlist's page in the Yoto app, which keeps it
    /// unique within a playlist (import matches sources by it).
    /// </summary>
    public static SubscriptionSettings ForYotoPlaylist(string cardId, string name) => new()
    {
        Kind = SourceKind.YotoPlaylist,
        SourceCardId = cardId,
        Name = name,
        FeedUrl = YotoPlaylistUrl(cardId),
        RecentCount = 0, // just the picked tracks
        Order = PlaylistOrder.NewestFirst, // the source playlist's own order
        UseOwnOrder = true,
        MatchIcons = false,
    };

    public static string YotoPlaylistUrl(string cardId) => $"https://my.yotoplay.com/cards/{Uri.EscapeDataString(cardId)}";

    [Required, StringLength(200)]
    public string Name { get; set; } = "";

    [Required, Url]
    public string FeedUrl { get; set; } = "";

    /// <summary>Included in the playlist.</summary>
    public bool Enabled { get; set; } = true;

    [Range(0, 100, ErrorMessage = "Between 0 and 100.")]
    public int RecentCount { get; set; } = 25;

    [Range(1, 100, ErrorMessage = "Between 1 and 100 (Yoto's track limit).")]
    public int MaxTracks { get; set; } = 100;

    /// <summary>Yoto playlist sources: the first <see cref="RecentCount"/> tracks, or a weekly rotation of that many.</summary>
    public TrackSelection Selection { get; set; } = TrackSelection.First;

    /// <summary>This podcast's own order when the playlist groups by podcast; only used with <see cref="UseOwnOrder"/>.</summary>
    public PlaylistOrder Order { get; set; } = PlaylistOrder.NewestFirst;

    public bool UseOwnOrder { get; set; }

    /// <summary>Form binding for the two above: null = same as the playlist.</summary>
    [JsonIgnore]
    public PlaylistOrder? GroupOrder
    {
        get => UseOwnOrder ? Order : null;
        set
        {
            UseOwnOrder = value is not null;
            if (value is { } o) Order = o;
        }
    }

    public bool IncludeTrailers { get; set; }

    /// <summary>Pick each episode's icon from the emoji in its title.</summary>
    public bool MatchIcons { get; set; } = true;

    /// <summary>When to show the podcast's own logo icon. Export only: it's set with the icon (SetLogoIconAsync), not by ApplyTo.</summary>
    public LogoIconMode LogoIcon { get; set; } = LogoIconMode.Off;

    public static SubscriptionSettings From(Subscription s) => new()
    {
        Kind = s.Kind,
        SourceCardId = s.SourceCardId,
        Name = s.Name,
        FeedUrl = s.FeedUrl,
        Enabled = s.Enabled,
        RecentCount = s.RecentCount,
        MaxTracks = s.MaxTracks,
        Selection = s.Selection,
        Order = s.Order,
        UseOwnOrder = s.UseOwnOrder,
        IncludeTrailers = s.IncludeTrailers,
        MatchIcons = !s.UseDefaultIcon,
        LogoIcon = s.LogoIcon,
    };

    public void ApplyTo(Subscription s)
    {
        s.Kind = Kind;
        s.SourceCardId = SourceCardId;
        s.Name = Name.Trim();
        s.FeedUrl = FeedUrl.Trim();
        s.Enabled = Enabled;
        s.MaxTracks = MaxTracks;
        // A new rotation size or mode starts a new week on the next sync.
        if (s.Selection != Selection || s.RecentCount != RecentCount) s.RotationStartedAt = null;
        s.Selection = Selection;
        s.RecentCount = RecentCount;
        s.Order = Order;
        s.UseOwnOrder = UseOwnOrder;
        s.IncludeTrailers = IncludeTrailers;
        s.UseDefaultIcon = !MatchIcons;
    }
}

/// <summary>Editable playlist settings (form model).</summary>
public sealed class PlaylistSettings
{
    /// <summary>Title the Yoto playlist is published with.</summary>
    [Required, StringLength(200)]
    public string Name { get; set; } = "";

    /// <summary>Polled and synced automatically.</summary>
    public bool Enabled { get; set; } = true;

    [Range(1, 100, ErrorMessage = "Between 1 and 100 (Yoto's track limit).")]
    public int MaxTracks { get; set; } = 100;

    [JsonIgnore]
    public PlaylistOrder Order { get; set; } = PlaylistOrder.NewestFirst;

    public bool GroupByPodcast { get; set; }

    /// <summary>
    /// <see cref="Order"/> as exported. Files written before the order and grouping were split could say "GroupedByPodcast";
    /// that reads as newest first, grouped.
    /// </summary>
    [JsonPropertyName("order")]
    public string OrderJson
    {
        get => Order.ToString();
        set
        {
            if (value == "GroupedByPodcast") GroupByPodcast = true;
            else Order = Enum.Parse<PlaylistOrder>(value, ignoreCase: true);
        }
    }

    /// <summary>Null → global default.</summary>
    [Range(5, 24 * 60, ErrorMessage = "Between 5 and 1440 minutes.")]
    public int? PollIntervalMinutes { get; set; }

    /// <summary>Null → first podcast's artwork.</summary>
    [Url]
    public string? CoverImageUrl { get; set; }

    public static PlaylistSettings From(Playlist p) => new()
    {
        Name = p.Name,
        Enabled = p.Enabled,
        MaxTracks = p.MaxTracks,
        Order = p.Order,
        GroupByPodcast = p.GroupByPodcast,
        PollIntervalMinutes = p.PollInterval is { } i ? (int)i.TotalMinutes : null,
        CoverImageUrl = p.CoverImageUrl,
    };

    public void ApplyTo(Playlist p)
    {
        p.Name = Name.Trim();
        p.Enabled = Enabled;
        p.MaxTracks = MaxTracks;
        p.Order = Order;
        p.GroupByPodcast = GroupByPodcast;
        p.PollInterval = PollIntervalMinutes is { } m ? TimeSpan.FromMinutes(m) : null;
        p.CoverImageUrl = string.IsNullOrWhiteSpace(CoverImageUrl) ? null : CoverImageUrl.Trim();
    }
}
