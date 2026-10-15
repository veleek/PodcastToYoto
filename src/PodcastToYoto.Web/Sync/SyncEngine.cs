using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PodcastToYoto.Web.Data;
using PodcastToYoto.Web.Icons;
using PodcastToYoto.Web.Options;
using PodcastToYoto.Web.Subscriptions;
using PodcastToYoto.Yoto;

namespace PodcastToYoto.Web.Sync;

public sealed record SyncOutcome(SyncResult Result, string Message, PlannedPlaylist? Plan = null);

/// <summary>
/// One sync of one playlist: poll each podcast → plan → ensure audio → ensure cover → publish (skipped if unchanged).
/// Only one sync runs at a time across the app.
/// </summary>
public sealed class SyncEngine(
    IDbContextFactory<AppDbContext> dbFactory,
    SubscriptionService subscriptions,
    AudioPipeline audio,
    YotoApiClient yoto,
    SyncActivity activity,
    INotificationSink notify,
    IconCatalog iconCatalog,
    IOptionsMonitor<SyncOptions> options,
    TimeProvider time,
    ILogger<SyncEngine> logger)
{
    private const int MaxRunsKept = 200;

    private static readonly JsonSerializerOptions HashJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<SyncOutcome> SyncAsync(int playlistId, SyncTrigger trigger, bool dryRun = false, CancellationToken ct = default)
    {
        dryRun |= options.CurrentValue.DryRun;
        activity.Set(playlistId, "Waiting for another sync…");
        try
        {
            await _gate.WaitAsync(ct);
        }
        catch
        {
            activity.Clear(playlistId);
            throw;
        }

        try
        {
            return await RunAsync(playlistId, trigger, dryRun, ct);
        }
        finally
        {
            _gate.Release();
            activity.Clear(playlistId);
        }
    }

    private async Task<SyncOutcome> RunAsync(int id, SyncTrigger trigger, bool dryRun, CancellationToken ct)
    {
        var run = new SyncRun { PlaylistId = id, Trigger = trigger, StartedAt = time.GetUtcNow() };
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            db.SyncRuns.Add(run);
            await db.SaveChangesAsync(ct);
        }

        SyncOutcome outcome;
        try
        {
            outcome = await SyncCoreAsync(id, run, dryRun, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome = new SyncOutcome(SyncResult.Failed, "Cancelled (shutting down).");
        }
        catch (YotoAuthException ex)
        {
            logger.LogWarning(ex, "Sync of playlist {Id} failed: Yoto auth", id);
            outcome = new SyncOutcome(SyncResult.Failed, $"Yoto login problem: {ex.Message}");
            await notify.NotifyAsync(new Notification(NotificationKind.YotoNeedsLogin, "Yoto needs re-login", ex.Message, id), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sync of playlist {Id} failed", id);
            var msg = ex is YotoApiException api ? $"Yoto API {api.StatusCode}: {api.Message}" : ex.Message;
            outcome = new SyncOutcome(SyncResult.Failed, msg);
            await notify.NotifyAsync(new Notification(NotificationKind.SyncFailed, "Sync failed", msg, id), CancellationToken.None);
        }

        // Record outcome (never with the caller's token: we want this written even on shutdown).
        await using (var db = await dbFactory.CreateDbContextAsync(CancellationToken.None))
        {
            var r = await db.SyncRuns.SingleAsync(x => x.Id == run.Id);
            r.FinishedAt = time.GetUtcNow();
            r.Result = outcome.Result;
            r.Message = Truncate(outcome.Message, 2000);
            r.NewEpisodes = run.NewEpisodes;
            r.Uploaded = run.Uploaded;

            var s = await db.Playlists.SingleOrDefaultAsync(x => x.Id == id);
            if (s is not null && outcome.Result != SyncResult.DryRun)
            {
                s.LastSyncResult = outcome.Result;
                if (outcome.Result == SyncResult.Failed)
                {
                    s.LastError = Truncate(outcome.Message, 1000);
                }
                else
                {
                    s.LastSyncedAt = r.FinishedAt;
                    s.LastError = null;
                }
            }

            await db.SaveChangesAsync();

            // Prune history.
            var cutoff = await db.SyncRuns.Where(x => x.PlaylistId == id)
                .OrderByDescending(x => x.StartedAt).Skip(MaxRunsKept).Select(x => (int?)x.Id).FirstOrDefaultAsync();
            if (cutoff is not null)
                await db.SyncRuns.Where(x => x.PlaylistId == id && x.Id <= cutoff).ExecuteDeleteAsync();
        }

        return outcome;
    }

    private async Task<SyncOutcome> SyncCoreAsync(int id, SyncRun run, bool dryRun, CancellationToken ct)
    {
        var notes = new List<string>();

        // 1. Poll every included podcast. One broken feed doesn't hold up the others: its stored episodes are used.
        var podcastIds = await GetIncludedPodcastsAsync(id, ct);
        var failedPolls = 0;
        foreach (var (podcastId, name) in podcastIds)
        {
            activity.Set(id, podcastIds.Count > 1 ? $"Checking: {name}…" : "Checking feed…");
            var poll = await subscriptions.PollAsync(podcastId, ct);
            if (!poll.Ok)
            {
                failedPolls++;
                notes.Add(podcastIds.Count > 1 ? $"{name}: {poll.Describe()}" : poll.Describe());
            }
            run.NewEpisodes += poll.Merge.Added;
        }
        if (podcastIds.Count > 0 && failedPolls == podcastIds.Count) throw new InvalidOperationException(string.Join(" ", notes));

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var p = await db.Playlists
            .Include(x => x.Subscriptions).ThenInclude(s => s.Episodes)
            .AsSplitQuery()
            .SingleAsync(x => x.Id == id, ct);

        // 2. Plan
        var plan = PlaylistPlanner.Plan(p, p.Subscriptions, time.GetUtcNow());

        if (dryRun)
        {
            var missing = plan.Episodes.Count(e => e.AudioId is null);
            return new SyncOutcome(SyncResult.DryRun,
                $"Dry run: {plan.Episodes.Count} episodes planned, {missing} would be uploaded. {string.Join(" ", plan.Warnings.Concat(notes))}".Trim(), plan);
        }

        // A new week's rotation is kept even if publishing fails, so the next sync retries the same tracks.
        if (plan.Rotations.Any(r => r.IsNew))
        {
            TrackRotation.Record(plan.Rotations, plan.Episodes);
            await db.SaveChangesAsync(ct);
        }

        // 3. Audio
        var podcasts = p.Subscriptions.ToDictionary(s => s.Id);
        var wantsIcons = plan.Episodes.Any(e => !podcasts[e.SubscriptionId].UseDefaultIcon);
        var icons = wantsIcons ? await iconCatalog.GetAsync(ct) : null;
        if (wantsIcons && icons is null && iconCatalog.LastError is not null) notes.Add(iconCatalog.LastError);

        var entries = new List<PlaylistEntry>();
        var skipped = new List<string>();
        var i = 0;
        foreach (var e in plan.Episodes)
        {
            i++;
            var needsUpload = e.AudioId is null;
            if (needsUpload) activity.Set(id, $"Uploading {i}/{plan.Episodes.Count}: {e.Title}");
            try
            {
                var a = await audio.EnsureAsync(db, e, ct);
                if (needsUpload) run.Uploaded++;
                entries.Add(new PlaylistEntry(e.Title, AudioPipeline.ToTranscoded(a), IconFor(e, podcasts[e.SubscriptionId], icons)));
            }
            catch (Exception ex) when (ex is HttpRequestException or YotoApiException or TimeoutException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                // One bad episode shouldn't block the rest of the playlist.
                logger.LogWarning(ex, "Skipping '{Title}': audio unavailable", e.Title);
                skipped.Add(e.Title);
            }
        }

        if (entries.Count == 0)
            throw new InvalidOperationException("No episodes could be prepared; nothing to publish.");

        // 4. Cover
        if (p.DesiredCoverUrl is { } coverUrl && (p.CoverMediaUrl is null || p.CoverSourceUrl != coverUrl))
        {
            activity.Set(id, "Uploading cover…");
            try
            {
                p.CoverMediaUrl = await yoto.UploadCoverImageFromUrlAsync(coverUrl, ct: ct);
                p.CoverSourceUrl = coverUrl;
                await db.SaveChangesAsync(ct);
            }
            catch (YotoApiException ex)
            {
                logger.LogWarning(ex, "Cover upload failed for playlist {Id}; continuing without", id);
            }
        }

        // 5. Publish
        var request = PlaylistBuilder.Build(p.Name, entries, p.YotoCardId, p.CoverMediaUrl);
        var hash = Hash(request);

        if (p.YotoCardId is not null && hash == p.LastPublishedHash)
            return new SyncOutcome(SyncResult.NoChange, Summary("No changes.", entries.Count, run, skipped, plan, notes), plan);

        activity.Set(id, p.YotoCardId is null ? "Creating Yoto playlist…" : "Updating Yoto playlist…");
        if (p.YotoCardId is not null)
            await MergeExistingSettingsAsync(p.YotoCardId, request, ct);

        var card = await yoto.CreateOrUpdateContentAsync(request, ct);
        var created = p.YotoCardId is null;
        p.YotoCardId = card.GetProperty("cardId").GetString();
        p.LastPublishedHash = hash;
        await db.SaveChangesAsync(ct);

        var message = Summary(created ? $"Created playlist {p.YotoCardId}." : "Playlist updated.", entries.Count, run, skipped, plan, notes);
        logger.LogInformation("Playlist {Id} '{Name}': {Message}", id, p.Name, message);
        await notify.NotifyAsync(new Notification(NotificationKind.PlaylistUpdated, p.Name, message, id), CancellationToken.None);
        return new SyncOutcome(SyncResult.Updated, message, plan);
    }

    private async Task<List<(int Id, string Name)>> GetIncludedPodcastsAsync(int playlistId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await db.Subscriptions.AsNoTracking()
                .Where(s => s.PlaylistId == playlistId && s.Enabled)
                .OrderBy(s => s.Position).ThenBy(s => s.Id)
                .Select(s => new { s.Id, s.Name })
                .ToListAsync(ct))
            .Select(x => (x.Id, x.Name))
            .ToList();
    }

    /// <summary>
    /// The icon for an episode: hand-picked always wins; then a Yoto playlist track's icon from its source; then the podcast's logo icon if it's set to "always"; then emoji
    /// matching (unless turned off for the podcast); then the logo icon if it's the fallback; else the player's default.
    /// </summary>
    public static string? IconFor(Episode e, Subscription podcast, IconIndex? icons)
    {
        var logo = podcast.LogoIconMediaId is { } m && podcast.LogoIcon != LogoIconMode.Off ? $"yoto:#{m}" : null;
        if (e.IconMediaId == Episode.PodcastDefaultIcon) return logo;
        if (e.IconMediaId == Episode.NoIcon) return null;
        if (e.IconMediaId is { } chosen) return $"yoto:#{chosen}";
        if (e.SourceIconRef is { } source) return source;
        if (logo is not null && podcast.LogoIcon == LogoIconMode.Always) return logo;

        var matched = podcast.UseDefaultIcon || icons is null ? null : IconMatcher.Match(e.Title, icons)?.Icon.Ref;
        return matched ?? logo;
    }

    /// <summary>Keep Yoto-side settings (resume timeout, playback type, cover if we have none) when updating.</summary>
    private async Task MergeExistingSettingsAsync(string cardId, YotoContentRequest request, CancellationToken ct)
    {
        JsonDocument doc;
        try
        {
            doc = await yoto.GetContentAsync(cardId, ct);
        }
        catch (YotoApiException ex) when (ex.StatusCode == 404)
        {
            throw new InvalidOperationException($"Yoto playlist {cardId} no longer exists. Detach it in settings to create a new one.", ex);
        }
        catch (YotoApiException ex) when (ex.StatusCode == 403)
        {
            // Without the current playlist we can't preserve its Yoto-side settings; refuse rather than overwrite them.
            throw new InvalidOperationException(
                $"Not allowed to read playlist {cardId} (needs the user:content:view scope). Reconnect Yoto on the dashboard to grant it. Nothing was published.", ex);
        }

        using (doc)
        {
            var card = doc.RootElement.TryGetProperty("card", out var c) ? c : doc.RootElement;
            if (card.TryGetProperty("content", out var content))
            {
                if (content.TryGetProperty("config", out var config) && config.ValueKind == JsonValueKind.Object)
                    request.Content.Config = JsonNode.Parse(config.GetRawText())!.AsObject();
                if (content.TryGetProperty("playbackType", out var pt) && pt.ValueKind == JsonValueKind.String)
                    request.Content.PlaybackType = pt.GetString();
            }

            if (request.Metadata is { Cover: null }
                && card.TryGetProperty("metadata", out var md)
                && md.TryGetProperty("cover", out var cover)
                && cover.TryGetProperty("imageL", out var img)
                && img.ValueKind == JsonValueKind.String)
            {
                request.Metadata.Cover = new YotoCover { ImageL = img.GetString() };
            }
        }
    }

    /// <summary>Hash of what we'd publish, excluding cardId (so create → first update doesn't look like a change).</summary>
    private static string Hash(YotoContentRequest request)
    {
        var cardId = request.CardId;
        request.CardId = null;
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, HashJson))));
        }
        finally
        {
            request.CardId = cardId;
        }
    }

    private static string Summary(string head, int tracks, SyncRun run, List<string> skipped, PlannedPlaylist plan, List<string> notes)
    {
        var sb = new StringBuilder(head).Append($" {tracks} tracks; {run.NewEpisodes} new in feed; {run.Uploaded} uploaded.");
        if (skipped.Count > 0) sb.Append($" Skipped (audio unavailable): {string.Join(", ", skipped)}.");
        foreach (var w in plan.Warnings) sb.Append(' ').Append(w);
        foreach (var n in notes) sb.Append(' ').Append(n);
        return sb.ToString();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
