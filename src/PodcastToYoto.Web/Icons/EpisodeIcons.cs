using PodcastToYoto.Web.Data;

namespace PodcastToYoto.Web.Icons;

/// <summary>An episode's icon as the UI shows it: image URL (null = player default), and why it was chosen.</summary>
public sealed record ShownIcon(string? Url, string Title, string Tip);

/// <summary>Mirrors the sync's icon choice (<see cref="Sync.SyncEngine.IconFor"/>) for display.</summary>
public static class EpisodeIcons
{
    /// <summary>Emoji match for an episode, or null when the podcast has matching turned off or the library isn't loaded.</summary>
    public static IconMatch? Match(Episode e, Subscription podcast, IconIndex? index) =>
        podcast.UseDefaultIcon || index is null ? null : IconMatcher.Match(e.Title, index);

    public static ShownIcon Describe(Episode e, Subscription podcast, IconIndex? index, IconMatch? match)
    {
        if (e.IconMediaId is { } id)
        {
            var picked = index?.Find(id);
            return new ShownIcon(picked?.Url, picked?.Title ?? id, $"{picked?.Title ?? id} (picked by hand)");
        }

        var logo = podcast.LogoIconMediaId is not null && podcast.LogoIcon != LogoIconMode.Off;
        if (logo && podcast.LogoIcon == LogoIconMode.Always)
            return new ShownIcon(podcast.LogoIconUrl, "Podcast logo", "Podcast logo (used on every episode)");

        if (match is not null)
            return new ShownIcon(match.Icon.Url, match.Icon.Title, $"{match.Icon.Title} ({match.Reason})");

        return logo
            ? new ShownIcon(podcast.LogoIconUrl, "Podcast logo", "Podcast logo (no emoji match)")
            : new ShownIcon(null, "Default", "Default player icon");
    }
}
