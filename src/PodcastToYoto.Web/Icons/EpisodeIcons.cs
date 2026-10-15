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

    /// <summary>What an episode shows without an emoji match: the podcast's logo icon when it's turned on, else the player default.</summary>
    public static ShownIcon PodcastDefault(Subscription podcast) =>
        podcast.LogoIconMediaId is not null && podcast.LogoIcon != LogoIconMode.Off
            ? new ShownIcon(podcast.LogoIconUrl, "Podcast logo", "Podcast logo")
            : new ShownIcon(null, "Default", "Default player icon");

    public static ShownIcon Describe(Episode e, Subscription podcast, IconIndex? index, IconMatch? match)
    {
        if (e.IconMediaId == Episode.PodcastDefaultIcon)
        {
            var d = PodcastDefault(podcast);
            return d with { Tip = $"{d.Tip} (automatic icon cleared)" };
        }

        if (e.IconMediaId == Episode.NoIcon)
            return new ShownIcon(null, "Default", "Default player icon (cleared)");

        if (e.IconMediaId is { } id)
        {
            var picked = index?.Find(id);
            return new ShownIcon(picked?.Url, picked?.Title ?? id, $"{picked?.Title ?? id} (picked by hand)");
        }

        if (e.SourceIconRef is { } source)
        {
            var sourceId = source.StartsWith("yoto:#", StringComparison.Ordinal) ? source["yoto:#".Length..] : source;
            var icon = index?.Find(sourceId);
            return new ShownIcon(icon?.Url, icon?.Title ?? "Source icon", $"{icon?.Title ?? "Icon"} (from the Yoto playlist)");
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
