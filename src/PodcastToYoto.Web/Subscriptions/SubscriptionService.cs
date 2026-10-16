using Microsoft.EntityFrameworkCore;
using PodcastToYoto.Feeds;
using PodcastToYoto.Web.Data;
using PodcastToYoto.Web.Sync;
using PodcastToYoto.Yoto;

namespace PodcastToYoto.Web.Subscriptions;

public sealed record PollOutcome(bool Ok, bool NotModified, MergeResult Merge, string? Error, bool YotoPlaylist = false, int SkippedStreams = 0)
{
    public string Describe() => YotoPlaylist
        ? !Ok ? $"Yoto playlist check failed: {Error}"
            : $"{Merge.Added} new, {Merge.Updated} updated, {Merge.MarkedMissing} removed from the Yoto playlist, {Merge.Reappeared} back." +
              (SkippedStreams > 0 ? $" {SkippedStreams} stream track(s) skipped (only uploaded audio can be reused)." : "")
        : !Ok ? $"Feed check failed: {Error}"
        : NotModified ? "Feed unchanged since last check."
        : $"{Merge.Added} new, {Merge.Updated} updated, {Merge.MarkedMissing} no longer in feed, {Merge.Reappeared} back in feed.";
}

/// <summary>
/// Sources within a playlist (podcasts, and Yoto playlists whose tracks are reused): CRUD, ordering, polling, favourites
/// and icons.
/// </summary>
public sealed class SubscriptionService(
    IDbContextFactory<AppDbContext> dbFactory,
    FeedFetcher fetcher,
    YotoApiClient yoto,
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

    /// <summary>
    /// Adds an existing Yoto playlist as a source: reads its tracks, then marks <paramref name="picked"/> (track GUIDs from
    /// <see cref="YotoTrackMerger.GuidFor"/>) as favourites.
    /// </summary>
    public async Task<(int Id, PollOutcome Poll)> AddYotoPlaylistAsync(int playlistId, string cardId, string name,
        IReadOnlyCollection<string> picked, CancellationToken ct = default)
    {
        var (id, poll) = await AddAsync(playlistId, SubscriptionSettings.ForYotoPlaylist(cardId, name), ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var wanted = picked.ToHashSet(StringComparer.Ordinal);
        await db.Episodes.Where(e => e.SubscriptionId == id && wanted.Contains(e.Guid))
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.IsFavourite, true), ct);
        return (id, poll);
    }

    /// <summary>The source Yoto playlist's tracks, for picking (no changes saved).</summary>
    public async Task<(string? Title, IReadOnlyList<YotoCardTrack> Tracks)> PreviewYotoPlaylistAsync(string cardId, CancellationToken ct = default)
    {
        using var doc = await yoto.GetContentAsync(cardId, ct);
        var card = YotoCardReader.Card(doc);
        return (YotoCardReader.Title(card), YotoCardReader.Tracks(card).ToList());
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
    public Task MoveAsync(int id, int delta, CancellationToken ct = default) =>
        MoveCoreAsync(id, from => from + delta, ct);

    /// <summary>Moves the podcast to <paramref name="index"/> in its playlist's order (0 = first).</summary>
    public Task MoveToAsync(int id, int index, CancellationToken ct = default) =>
        MoveCoreAsync(id, _ => index, ct);

    private async Task MoveCoreAsync(int id, Func<int, int> target, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var playlistId = await db.Subscriptions.Where(x => x.Id == id).Select(x => x.PlaylistId).SingleAsync(ct);
        var subs = await db.Subscriptions.Where(x => x.PlaylistId == playlistId).OrderBy(x => x.Position).ThenBy(x => x.Id).ToListAsync(ct);

        var from = subs.FindIndex(x => x.Id == id);
        var to = Math.Clamp(target(from), 0, subs.Count - 1);
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

    /// <summary>A rotating Yoto playlist source picks its next tracks on the next sync (<see cref="TrackRotation.RotateEarly"/>).</summary>
    public async Task RotateEarlyAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.Subscriptions.SingleAsync(x => x.Id == id, ct);
        if (TrackRotation.RotateEarly(s)) await db.SaveChangesAsync(ct);
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

    /// <summary>Conditional GET of the feed (or a read of the source Yoto playlist), then reconcile episodes.</summary>
    public async Task<PollOutcome> PollAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.Subscriptions.Include(x => x.Episodes).SingleAsync(x => x.Id == id, ct);
        var now = time.GetUtcNow();
        if (s.IsYotoPlaylist) return await PollYotoPlaylistAsync(db, s, now, ct);

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

    /// <summary>Reads the source Yoto playlist and reconciles its tracks, pointing new ones at their existing Yoto audio.</summary>
    private async Task<PollOutcome> PollYotoPlaylistAsync(AppDbContext db, Subscription s, DateTimeOffset now, CancellationToken ct)
    {
        s.LastPolledAt = now;
        IReadOnlyList<YotoCardTrack> tracks;
        try
        {
            using var doc = await yoto.GetContentAsync(s.SourceCardId ?? throw new InvalidOperationException("No Yoto playlist set."), ct);
            var card = YotoCardReader.Card(doc);
            tracks = YotoCardReader.Tracks(card).ToList();
            s.CoverSourceUrl = YotoCardReader.CoverUrl(card) ?? s.CoverSourceUrl;
        }
        catch (Exception ex) when (ex is YotoApiException or HttpRequestException or InvalidOperationException
                                   || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            // Includes the source playlist being deleted (Yoto answers 404): its tracks stay as they are, still playable.
            var message = ex is YotoApiException { StatusCode: 404 } ? "Yoto playlist not found (deleted?)." : ex.Message;
            logger.LogWarning(ex, "Reading Yoto playlist {CardId} for source {Id} failed", s.SourceCardId, s.Id);
            s.LastError = $"Feed: {message}";
            await db.SaveChangesAsync(ct);
            return new PollOutcome(false, false, MergeResult.Empty, message, YotoPlaylist: true);
        }

        if (s.LastError?.StartsWith("Feed:", StringComparison.Ordinal) == true) s.LastError = null;

        var (merge, streams) = YotoTrackMerger.Merge(s, tracks, now);

        // Every track reuses the audio already on Yoto, so the sync never downloads or uploads it.
        var bySha = tracks.Where(t => t.Sha is not null).GroupBy(t => t.Sha!).ToDictionary(g => g.Key, g => g.First());
        foreach (var e in s.Episodes.Where(e => e.AudioId is null && e.Audio is null))
        {
            if (!bySha.TryGetValue(e.Guid[YotoTrackMerger.GuidPrefix.Length..], out var t)) continue;
            e.Audio = await YotoAudioRefs.FindOrAddAsync(db, t.Sha!, t.Duration, t.FileSize, t.ChannelsJson, t.FormatJson, now, ct);
        }

        await db.SaveChangesAsync(ct);
        if (merge.Added > 0) logger.LogInformation("Source {Id}: {Added} new track(s) in Yoto playlist {CardId}", s.Id, merge.Added, s.SourceCardId);
        return new PollOutcome(true, false, merge, null, YotoPlaylist: true, SkippedStreams: streams);
    }
}
