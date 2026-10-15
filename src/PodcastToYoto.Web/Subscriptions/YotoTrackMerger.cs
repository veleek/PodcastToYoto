using PodcastToYoto.Web.Data;
using PodcastToYoto.Yoto;

namespace PodcastToYoto.Web.Subscriptions;

/// <summary>
/// Reconciles a Yoto playlist source's stored tracks with the source playlist's current tracks. Tracks are identified by
/// their audio (<c>yoto:{sha}</c>), so renames and reorders carry over. Like podcast episodes, tracks are never deleted:
/// ones removed from the source get <see cref="Episode.MissingFromFeedSince"/>, and picked ones stay on the playlist.
/// Stream tracks have no uploaded audio to reuse and are skipped.
/// </summary>
public static class YotoTrackMerger
{
    public const string GuidPrefix = "yoto:";

    public static string GuidFor(string sha) => GuidPrefix + sha;

    public static (MergeResult Merge, int SkippedStreams) Merge(Subscription source, IEnumerable<YotoCardTrack> tracks, DateTimeOffset now)
    {
        var existing = source.Episodes.ToDictionary(e => e.Guid, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int added = 0, updated = 0, missing = 0, reappeared = 0, streams = 0, index = 0;

        foreach (var t in tracks)
        {
            if (t.Sha is null)
            {
                streams++;
                continue;
            }

            var guid = GuidFor(t.Sha);
            if (!seen.Add(guid)) continue; // same audio twice in the source: first wins

            var title = string.IsNullOrWhiteSpace(t.Title) ? $"Track {t.Index + 1}" : t.Title;
            var duration = t.Duration is { } d ? (int?)d : null;
            var position = index++;

            if (existing.TryGetValue(guid, out var e))
            {
                if (e.Title != title || e.SourceIndex != position || e.SourceIconRef != t.IconRef || e.DurationSeconds != duration)
                {
                    e.Title = title;
                    e.SourceIndex = position;
                    e.SourceIconRef = t.IconRef;
                    e.DurationSeconds = duration;
                    updated++;
                }

                if (e.MissingFromFeedSince is not null)
                {
                    e.MissingFromFeedSince = null;
                    reappeared++;
                }
            }
            else
            {
                source.Episodes.Add(new Episode
                {
                    Guid = guid,
                    Title = title,
                    EnclosureUrl = t.TrackUrl!,
                    EnclosureLength = t.FileSize,
                    DurationSeconds = duration,
                    SourceIndex = position,
                    SourceIconRef = t.IconRef,
                    FirstSeenAt = now,
                });
                added++;
            }
        }

        foreach (var e in existing.Values)
        {
            if (!seen.Contains(e.Guid) && e.MissingFromFeedSince is null)
            {
                e.MissingFromFeedSince = now;
                missing++;
            }
        }

        return (new MergeResult(added, updated, missing, reappeared), streams);
    }
}
