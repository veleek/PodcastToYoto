using System.ComponentModel.DataAnnotations;
using PodcastToYoto.Web.Data;

namespace PodcastToYoto.Web.Subscriptions;

/// <summary>Editable settings of one podcast within a playlist (form model).</summary>
public sealed class SubscriptionSettings
{
    [Required, StringLength(200)]
    public string Name { get; set; } = "";

    [Required, Url]
    public string FeedUrl { get; set; } = "";

    /// <summary>Included in the playlist.</summary>
    public bool Enabled { get; set; } = true;

    [Range(1, 100, ErrorMessage = "Between 1 and 100.")]
    public int RecentCount { get; set; } = 25;

    [Range(1, 100, ErrorMessage = "Between 1 and 100 (Yoto's track limit).")]
    public int MaxTracks { get; set; } = 100;

    /// <summary>Order of this podcast's episodes when the playlist groups by podcast.</summary>
    public PlaylistOrder Order { get; set; } = PlaylistOrder.NewestFirst;

    public bool IncludeTrailers { get; set; }

    /// <summary>Pick each episode's icon from the emoji in its title.</summary>
    public bool MatchIcons { get; set; } = true;

    /// <summary>When to show the podcast's own logo icon (needs one generated first).</summary>
    public LogoIconMode LogoIcon { get; set; } = LogoIconMode.Off;

    public static SubscriptionSettings From(Subscription s) => new()
    {
        Name = s.Name,
        FeedUrl = s.FeedUrl,
        Enabled = s.Enabled,
        RecentCount = s.RecentCount,
        MaxTracks = s.MaxTracks,
        Order = s.Order,
        IncludeTrailers = s.IncludeTrailers,
        MatchIcons = !s.UseDefaultIcon,
        LogoIcon = s.LogoIcon,
    };

    public void ApplyTo(Subscription s)
    {
        s.Name = Name.Trim();
        s.FeedUrl = FeedUrl.Trim();
        s.Enabled = Enabled;
        s.RecentCount = RecentCount;
        s.MaxTracks = MaxTracks;
        s.Order = Order;
        s.IncludeTrailers = IncludeTrailers;
        s.UseDefaultIcon = !MatchIcons;
        s.LogoIcon = LogoIcon;
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

    public TrackOrder Order { get; set; } = TrackOrder.NewestFirst;

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
        PollIntervalMinutes = p.PollInterval is { } i ? (int)i.TotalMinutes : null,
        CoverImageUrl = p.CoverImageUrl,
    };

    public void ApplyTo(Playlist p)
    {
        p.Name = Name.Trim();
        p.Enabled = Enabled;
        p.MaxTracks = MaxTracks;
        p.Order = Order;
        p.PollInterval = PollIntervalMinutes is { } m ? TimeSpan.FromMinutes(m) : null;
        p.CoverImageUrl = string.IsNullOrWhiteSpace(CoverImageUrl) ? null : CoverImageUrl.Trim();
    }
}
