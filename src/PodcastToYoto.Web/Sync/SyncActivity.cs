using System.Collections.Concurrent;

namespace PodcastToYoto.Web.Sync;

/// <summary>In-memory "what is the sync doing right now" for live UI updates.</summary>
public sealed class SyncActivity
{
    private readonly ConcurrentDictionary<int, string> _current = new();

    /// <summary>Raised with the playlist id whenever its activity changes (on a background thread).</summary>
    public event Action<int>? Changed;

    public string? Get(int playlistId) => _current.TryGetValue(playlistId, out var s) ? s : null;

    public bool IsBusy(int playlistId) => _current.ContainsKey(playlistId);

    public void Set(int playlistId, string message)
    {
        _current[playlistId] = message;
        Changed?.Invoke(playlistId);
    }

    public void Clear(int playlistId)
    {
        _current.TryRemove(playlistId, out _);
        Changed?.Invoke(playlistId);
    }
}
