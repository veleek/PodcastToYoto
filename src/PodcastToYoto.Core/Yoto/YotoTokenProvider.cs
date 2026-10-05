namespace PodcastToYoto.Yoto;

/// <summary>Hands out a valid access token, refreshing (and persisting the rotated refresh token) when needed.</summary>
public sealed class YotoTokenProvider(YotoAuthClient auth, ITokenStore store, YotoOAuthOptions options, TimeProvider? time = null)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private YotoTokens? _cached;

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        var current = _cached;
        if (current is not null && !current.IsExpiring(options.RefreshSkew, _time.GetUtcNow()))
            return current.AccessToken;

        await _lock.WaitAsync(ct);
        try
        {
            _cached ??= await store.LoadAsync(ct)
                        ?? throw new YotoAuthException("Not logged in to Yoto (no stored tokens).");

            if (_cached.IsExpiring(options.RefreshSkew, _time.GetUtcNow()))
                await RefreshCoreAsync(ct);

            return _cached.AccessToken;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Force a refresh regardless of expiry (used to verify rotation works).</summary>
    public async Task<YotoTokens> ForceRefreshAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            _cached ??= await store.LoadAsync(ct)
                        ?? throw new YotoAuthException("Not logged in to Yoto (no stored tokens).");
            await RefreshCoreAsync(ct);
            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetTokensAsync(YotoTokens tokens, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await store.SaveAsync(tokens, ct);
            _cached = tokens;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Forget cached and stored tokens (disconnect).</summary>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await store.ClearAsync(ct);
            _cached = null;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        YotoTokens refreshed;
        try
        {
            refreshed = await auth.RefreshAsync(_cached!.RefreshToken, ct);
        }
        catch (YotoAuthException ex) when (ex.StatusCode is >= 400 and < 500)
        {
            // invalid_grant etc.: the refresh token is dead, retrying won't help.
            await store.MarkInvalidAsync(ex.Message, ct);
            _cached = null;
            throw;
        }

        // Persist before using: the old refresh token is now dead.
        await store.SaveAsync(refreshed, ct);
        _cached = refreshed;
    }
}
