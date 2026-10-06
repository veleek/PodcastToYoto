using System.ComponentModel.DataAnnotations;
using PodcastToYoto.Web.Data;

namespace PodcastToYoto.Web.Subscriptions;

/// <summary>Editable subscription settings (form model).</summary>
public sealed class SubscriptionSettings
{
    [Required, StringLength(200)]
    public string Name { get; set; } = "";

    [Required, Url]
    public string FeedUrl { get; set; } = "";

    public bool Enabled { get; set; } = true;

    [StringLength(200)]
    public string? TitleOverride { get; set; }

    [Range(1, 100, ErrorMessage = "Between 1 and 100.")]
    public int RecentCount { get; set; } = 25;

    [Range(1, 100, ErrorMessage = "Between 1 and 100 (Yoto's track limit).")]
    public int MaxTracks { get; set; } = 100;

    public PlaylistOrder Order { get; set; } = PlaylistOrder.NewestFirst;

    public bool IncludeTrailers { get; set; }

    /// <summary>Pick each episode's icon from the emoji in its title.</summary>
    public bool MatchIcons { get; set; } = true;

    /// <summary>Null → global default.</summary>
    [Range(5, 24 * 60, ErrorMessage = "Between 5 and 1440 minutes.")]
    public int? PollIntervalMinutes { get; set; }

    public static SubscriptionSettings From(Subscription s) => new()
    {
        Name = s.Name,
        FeedUrl = s.FeedUrl,
        Enabled = s.Enabled,
        TitleOverride = s.TitleOverride,
        RecentCount = s.RecentCount,
        MaxTracks = s.MaxTracks,
        Order = s.Order,
        IncludeTrailers = s.IncludeTrailers,
        MatchIcons = !s.UseDefaultIcon,
        PollIntervalMinutes = s.PollInterval is { } p ? (int)p.TotalMinutes : null,
    };

    public void ApplyTo(Subscription s)
    {
        s.Name = Name.Trim();
        s.FeedUrl = FeedUrl.Trim();
        s.Enabled = Enabled;
        s.TitleOverride = string.IsNullOrWhiteSpace(TitleOverride) ? null : TitleOverride.Trim();
        s.RecentCount = RecentCount;
        s.MaxTracks = MaxTracks;
        s.Order = Order;
        s.IncludeTrailers = IncludeTrailers;
        s.UseDefaultIcon = !MatchIcons;
        s.PollInterval = PollIntervalMinutes is { } m ? TimeSpan.FromMinutes(m) : null;
    }
}
