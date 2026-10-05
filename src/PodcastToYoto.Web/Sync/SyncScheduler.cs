using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PodcastToYoto.Web.Data;
using PodcastToYoto.Web.Options;

namespace PodcastToYoto.Web.Sync;

/// <param name="At">Approximate time of the next automatic sync (scheduler checks every minute); null when paused.</param>
/// <param name="PausedReason">Why no automatic sync is scheduled, if applicable.</param>
public sealed record NextSync(DateTimeOffset? At, TimeSpan Interval, string? PausedReason);

/// <summary>
/// Runs syncs: on startup (optional), whenever a subscription's poll interval has elapsed, and on demand ("Sync now").
/// Syncs are serialized by <see cref="SyncEngine"/>.
/// </summary>
public sealed class SyncScheduler(
    IDbContextFactory<AppDbContext> dbFactory,
    SyncEngine engine,
    SyncActivity activity,
    IOptionsMonitor<SyncOptions> options,
    TimeProvider time,
    ILogger<SyncScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    private readonly Channel<int> _requests = Channel.CreateUnbounded<int>();

    /// <summary>Queue a manual sync. Returns false if one is already queued or running for this subscription.</summary>
    public bool RequestSync(int subscriptionId)
    {
        if (activity.IsBusy(subscriptionId)) return false;
        activity.Set(subscriptionId, "Queued…");
        return _requests.Writer.TryWrite(subscriptionId);
    }

    /// <summary>When the scheduler will next sync this subscription (same rule as <see cref="GetDueAsync"/>).</summary>
    public async Task<NextSync> GetNextSyncAsync(int subscriptionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var defaultInterval = options.CurrentValue.DefaultPollInterval;

        var s = await db.Subscriptions.AsNoTracking()
            .Where(x => x.Id == subscriptionId)
            .Select(x => new { x.Enabled, x.PollInterval })
            .SingleOrDefaultAsync(ct);
        if (s is null) return new NextSync(null, defaultInterval, "Not found");

        var interval = s.PollInterval ?? defaultInterval;
        if (!s.Enabled) return new NextSync(null, interval, "Disabled: manual sync only");

        var account = await db.YotoAccounts.AsNoTracking().SingleOrDefaultAsync(ct);
        if (account?.Status != AccountStatus.Connected) return new NextSync(null, interval, "Paused until Yoto is connected");

        var starts = await db.SyncRuns.AsNoTracking()
            .Where(r => r.SubscriptionId == subscriptionId && r.Result != SyncResult.DryRun)
            .Select(r => r.StartedAt)
            .ToListAsync(ct);

        var at = starts.Count == 0 ? time.GetUtcNow() : starts.Max() + interval;
        return new NextSync(at, interval, null);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the app finish starting before doing network work.
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        await MarkInterruptedRunsAsync(stoppingToken);

        var startup = options.CurrentValue.RunOnStartup;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (_requests.Reader.TryRead(out var id))
                    await engine.SyncAsync(id, SyncTrigger.Manual, ct: stoppingToken);

                foreach (var id in await GetDueAsync(startup, stoppingToken))
                {
                    if (_requests.Reader.TryPeek(out _)) break; // manual requests jump the queue
                    await engine.SyncAsync(id, startup ? SyncTrigger.Startup : SyncTrigger.Schedule, ct: stoppingToken);
                }

                startup = false;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduler loop error");
            }

            await WaitForWorkAsync(stoppingToken);
        }
    }

    private async Task<List<int>> GetDueAsync(bool all, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var account = await db.YotoAccounts.AsNoTracking().SingleOrDefaultAsync(ct);
        if (account?.Status != AccountStatus.Connected)
        {
            logger.LogDebug("Yoto not connected; scheduled syncs paused");
            return [];
        }

        var subs = await db.Subscriptions.AsNoTracking()
            .Where(s => s.Enabled)
            .Select(s => new { s.Id, s.PollInterval })
            .ToListAsync(ct);

        // Small table (pruned to ~200 runs per subscription); aggregate in memory to keep the SQL trivial.
        var lastRuns = (await db.SyncRuns.AsNoTracking()
                .Where(r => r.Result != SyncResult.DryRun)
                .Select(r => new { r.SubscriptionId, r.StartedAt })
                .ToListAsync(ct))
            .GroupBy(r => r.SubscriptionId)
            .ToDictionary(g => g.Key, g => g.Max(r => r.StartedAt));

        var now = time.GetUtcNow();
        var defaultInterval = options.CurrentValue.DefaultPollInterval;
        return subs
            .Where(s => all || !lastRuns.TryGetValue(s.Id, out var last) || last + (s.PollInterval ?? defaultInterval) <= now)
            .Where(s => !activity.IsBusy(s.Id))
            .Select(s => s.Id)
            .ToList();
    }

    /// <summary>Runs left "Running" by a crash or restart are marked failed.</summary>
    private async Task MarkInterruptedRunsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var n = await db.SyncRuns.Where(r => r.Result == SyncResult.Running)
            .ExecuteUpdateAsync(u => u
                .SetProperty(r => r.Result, SyncResult.Failed)
                .SetProperty(r => r.Message, "Interrupted (app restarted)."), ct);
        if (n > 0) logger.LogWarning("Marked {Count} interrupted sync run(s) as failed", n);
    }

    /// <summary>Sleep until the next tick or until a manual request arrives.</summary>
    private async Task WaitForWorkAsync(CancellationToken stoppingToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var delay = Task.Delay(Tick, cts.Token);
        var request = _requests.Reader.WaitToReadAsync(cts.Token).AsTask();
        await Task.WhenAny(delay, request);
        cts.Cancel();
        try { await Task.WhenAll(delay, request); } catch (OperationCanceledException) { }
    }
}
