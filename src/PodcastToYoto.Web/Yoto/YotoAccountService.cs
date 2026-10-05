using Microsoft.EntityFrameworkCore;
using PodcastToYoto.Web.Data;
using PodcastToYoto.Yoto;

namespace PodcastToYoto.Web.Yoto;

public sealed record YotoAccountStatus(
    AccountStatus Status,
    string? Scope,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? ConnectedAt,
    string? LastError);

public sealed record YotoConnectionTest(bool Ok, string Message, IReadOnlyList<YotoCardSummary> Playlists);

/// <summary>Account-level operations used by the UI.</summary>
public sealed class YotoAccountService(
    IDbContextFactory<AppDbContext> dbFactory,
    YotoTokenProvider tokens,
    YotoApiClient api,
    ILogger<YotoAccountService> logger)
{
    public async Task<YotoAccountStatus> GetStatusAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var a = await db.YotoAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == YotoAccount.SingletonId, ct);
        return a is null
            ? new YotoAccountStatus(AccountStatus.Disconnected, null, null, null, null)
            : new YotoAccountStatus(a.Status, a.Scope, a.ExpiresAt, a.ConnectedAt, a.LastError);
    }

    /// <summary>Calls GET /content/mine — exercises token refresh + API access end to end.</summary>
    public async Task<YotoConnectionTest> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            var cards = await api.GetMyContentAsync(ct);
            return new YotoConnectionTest(true, $"OK — {cards.Count} playlists visible.", cards);
        }
        catch (YotoAuthException ex)
        {
            logger.LogWarning(ex, "Yoto auth failed during connection test");
            return new YotoConnectionTest(false, ex.Message, []);
        }
        catch (YotoApiException ex)
        {
            logger.LogWarning(ex, "Yoto API call failed during connection test");
            return new YotoConnectionTest(false, $"Yoto API error {ex.StatusCode}: {ex.Message}", []);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Network error during connection test");
            return new YotoConnectionTest(false, $"Network error: {ex.Message}", []);
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await tokens.ClearAsync(ct);
        logger.LogInformation("Yoto account disconnected");
    }
}
