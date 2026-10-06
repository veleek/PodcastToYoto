using Microsoft.EntityFrameworkCore;
using PodcastToYoto.Feeds;
using PodcastToYoto.Web.Data;

namespace PodcastToYoto.Web.Subscriptions;

public sealed record SubscriptionSummary(
    int Id,
    string Name,
    string FeedUrl,
    bool Enabled,
    string? YotoCardId,
    string? CoverSourceUrl,
    int EpisodeCount,
    int FavouriteCount,
    DateTimeOffset? LastPolledAt,
    DateTimeOffset? LastSyncedAt,
    SyncResult? LastSyncResult,
    string? LastError,
    string? TitleOverride)
{
    /// <summary>Title the Yoto playlist is published with.</summary>
    public string PlaylistTitle => TitleOverride ?? Name;
}

public sealed record PollOutcome(bool Ok, bool NotModified, MergeResult Merge, string? Error)
{
    public string Describe() => !Ok ? $"Feed check failed: {Error}"
        : NotModified ? "Feed unchanged since last check."
        : $"{Merge.Added} new, {Merge.Updated} updated, {Merge.MarkedMissing} no longer in feed, {Merge.Reappeared} back in feed.";
}

/// <summary>Subscription CRUD, feed polling and favourites.</summary>
public sealed class SubscriptionService(
    IDbContextFactory<AppDbContext> dbFactory,
    FeedFetcher fetcher,
    TimeProvider time,
    ILogger<SubscriptionService> logger)
{
    public async Task<IReadOnlyList<SubscriptionSummary>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Subscriptions
            .AsNoTracking()
            .OrderBy(s => s.Name)
            .Select(s => new SubscriptionSummary(
                s.Id, s.Name, s.FeedUrl, s.Enabled, s.YotoCardId, s.CoverSourceUrl,
                s.Episodes.Count, s.Episodes.Count(e => e.IsFavourite),
                s.LastPolledAt, s.LastSyncedAt, s.LastSyncResult, s.LastError, s.TitleOverride))
            .ToListAsync(ct);
    }

    /// <summary>Subscription with all episodes (newest first). Detached snapshot for display.</summary>
    public async Task<Subscription?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.Subscriptions
            .AsNoTracking()
            .Include(x => x.Episodes)
            .SingleOrDefaultAsync(x => x.Id == id, ct);

        s?.Episodes.Sort((a, b) => Nullable.Compare(b.PublishedAt, a.PublishedAt));
        return s;
    }

    public async Task<PodcastFeed> PreviewAsync(string feedUrl, CancellationToken ct = default)
    {
        var result = await fetcher.FetchAsync(feedUrl.Trim(), ct: ct);
        return result.Feed ?? throw new InvalidOperationException("Feed returned no content.");
    }

    /// <summary>Creates the subscription and immediately polls the feed to populate episodes.</summary>
    public async Task<(int Id, PollOutcome Poll)> CreateAsync(SubscriptionSettings settings, CancellationToken ct = default)
    {
        int id;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var s = new Subscription { Name = settings.Name, FeedUrl = settings.FeedUrl, CreatedAt = time.GetUtcNow() };
            settings.ApplyTo(s);
            db.Subscriptions.Add(s);
            await db.SaveChangesAsync(ct);
            id = s.Id;
        }

        logger.LogInformation("Subscription {Id} '{Name}' created for {FeedUrl}", id, settings.Name, settings.FeedUrl);
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

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Subscriptions.Where(x => x.Id == id).ExecuteDeleteAsync(ct);
        logger.LogInformation("Subscription {Id} deleted", id);
    }

    /// <summary>Set the target playlist id without touching its contents (used by import).</summary>
    public async Task SetPlaylistAsync(int id, string cardId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Subscriptions.Where(x => x.Id == id).ExecuteUpdateAsync(u => u
            .SetProperty(x => x.YotoCardId, cardId)
            .SetProperty(x => x.LastPublishedHash, (string?)null), ct);
    }

    /// <summary>Forget the target playlist so the next sync creates a new one (e.g. it was deleted in the Yoto app).</summary>
    public async Task ClearPlaylistAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Subscriptions.Where(x => x.Id == id).ExecuteUpdateAsync(u => u
            .SetProperty(x => x.YotoCardId, (string?)null)
            .SetProperty(x => x.LastPublishedHash, (string?)null)
            .SetProperty(x => x.CoverMediaUrl, (string?)null), ct);
    }

    public async Task<IReadOnlyList<SyncRun>> GetRecentRunsAsync(int id, int count = 10, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SyncRuns.AsNoTracking()
            .Where(r => r.SubscriptionId == id)
            .OrderByDescending(r => r.StartedAt)
            .Take(count)
            .ToListAsync(ct);
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
            logger.LogWarning(ex, "Feed poll failed for subscription {Id} ({FeedUrl})", id, s.FeedUrl);
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

        if (!string.Equals(s.CoverSourceUrl, feed.ImageUrl, StringComparison.Ordinal))
        {
            s.CoverSourceUrl = feed.ImageUrl;
            s.CoverMediaUrl = null; // re-upload on next sync
        }

        var merge = EpisodeMerger.Merge(s, feed.Episodes, now);
        await db.SaveChangesAsync(ct);

        if (merge.Added > 0)
            logger.LogInformation("Subscription {Id}: {Added} new episode(s) in '{Feed}'", id, merge.Added, feed.Title);

        return new PollOutcome(true, false, merge, null);
    }
}
