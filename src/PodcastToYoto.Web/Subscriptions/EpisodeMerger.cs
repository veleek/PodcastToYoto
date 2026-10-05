using PodcastToYoto.Feeds;
using PodcastToYoto.Web.Data;

namespace PodcastToYoto.Web.Subscriptions;

public sealed record MergeResult(int Added, int Updated, int MarkedMissing, int Reappeared)
{
    public static readonly MergeResult Empty = new(0, 0, 0, 0);
}

/// <summary>
/// Reconciles a subscription's stored episodes with the current feed. Episodes are never deleted:
/// ones that drop out of the feed get <see cref="Episode.MissingFromFeedSince"/> so favourites keep working.
/// </summary>
public static class EpisodeMerger
{
    public static MergeResult Merge(Subscription subscription, IEnumerable<PodcastEpisode> feedEpisodes, DateTimeOffset now)
    {
        var existing = subscription.Episodes.ToDictionary(e => e.Guid, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int added = 0, updated = 0, missing = 0, reappeared = 0;

        foreach (var fe in feedEpisodes)
        {
            if (!seen.Add(fe.Guid)) continue; // duplicate GUID within the feed: first wins

            var duration = fe.Duration is { } d ? (int?)Math.Round(d.TotalSeconds) : null;

            if (existing.TryGetValue(fe.Guid, out var e))
            {
                var changed =
                    e.Title != fe.Title ||
                    e.PublishedAt != fe.Published ||
                    e.EnclosureUrl != fe.EnclosureUrl ||
                    e.EnclosureLength != fe.EnclosureLength ||
                    e.DurationSeconds != duration ||
                    e.EpisodeType != fe.EpisodeType ||
                    e.ImageUrl != fe.ImageUrl;

                if (changed)
                {
                    e.Title = fe.Title;
                    e.PublishedAt = fe.Published;
                    // Note: an enclosure URL change does not invalidate the uploaded audio (dynamic ad URLs churn).
                    e.EnclosureUrl = fe.EnclosureUrl;
                    e.EnclosureLength = fe.EnclosureLength;
                    e.DurationSeconds = duration;
                    e.EpisodeType = fe.EpisodeType;
                    e.ImageUrl = fe.ImageUrl;
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
                subscription.Episodes.Add(new Episode
                {
                    Guid = fe.Guid,
                    Title = fe.Title,
                    PublishedAt = fe.Published,
                    EnclosureUrl = fe.EnclosureUrl,
                    EnclosureLength = fe.EnclosureLength,
                    DurationSeconds = duration,
                    EpisodeType = fe.EpisodeType,
                    ImageUrl = fe.ImageUrl,
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

        return new MergeResult(added, updated, missing, reappeared);
    }
}
