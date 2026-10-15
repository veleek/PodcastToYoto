using PodcastToYoto.Web.Data;

namespace PodcastToYoto.Web.Sync;

public sealed record PlannedPlaylist(IReadOnlyList<Episode> Episodes, IReadOnlyList<string> Warnings)
{
    /// <summary>This week's rotation of each rotating Yoto playlist source; a sync records the new ones.</summary>
    public IReadOnlyList<TrackRotation.Week> Rotations { get; init; } = [];
}

/// <summary>Decides which episodes go on the card, and in what order. Pure; no I/O.</summary>
public static class PlaylistPlanner
{
    /// <summary>
    /// A whole playlist: each included podcast picks its own episodes (newest N ∪ favourites, capped at its max), and each
    /// Yoto playlist source its first N (or this week's rotation of N) ∪ favourite tracks; then the combined list is capped at the playlist's max and ordered
    /// by release date (optionally grouped by podcast), by title, or interleaved one track per source in turn.
    /// Uses each subscription's <see cref="Subscription.Episodes"/>.
    /// </summary>
    public static PlannedPlaylist Plan(Playlist playlist, IEnumerable<Subscription> subscriptions, DateTimeOffset? now = null)
    {
        var warnings = new List<string>();
        var rotations = new List<TrackRotation.Week>();
        var podcasts = subscriptions.Where(s => s.Enabled).OrderBy(s => s.Position).ThenBy(s => s.Id).ToList();
        var multi = podcasts.Count > 1;

        // Each podcast's picks, newest first.
        var groups = new List<(Subscription Sub, List<Episode> Episodes)>();
        foreach (var s in podcasts)
        {
            var (picked, w) = s.IsYotoPlaylist ? SelectTracks(s, s.Episodes, now, rotations) : Select(s, s.Episodes);
            groups.Add((s, picked));
            warnings.AddRange(multi ? w.Select(x => $"{s.Name}: {x}") : w);
        }

        // Stable sort, so a single podcast keeps exactly its own order.
        // Undated Yoto playlist tracks sort last, in their source's order, so the playlist cap drops their last tracks first.
        var rank = new Dictionary<Episode, int>(ReferenceEqualityComparer.Instance);
        foreach (var g in groups.Where(g => g.Sub.IsYotoPlaylist))
            for (var i = 0; i < g.Episodes.Count; i++) rank[g.Episodes[i]] = i;
        var newestFirst = groups.SelectMany(g => g.Episodes).OrderByDescending(e => e.PublishedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(e => e.FirstSeenAt).ThenBy(e => rank.GetValueOrDefault(e)).ToList();

        var max = Math.Max(1, playlist.MaxTracks);
        if (newestFirst.Count > max)
        {
            var (kept, w) = Trim(newestFirst, max, podcast: false);
            warnings.Add(w);
            var keep = new HashSet<Episode>(kept, ReferenceEqualityComparer.Instance);
            foreach (var g in groups) g.Episodes.RemoveAll(e => !keep.Contains(e));
            newestFirst = kept;
        }

        // A source's picks in its own order; podcasts without one use `fallback`.
        IEnumerable<Episode> OwnOrder((Subscription Sub, List<Episode> Episodes) g, PlaylistOrder fallback) =>
            g.Sub.IsYotoPlaylist ? g.Episodes : Ordered(g.Episodes, g.Sub.UseOwnOrder ? g.Sub.Order : fallback);

        IReadOnlyList<Episode> ordered;
        if (playlist.Order == PlaylistOrder.Interleaved)
        {
            // One track from each source in turn, in source order; sources that run out drop out of the rotation.
            var queues = groups.Select(g => OwnOrder(g, PlaylistOrder.NewestFirst).ToList()).ToList();
            var list = new List<Episode>();
            for (var i = 0; i < queues.Select(q => q.Count).DefaultIfEmpty().Max(); i++)
                list.AddRange(queues.Where(q => i < q.Count).Select(q => q[i]));
            ordered = list;
        }
        else if (playlist.GroupByPodcast)
        {
            ordered = groups.SelectMany(g => OwnOrder(g, playlist.Order)).ToList();
        }
        else if (playlist.Order is PlaylistOrder.TitleAscending or PlaylistOrder.TitleDescending || groups.All(g => !g.Sub.IsYotoPlaylist))
        {
            ordered = Ordered(newestFirst, playlist.Order).ToList();
        }
        else
        {
            // Yoto playlist tracks have no release date: each Yoto source stays together, in its own order, at its place in
            // the source order. The podcasts' episodes, interleaved by date, go where the first podcast is.
            var undated = new HashSet<Episode>(groups.Where(g => g.Sub.IsYotoPlaylist).SelectMany(g => g.Episodes), ReferenceEqualityComparer.Instance);
            var list = new List<Episode>();
            var dated = false;
            foreach (var g in groups)
            {
                if (g.Sub.IsYotoPlaylist) list.AddRange(g.Episodes);
                else if (!dated)
                {
                    dated = true;
                    list.AddRange(Ordered(newestFirst.Where(e => !undated.Contains(e)).ToList(), playlist.Order));
                }
            }
            ordered = list;
        }

        if (podcasts.Count == 0) warnings.Add("Nothing is included in this playlist.");
        return new PlannedPlaylist(ordered, warnings) { Rotations = rotations };
    }

    /// <summary>One podcast on its own, ordered by its <see cref="Subscription.Order"/>.</summary>
    public static PlannedPlaylist Plan(Subscription s, IEnumerable<Episode> episodes, DateTimeOffset? now = null)
    {
        if (s.IsYotoPlaylist)
        {
            var rotations = new List<TrackRotation.Week>();
            var (tracks, w) = SelectTracks(s, episodes, now, rotations);
            return new PlannedPlaylist(tracks, w) { Rotations = rotations };
        }
        var (newestFirst, warnings) = Select(s, episodes);
        return new PlannedPlaylist(Ordered(newestFirst, s.Order).ToList(), warnings);
    }

    /// <summary>
    /// A Yoto playlist source's tracks in its <see cref="Subscription.Order"/>. They have no dates, so newest first means
    /// the source playlist's track order and oldest first its reverse.
    /// </summary>
    public static IEnumerable<Episode> InSourceOrder(Subscription s, IEnumerable<Episode> tracks)
    {
        var byIndex = tracks.OrderBy(e => e.SourceIndex ?? int.MaxValue).ThenBy(e => e.Id).ToList();
        return s.Order switch
        {
            PlaylistOrder.OldestFirst => byIndex.AsEnumerable().Reverse(),
            PlaylistOrder.NewestFirst => byIndex,
            var o => Ordered(byIndex, o),
        };
    }

    /// <summary>
    /// A Yoto playlist source: the first <see cref="Subscription.RecentCount"/> tracks still in the source (in its order), or
    /// this week's rotation of that many (<see cref="TrackRotation"/>, added to <paramref name="rotations"/>), ∪ favourites, capped at <see cref="Subscription.MaxTracks"/> by dropping the last non-favourites, then the last
    /// favourites; in its order.
    /// </summary>
    private static (List<Episode> Tracks, List<string> Warnings) SelectTracks(
        Subscription s, IEnumerable<Episode> episodes, DateTimeOffset? now, List<TrackRotation.Week> rotations)
    {
        var warnings = new List<string>();
        var ordered = InSourceOrder(s, episodes).ToList();
        IEnumerable<Episode> top;
        if (s.Selection == TrackSelection.RotateWeekly)
        {
            var week = TrackRotation.Current(s, ordered, now ?? DateTimeOffset.UtcNow);
            rotations.Add(week);
            top = week.Tracks;
        }
        else top = ordered.Where(e => e.MissingFromFeedSince is null).Take(Math.Max(0, s.RecentCount));
        var selected = new HashSet<Episode>(top, ReferenceEqualityComparer.Instance);
        foreach (var f in ordered.Where(e => e.IsFavourite)) selected.Add(f);

        var tracks = ordered.Where(selected.Contains).ToList();
        var max = Math.Max(1, s.MaxTracks);
        if (tracks.Count > max)
        {
            var excess = tracks.Count - max;
            var drop = tracks.AsEnumerable().Reverse().Where(e => !e.IsFavourite).Take(excess).ToList();
            if (drop.Count < excess) drop.AddRange(tracks.AsEnumerable().Reverse().Where(e => e.IsFavourite).Take(excess - drop.Count));
            var dropSet = new HashSet<Episode>(drop, ReferenceEqualityComparer.Instance);
            tracks = tracks.Where(e => !dropSet.Contains(e)).ToList();
            warnings.Add($"Capped at {max} tracks; {excess} left off.");
        }
        return (tracks, warnings);
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
