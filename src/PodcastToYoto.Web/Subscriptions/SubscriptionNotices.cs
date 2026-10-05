using System.Collections.Concurrent;

namespace PodcastToYoto.Web.Subscriptions;

/// <summary>One-shot messages carried across a navigation (e.g. "created + attached playlist: …").</summary>
public sealed class SubscriptionNotices
{
    private readonly ConcurrentDictionary<int, string> _notices = new();

    public void Set(int subscriptionId, string message) => _notices[subscriptionId] = message;

    public string? Take(int subscriptionId) => _notices.TryRemove(subscriptionId, out var m) ? m : null;
}
