using Microsoft.EntityFrameworkCore;
using PodcastToYoto.Feeds;
using PodcastToYoto.Web.Data;

namespace PodcastToYoto.Web.Subscriptions;

public sealed record PollOutcome(bool Ok, bool NotModified, MergeResult Merge, string? Error)
{
    public string Describe() => !Ok ? $"Feed check failed: {Error}"
        : NotModified ? "Feed unchanged since last check."
        : $"{Merge.Added} new, {Merge.Updated} updated, {Merge.MarkedMissing} no longer in feed, {Merge.Reappeared} back in feed.";
}

/// <summary>Podcast subscriptions within a playlist: CRUD, ordering, feed polling, favourites and icons.</summary>
public sealed class SubscriptionService(
    IDbContextFactory<AppDbContext> dbFactory,
    FeedFetcher fetcher,
    TimeProvider time,
    ILogger<SubscriptionService> logger)
{
    /// <summary>The playlist a subscription belongs to (null if it doesn't exist).</summary>
    public async Task<int?> GetPlaylistIdAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Subscriptions.Where(s => s.Id == id).Select(s => (int?)s.PlaylistId).SingleOrDefaultAsync(ct);
    }

    public async Task<PodcastFeed> PreviewAsync(string feedUrl, CancellationToken ct = default)
    {
        var result = await fetcher.FetchAsync(feedUrl.Trim(), ct: ct);
        return result.Feed ?? throw new InvalidOperationException("Feed returned no content.");
    }

    /// <summary>Adds a podcast to the end of a playlist and immediately polls the feed to populate episodes.</summary>
    public async Task<(int Id, PollOutcome Poll)> AddAsync(int playlistId, SubscriptionSettings settings, CancellationToken ct = default)
    {
        int id;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var position = await db.Subscriptions.Where(x => x.PlaylistId == playlistId).Select(x => (int?)x.Position).MaxAsync(ct) ?? -1;
            var s = new Subscription
            {
                PlaylistId = playlistId,
                Position = position + 1,
                Name = settings.Name,
                FeedUrl = settings.FeedUrl,
                CreatedAt = time.GetUtcNow(),
            };
            settings.ApplyTo(s);
            db.Subscriptions.Add(s);
            await db.SaveChangesAsync(ct);
            id = s.Id;
        }

        logger.LogInformation("Podcast {Id} '{Name}' added to playlist {PlaylistId} from {FeedUrl}", id, settings.Name, playlistId, settings.FeedUrl);
        return (id, await PollAsync(id, ct));
    }

    public async Task UpdateSettingsAsync(int id, SubscriptionSettings settings, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.Subscriptions.SingleAsync(x => x.Id == id, ct);
        var feedChanged = !string.Equals(s.FeedUrl, settings.FeedUrl.Trim(), StringComparison.Ordinal);

        settings.ApplyTo(s);
        if (feedChanged)
        {
            // Different feed: drop conditional-GET validators so the next poll fetches fully.
            s.FeedETag = null;
            s.FeedLastModified = null;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Removes a podcast (and its episode history) from its playlist.</summary>
    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Subscriptions.Where(x => x.Id == id).ExecuteDeleteAsync(ct);
        logger.LogInformation("Podcast {Id} removed", id);
    }

    /// <summary>Moves a podcast up (negative) or down (positive) in its playlist's order.</summary>
    public async Task MoveAsync(int id, int delta, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var playlistId = await db.Subscriptions.Where(x => x.Id == id).Select(x => x.PlaylistId).SingleAsync(ct);
        var subs = await db.Subscriptions.Where(x => x.PlaylistId == playlistId).OrderBy(x => x.Position).ThenBy(x => x.Id).ToListAsync(ct);

        var from = subs.FindIndex(x => x.Id == id);
        var to = Math.Clamp(from + delta, 0, subs.Count - 1);
        if (from == to) return;

        var s = subs[from];
        subs.RemoveAt(from);
        subs.Insert(to, s);
        for (var i = 0; i < subs.Count; i++) subs[i].Position = i;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Use a custom icon (already uploaded to Yoto) as this podcast's logo icon; null clears it.</summary>
    public async Task SetLogoIconAsync(int id, string? mediaId, string? url, LogoIconMode mode, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Subscriptions.Where(x => x.Id == id).ExecuteUpdateAsync(u => u
            .SetProperty(x => x.LogoIconMediaId, mediaId)
            .SetProperty(x => x.LogoIconUrl, url)
            .SetProperty(x => x.LogoIcon, mediaId is null ? LogoIconMode.Off : mode), ct);
    }

    public async Task<int> ClearFavouritesAsync(int subscriptionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Episodes.Where(e => e.SubscriptionId == subscriptionId && e.IsFavourite)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.IsFavourite, false), ct);
    }

    /// <summary>Pin an icon for an episode (Yoto media id), or null to go back to automatic matching.</summary>
    public async Task SetIconAsync(int episodeId, string? mediaId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Episodes.Where(e => e.Id == episodeId)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.IconMediaId, mediaId), ct);
    }

    public async Task SetFavouriteAsync(int episodeId, bool favourite, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Episodes.Where(e => e.Id == episodeId)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.IsFavourite, favourite), ct);
    }

    /// <summary>Conditional GET of the feed, then reconcile episodes.</summary>
    public async Task<PollOutcome> PollAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.Subscriptions.Include(x => x.Episodes).SingleAsync(x => x.Id == id, ct);
        var now = time.GetUtcNow();

        FeedFetchResult result;
        try
        {
            result = await fetcher.FetchAsync(s.FeedUrl, s.FeedETag, s.FeedLastModified, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or FormatException or System.Xml.XmlException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Feed poll failed for podcast {Id} ({FeedUrl})", id, s.FeedUrl);
            s.LastPolledAt = now;
            s.LastError = $"Feed: {ex.Message}";
            await db.SaveChangesAsync(ct);
            return new PollOutcome(false, false, MergeResult.Empty, ex.Message);
        }

        s.LastPolledAt = now;
        if (s.LastError?.StartsWith("Feed:", StringComparison.Ordinal) == true) s.LastError = null;

        if (result.NotModified)
        {
            await db.SaveChangesAsync(ct);
            return new PollOutcome(true, true, MergeResult.Empty, null);
        }

        var feed = result.Feed!;
        s.FeedETag = result.ETag;
        s.FeedLastModified = result.LastModified;

        // The playlist re-uploads its cover when this changes.
        s.CoverSourceUrl = feed.ImageUrl;

        var merge = EpisodeMerger.Merge(s, feed.Episodes, now);
        await db.SaveChangesAsync(ct);

        if (merge.Added > 0)
            logger.LogInformation("Podcast {Id}: {Added} new episode(s) in '{Feed}'", id, merge.Added, feed.Title);

        return new PollOutcome(true, false, merge, null);
    }
}
