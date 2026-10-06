using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PodcastToYoto.Web.Data;
using PodcastToYoto.Web.Options;

namespace PodcastToYoto.Web.Sync;

public enum NotificationKind { PlaylistUpdated, SyncFailed, YotoNeedsLogin, Test }

public sealed record Notification(NotificationKind Kind, string Title, string Message, int? SubscriptionId = null);

/// <summary>A notification with links resolved, as handed to each channel.</summary>
public sealed record NotificationContext(
    Notification Notification,
    string? SubscriptionName,
    string? SubscriptionUrl,
    string? PlaylistUrl);

/// <summary>What the app calls to notify. Fans out to every <see cref="INotificationChannel"/>.</summary>
public interface INotificationSink
{
    Task NotifyAsync(Notification notification, CancellationToken ct = default);
}

/// <summary>One delivery mechanism (log, Home Assistant, …). Failures are logged, never thrown to the caller.</summary>
public interface INotificationChannel
{
    string Name { get; }
    bool IsEnabled { get; }
    Task SendAsync(NotificationContext context, CancellationToken ct);
}

public sealed class Notifier(
    IEnumerable<INotificationChannel> channels,
    IDbContextFactory<AppDbContext> dbFactory,
    IOptions<AppOptions> app,
    ILogger<Notifier> logger) : INotificationSink
{
    public async Task NotifyAsync(Notification n, CancellationToken ct = default)
    {
        var context = await ResolveAsync(n, ct);
        foreach (var channel in channels.Where(c => c.IsEnabled))
        {
            try
            {
                await channel.SendAsync(context, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Notification channel {Channel} failed for {Kind}", channel.Name, n.Kind);
            }
        }
    }

    /// <summary>Sends a test notification to every enabled channel; returns per-channel results for the UI.</summary>
    public async Task<IReadOnlyList<string>> SendTestAsync(CancellationToken ct = default)
    {
        var context = await ResolveAsync(new Notification(NotificationKind.Test, "Podcast → Yoto test", "Notifications are working."), ct);
        var results = new List<string>();
        foreach (var channel in channels)
        {
            if (!channel.IsEnabled) { results.Add($"{channel.Name}: not configured"); continue; }
            try
            {
                await channel.SendAsync(context, ct);
                results.Add($"{channel.Name}: sent");
            }
            catch (Exception ex)
            {
                results.Add($"{channel.Name}: failed: {ex.Message}");
            }
        }
        return results;
    }

    private async Task<NotificationContext> ResolveAsync(Notification n, CancellationToken ct)
    {
        var baseUrl = app.Value.PublicBaseUrl.TrimEnd('/');
        if (n.SubscriptionId is not { } id) return new NotificationContext(n, null, baseUrl, null);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.Subscriptions.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new { x.Name, x.YotoCardId })
            .SingleOrDefaultAsync(ct);

        return new NotificationContext(
            n,
            s?.Name,
            $"{baseUrl}/subscriptions/{id}",
            s?.YotoCardId is { } card ? $"https://my.yotoplay.com/cards/{card}" : null);
    }
}

public sealed class LogNotificationChannel(ILogger<LogNotificationChannel> logger) : INotificationChannel
{
    public string Name => "Log";
    public bool IsEnabled => true;

    public Task SendAsync(NotificationContext c, CancellationToken ct)
    {
        var n = c.Notification;
        var level = n.Kind is NotificationKind.PlaylistUpdated or NotificationKind.Test ? LogLevel.Information : LogLevel.Warning;
        logger.Log(level, "[{Kind}] {Title}: {Message}", n.Kind, n.Title, n.Message);
        return Task.CompletedTask;
    }
}
