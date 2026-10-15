using Microsoft.EntityFrameworkCore;
using PodcastToYoto.Web.Data;

namespace PodcastToYoto.Web.Subscriptions;

public sealed record PodcastSummary(
    int Id,
    string Name,
    string FeedUrl,
    bool Enabled,
    string? CoverSourceUrl,
    int EpisodeCount,
    int FavouriteCount,
    string? LastError);

public sealed record PlaylistSummary(
    int Id,
    string Name,
    bool Enabled,
    string? YotoCardId,
    string? CoverUrl,
    IReadOnlyList<PodcastSummary> Podcasts,
    DateTimeOffset? LastSyncedAt,
    SyncResult? LastSyncResult,
    string? LastError)
{
    public int EpisodeCount => Podcasts.Sum(p => p.EpisodeCount);
    public int FavouriteCount => Podcasts.Sum(p => p.FavouriteCount);

    /// <summary>A sync error, else the first feed error.</summary>
    public string? AnyError => LastError ?? Podcasts.Select(p => p.LastError).FirstOrDefault(e => e is not null);
}

/// <summary>Playlist CRUD, target Yoto playlist and sync history.</summary>
public sealed class PlaylistService(
    IDbContextFactory<AppDbContext> dbFactory,
    SubscriptionService subscriptions,
    TimeProvider time,
    ILogger<PlaylistService> logger)
{
    public async Task<IReadOnlyList<PlaylistSummary>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var playlists = await db.Playlists.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);
        var podcasts = (await db.Subscriptions.AsNoTracking()
                .OrderBy(s => s.Position).ThenBy(s => s.Id)
                .Select(s => new
                {
                    s.PlaylistId,
                    Summary = new PodcastSummary(s.Id, s.Name, s.FeedUrl, s.Enabled, s.CoverSourceUrl,
                        s.Episodes.Count, s.Episodes.Count(e => e.IsFavourite), s.LastError),
                })
                .ToListAsync(ct))
            .ToLookup(x => x.PlaylistId, x => x.Summary);

        return playlists.Select(p =>
        {
            var pods = podcasts[p.Id].ToList();
            var cover = p.CoverImageUrl ?? pods.Where(x => x.Enabled).Select(x => x.CoverSourceUrl).FirstOrDefault(c => c is not null);
            return new PlaylistSummary(p.Id, p.Name, p.Enabled, p.YotoCardId, cover, pods, p.LastSyncedAt, p.LastSyncResult, p.LastError);
        }).ToList();
    }

    /// <summary>Playlist with its podcasts (in order) and all their episodes (newest first). Detached snapshot for display.</summary>
    public async Task<Playlist?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var p = await db.Playlists
            .AsNoTracking()
            .Include(x => x.Subscriptions).ThenInclude(s => s.Episodes)
            .AsSplitQuery()
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return null;

        p.Subscriptions.Sort((a, b) => a.Position != b.Position ? a.Position.CompareTo(b.Position) : a.Id.CompareTo(b.Id));
        foreach (var s in p.Subscriptions)
        {
            s.Playlist = p;
            if (s.IsYotoPlaylist) s.Episodes.Sort((a, b) => Nullable.Compare(a.SourceIndex, b.SourceIndex) is var c and not 0 ? c : a.Id.CompareTo(b.Id));
            else s.Episodes.Sort((a, b) => Nullable.Compare(b.PublishedAt, a.PublishedAt));
        }
        return p;
    }

    /// <summary>Creates a playlist with its first podcast, and polls that podcast's feed.</summary>
    public async Task<(int PlaylistId, int SubscriptionId, PollOutcome Poll)> CreateAsync(
        PlaylistSettings settings, SubscriptionSettings firstPodcast, CancellationToken ct = default)
    {
        int id;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var p = new Playlist { Name = settings.Name, CreatedAt = time.GetUtcNow() };
            settings.ApplyTo(p);
            db.Playlists.Add(p);
            await db.SaveChangesAsync(ct);
            id = p.Id;
        }

        logger.LogInformation("Playlist {Id} '{Name}' created", id, settings.Name);
        var (subId, poll) = await subscriptions.AddAsync(id, firstPodcast, ct);
        return (id, subId, poll);
    }

    public async Task UpdateSettingsAsync(int id, PlaylistSettings settings, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var p = await db.Playlists.SingleAsync(x => x.Id == id, ct);
        settings.ApplyTo(p);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Deletes the playlist, its podcasts and history here. The Yoto playlist is left untouched.</summary>
    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Playlists.Where(x => x.Id == id).ExecuteDeleteAsync(ct);
        logger.LogInformation("Playlist {Id} deleted", id);
    }

    /// <summary>Set the target Yoto playlist id without touching its contents (used by import).</summary>
    public async Task SetCardAsync(int id, string cardId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Playlists.Where(x => x.Id == id).ExecuteUpdateAsync(u => u
            .SetProperty(x => x.YotoCardId, cardId)
            .SetProperty(x => x.LastPublishedHash, (string?)null), ct);
    }

    /// <summary>Forget the target Yoto playlist so the next sync creates a new one (e.g. it was deleted in the Yoto app).</summary>
    public async Task ClearCardAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Playlists.Where(x => x.Id == id).ExecuteUpdateAsync(u => u
            .SetProperty(x => x.YotoCardId, (string?)null)
            .SetProperty(x => x.LastPublishedHash, (string?)null)
            .SetProperty(x => x.CoverSourceUrl, (string?)null)
            .SetProperty(x => x.CoverMediaUrl, (string?)null), ct);
    }

    public async Task<IReadOnlyList<SyncRun>> GetRecentRunsAsync(int id, int count = 10, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SyncRuns.AsNoTracking()
            .Where(r => r.PlaylistId == id)
            .OrderByDescending(r => r.StartedAt)
            .Take(count)
            .ToListAsync(ct);
    }
}
