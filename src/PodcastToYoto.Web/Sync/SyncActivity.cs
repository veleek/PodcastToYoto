using System.Collections.Concurrent;

namespace PodcastToYoto.Web.Sync;

/// <summary>In-memory "what is the sync doing right now" for live UI updates.</summary>
public sealed class SyncActivity
{
    private readonly ConcurrentDictionary<int, string> _current = new();

    /// <summary>Raised with the subscription id whenever its activity changes (on a background thread).</summary>
    public event Action<int>? Changed;

    public string? Get(int subscriptionId) => _current.TryGetValue(subscriptionId, out var s) ? s : null;

    public bool IsBusy(int subscriptionId) => _current.ContainsKey(subscriptionId);

    public void Set(int subscriptionId, string message)
    {
        _current[subscriptionId] = message;
        Changed?.Invoke(subscriptionId);
    }

    public void Clear(int subscriptionId)
    {
        _current.TryRemove(subscriptionId, out _);
        Changed?.Invoke(subscriptionId);
    }
}
