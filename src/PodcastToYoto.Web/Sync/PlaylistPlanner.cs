using PodcastToYoto.Web.Data;

namespace PodcastToYoto.Web.Sync;

public sealed record PlannedPlaylist(IReadOnlyList<Episode> Episodes, IReadOnlyList<string> Warnings);

/// <summary>Decides which episodes go on the card, and in what order. Pure; no I/O.</summary>
public static class PlaylistPlanner
{
    public static PlannedPlaylist Plan(Subscription s, IEnumerable<Episode> episodes)
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

        // `candidates` is newest-first, so walking it backwards visits oldest first.
        var newestFirst = candidates.Where(selected.Contains).ToList();
        var max = Math.Max(1, s.MaxTracks);
        if (newestFirst.Count > max)
        {
            var excess = newestFirst.Count - max;
            var drop = newestFirst.AsEnumerable().Reverse().Where(e => !e.IsFavourite).Take(excess).ToList();
            if (drop.Count < excess)
            {
                drop.AddRange(newestFirst.AsEnumerable().Reverse().Where(e => e.IsFavourite).Take(excess - drop.Count));
                warnings.Add($"More favourites than the {max}-track limit; the oldest favourites were left off.");
            }
            else
            {
                warnings.Add($"Playlist trimmed to {max} tracks; {excess} older episode(s) left off.");
            }

            var dropSet = new HashSet<Episode>(drop, ReferenceEqualityComparer.Instance);
            newestFirst = newestFirst.Where(e => !dropSet.Contains(e)).ToList();
        }

        IReadOnlyList<Episode> ordered = s.Order == PlaylistOrder.OldestFirst
            ? newestFirst.AsEnumerable().Reverse().ToList()
            : newestFirst;

        return new PlannedPlaylist(ordered, warnings);
    }
}
