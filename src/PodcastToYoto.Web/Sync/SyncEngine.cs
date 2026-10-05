using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PodcastToYoto.Web.Data;
using PodcastToYoto.Web.Options;
using PodcastToYoto.Web.Subscriptions;
using PodcastToYoto.Yoto;

namespace PodcastToYoto.Web.Sync;

public sealed record SyncOutcome(SyncResult Result, string Message, PlannedPlaylist? Plan = null);

/// <summary>
/// One sync of one subscription: poll → plan → ensure audio → ensure cover → publish (skipped if unchanged).
/// Only one sync runs at a time across the app.
/// </summary>
public sealed class SyncEngine(
    IDbContextFactory<AppDbContext> dbFactory,
    SubscriptionService subscriptions,
    AudioPipeline audio,
    YotoApiClient yoto,
    SyncActivity activity,
    INotificationSink notify,
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

    public async Task<SyncOutcome> SyncAsync(int subscriptionId, SyncTrigger trigger, bool dryRun = false, CancellationToken ct = default)
    {
        dryRun |= options.CurrentValue.DryRun;
        activity.Set(subscriptionId, "Waiting for another sync…");
        try
        {
            await _gate.WaitAsync(ct);
        }
        catch
        {
            activity.Clear(subscriptionId);
            throw;
        }

        try
        {
            return await RunAsync(subscriptionId, trigger, dryRun, ct);
        }
        finally
        {
            _gate.Release();
            activity.Clear(subscriptionId);
        }
    }

    private async Task<SyncOutcome> RunAsync(int id, SyncTrigger trigger, bool dryRun, CancellationToken ct)
    {
        var run = new SyncRun { SubscriptionId = id, Trigger = trigger, StartedAt = time.GetUtcNow() };
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
            logger.LogWarning(ex, "Sync of subscription {Id} failed: Yoto auth", id);
            outcome = new SyncOutcome(SyncResult.Failed, $"Yoto login problem: {ex.Message}");
            await notify.NotifyAsync(new Notification(NotificationKind.YotoNeedsLogin, "Yoto needs re-login", ex.Message, id), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sync of subscription {Id} failed", id);
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

            var s = await db.Subscriptions.SingleOrDefaultAsync(x => x.Id == id);
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
            var cutoff = await db.SyncRuns.Where(x => x.SubscriptionId == id)
                .OrderByDescending(x => x.StartedAt).Skip(MaxRunsKept).Select(x => (int?)x.Id).FirstOrDefaultAsync();
            if (cutoff is not null)
                await db.SyncRuns.Where(x => x.SubscriptionId == id && x.Id <= cutoff).ExecuteDeleteAsync();
        }

        return outcome;
    }

    private async Task<SyncOutcome> SyncCoreAsync(int id, SyncRun run, bool dryRun, CancellationToken ct)
    {
        // 1. Poll
        activity.Set(id, "Checking feed…");
        var poll = await subscriptions.PollAsync(id, ct);
        if (!poll.Ok) throw new InvalidOperationException(poll.Describe());
        run.NewEpisodes = poll.Merge.Added;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.Subscriptions.Include(x => x.Episodes).SingleAsync(x => x.Id == id, ct);

        // 2. Plan
        var plan = PlaylistPlanner.Plan(s, s.Episodes);
        foreach (var w in plan.Warnings) logger.LogWarning("Subscription {Id}: {Warning}", id, w);

        if (dryRun)
        {
            var missing = plan.Episodes.Count(e => e.AudioId is null);
            return new SyncOutcome(SyncResult.DryRun,
                $"Dry run: {plan.Episodes.Count} episodes planned, {missing} would be uploaded. {string.Join(" ", plan.Warnings)}".Trim(), plan);
        }

        // 3. Audio
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
                entries.Add(new PlaylistEntry(e.Title, AudioPipeline.ToTranscoded(a)));
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
        if (s.CoverSourceUrl is not null && s.CoverMediaUrl is null)
        {
            activity.Set(id, "Uploading cover…");
            try
            {
                s.CoverMediaUrl = await yoto.UploadCoverImageFromUrlAsync(s.CoverSourceUrl, ct: ct);
                await db.SaveChangesAsync(ct);
            }
            catch (YotoApiException ex)
            {
                logger.LogWarning(ex, "Cover upload failed for subscription {Id}; continuing without", id);
            }
        }

        // 5. Publish
        var title = s.TitleOverride ?? s.Name;
        var request = PlaylistBuilder.Build(title, entries, s.YotoCardId, s.CoverMediaUrl);
        var hash = Hash(request);

        if (s.YotoCardId is not null && hash == s.LastPublishedHash)
            return new SyncOutcome(SyncResult.NoChange, Summary("No changes.", entries.Count, run, skipped, plan), plan);

        activity.Set(id, s.YotoCardId is null ? "Creating Yoto playlist…" : "Updating Yoto playlist…");
        if (s.YotoCardId is not null)
            await MergeExistingSettingsAsync(s.YotoCardId, request, ct);

        var card = await yoto.CreateOrUpdateContentAsync(request, ct);
        var created = s.YotoCardId is null;
        s.YotoCardId = card.GetProperty("cardId").GetString();
        s.LastPublishedHash = hash;
        await db.SaveChangesAsync(ct);

        var message = Summary(created ? $"Created playlist {s.YotoCardId}." : "Playlist updated.", entries.Count, run, skipped, plan);
        logger.LogInformation("Subscription {Id} '{Name}': {Message}", id, s.Name, message);
        await notify.NotifyAsync(new Notification(NotificationKind.PlaylistUpdated, s.Name, message, id), CancellationToken.None);
        return new SyncOutcome(SyncResult.Updated, message, plan);
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

    private static string Summary(string head, int tracks, SyncRun run, List<string> skipped, PlannedPlaylist plan)
    {
        var sb = new StringBuilder(head).Append($" {tracks} tracks; {run.NewEpisodes} new in feed; {run.Uploaded} uploaded.");
        if (skipped.Count > 0) sb.Append($" Skipped (audio unavailable): {string.Join(", ", skipped)}.");
        foreach (var w in plan.Warnings) sb.Append(' ').Append(w);
        return sb.ToString();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
