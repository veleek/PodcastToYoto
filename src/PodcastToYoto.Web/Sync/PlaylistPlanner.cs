using PodcastToYoto.Web.Data;

namespace PodcastToYoto.Web.Sync;

public sealed record PlannedPlaylist(IReadOnlyList<Episode> Episodes, IReadOnlyList<string> Warnings);

/// <summary>Decides which episodes go on the card, and in what order. Pure; no I/O.</summary>
public static class PlaylistPlanner
{
    /// <summary>
    /// A whole playlist: each included podcast picks its own episodes (newest N ∪ favourites, capped at its max), then the
    /// combined list is capped at the playlist's max and ordered by release date, optionally grouped by podcast.
    /// Uses each subscription's <see cref="Subscription.Episodes"/>.
    /// </summary>
    public static PlannedPlaylist Plan(Playlist playlist, IEnumerable<Subscription> subscriptions)
    {
        var warnings = new List<string>();
        var podcasts = subscriptions.Where(s => s.Enabled).OrderBy(s => s.Position).ThenBy(s => s.Id).ToList();
        var multi = podcasts.Count > 1;

        // Each podcast's picks, newest first.
        var groups = new List<(Subscription Sub, List<Episode> Episodes)>();
        foreach (var s in podcasts)
        {
            var (picked, w) = Select(s, s.Episodes);
            groups.Add((s, picked));
            warnings.AddRange(multi ? w.Select(x => $"{s.Name}: {x}") : w);
        }

        // Stable sort, so a single podcast keeps exactly its own order.
        var newestFirst = groups.SelectMany(g => g.Episodes).OrderByDescending(e => e.PublishedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(e => e.FirstSeenAt).ToList();

        var max = Math.Max(1, playlist.MaxTracks);
        if (newestFirst.Count > max)
        {
            var (kept, w) = Trim(newestFirst, max, podcast: false);
            warnings.Add(w);
            var keep = new HashSet<Episode>(kept, ReferenceEqualityComparer.Instance);
            foreach (var g in groups) g.Episodes.RemoveAll(e => !keep.Contains(e));
            newestFirst = kept;
        }

        IReadOnlyList<Episode> ordered = playlist.GroupByPodcast
            ? groups.SelectMany(g => Ordered(g.Episodes, g.Sub.UseOwnOrder ? g.Sub.Order : playlist.Order)).ToList()
            : Ordered(newestFirst, playlist.Order).ToList();

        if (podcasts.Count == 0) warnings.Add("No podcasts are included in this playlist.");
        return new PlannedPlaylist(ordered, warnings);
    }

    /// <summary>One podcast on its own, ordered by its <see cref="Subscription.Order"/>.</summary>
    public static PlannedPlaylist Plan(Subscription s, IEnumerable<Episode> episodes)
    {
        var (newestFirst, warnings) = Select(s, episodes);
        return new PlannedPlaylist(Ordered(newestFirst, s.Order).ToList(), warnings);
    }

    private static readonly StringComparer TitleComparer =
        StringComparer.Create(System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.NumericOrdering);

    /// <summary>Orders a newest-first list. Title orders are stable, so equal titles stay newest first.</summary>
    private static IEnumerable<Episode> Ordered(List<Episode> newestFirst, PlaylistOrder order) => order switch
    {
        PlaylistOrder.OldestFirst => newestFirst.AsEnumerable().Reverse(),
        PlaylistOrder.TitleAscending => newestFirst.OrderBy(e => TitleKey(e.Title), TitleComparer),
        PlaylistOrder.TitleDescending => newestFirst.OrderByDescending(e => TitleKey(e.Title), TitleComparer),
        _ => newestFirst,
    };

    /// <summary>Title without leading emoji, punctuation or spaces (e.g. "🪜 Rocking the Ladder 🪜" → "Rocking the Ladder 🪜").</summary>
    internal static string TitleKey(string title)
    {
        var i = 0;
        while (i < title.Length && !char.IsLetterOrDigit(title, i)) i += char.IsSurrogatePair(title, i) ? 2 : 1;
        return title[i..];
    }

    /// <summary>Newest <see cref="Subscription.RecentCount"/> ∪ favourites, capped at <see cref="Subscription.MaxTracks"/>; newest first.</summary>
    private static (List<Episode> NewestFirst, List<string> Warnings) Select(Subscription s, IEnumerable<Episode> episodes)
    {
        var warnings = new List<string>();

        var candidates = episodes
            .Where(e => s.IncludeTrailers || !string.Equals(e.EpisodeType, "trailer", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.PublishedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(e => e.FirstSeenAt)
            .ToList();

        // Recent = newest episodes still in the feed. Favourites may come from anywhere.
        var recent = candidates.Where(e => e.MissingFromFeedSince is null).Take(Math.Max(0, s.RecentCount));
        var selected = new HashSet<Episode>(recent, ReferenceEqualityComparer.Instance);
        foreach (var f in candidates.Where(e => e.IsFavourite)) selected.Add(f);

        var newestFirst = candidates.Where(selected.Contains).ToList();
        var max = Math.Max(1, s.MaxTracks);
        if (newestFirst.Count > max)
        {
            var (kept, w) = Trim(newestFirst, max, podcast: true);
            warnings.Add(w);
            newestFirst = kept;
        }

        return (newestFirst, warnings);
    }

    /// <summary>Drops the oldest non-favourites, then the oldest favourites, to get down to <paramref name="max"/>.</summary>
    private static (List<Episode> Kept, string Warning) Trim(List<Episode> newestFirst, int max, bool podcast)
    {
        // `newestFirst` walked backwards visits oldest first.
        var excess = newestFirst.Count - max;
        var drop = newestFirst.AsEnumerable().Reverse().Where(e => !e.IsFavourite).Take(excess).ToList();
        string warning;
        if (drop.Count < excess)
        {
            drop.AddRange(newestFirst.AsEnumerable().Reverse().Where(e => e.IsFavourite).Take(excess - drop.Count));
            warning = podcast
                ? $"More favourites than the {max}-episode cap; the oldest favourites were left off."
                : $"More favourites than the {max}-track limit; the oldest favourites were left off.";
        }
        else
        {
            warning = podcast
                ? $"Capped at {max} episodes; {excess} older episode(s) left off."
                : $"Playlist trimmed to {max} tracks; {excess} older episode(s) left off.";
        }

        var dropSet = new HashSet<Episode>(drop, ReferenceEqualityComparer.Instance);
        return (newestFirst.Where(e => !dropSet.Contains(e)).ToList(), warning);
    }
}
