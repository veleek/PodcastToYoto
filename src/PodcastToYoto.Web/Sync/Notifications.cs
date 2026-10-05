namespace PodcastToYoto.Web.Sync;

public enum NotificationKind { PlaylistUpdated, SyncFailed, YotoNeedsLogin }

public sealed record Notification(NotificationKind Kind, string Title, string Message, int? SubscriptionId = null);

/// <summary>Outbound notifications. Log-only today; Home Assistant later.</summary>
public interface INotificationSink
{
    Task NotifyAsync(Notification notification, CancellationToken ct = default);
}

public sealed class LogNotificationSink(ILogger<LogNotificationSink> logger) : INotificationSink
{
    public Task NotifyAsync(Notification n, CancellationToken ct = default)
    {
        var level = n.Kind == NotificationKind.PlaylistUpdated ? LogLevel.Information : LogLevel.Warning;
        logger.Log(level, "[{Kind}] {Title}: {Message}", n.Kind, n.Title, n.Message);
        return Task.CompletedTask;
    }
}
