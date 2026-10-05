using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using PodcastToYoto.Web.Data;
using PodcastToYoto.Yoto;

namespace PodcastToYoto.Web.Yoto;

/// <summary>Persists Yoto tokens in the single YotoAccount row, encrypted with Data Protection.</summary>
public sealed class DbTokenStore(IDbContextFactory<AppDbContext> dbFactory, IDataProtectionProvider dp, TimeProvider time) : ITokenStore
{
    private readonly IDataProtector _protector = dp.CreateProtector("PodcastToYoto.YotoTokens.v1");

    public async Task<YotoTokens?> LoadAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var a = await db.YotoAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == YotoAccount.SingletonId, ct);
        if (a is not { Status: AccountStatus.Connected, ProtectedAccessToken: not null, ProtectedRefreshToken: not null, ExpiresAt: not null })
            return null;

        try
        {
            return new YotoTokens(
                _protector.Unprotect(a.ProtectedAccessToken),
                _protector.Unprotect(a.ProtectedRefreshToken),
                a.ExpiresAt.Value,
                a.Scope);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Data Protection keys changed (e.g. DB copied to a new host without data/keys): tokens are unreadable.
            await MarkInvalidAsync("Stored tokens can't be decrypted (Data Protection keys changed). Reconnect Yoto.", ct);
            return null;
        }
    }

    public async Task SaveAsync(YotoTokens tokens, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var a = await GetOrCreateAsync(db, ct);
        var now = time.GetUtcNow();

        if (a.Status != AccountStatus.Connected) a.ConnectedAt = now;
        a.ProtectedAccessToken = _protector.Protect(tokens.AccessToken);
        a.ProtectedRefreshToken = _protector.Protect(tokens.RefreshToken);
        a.ExpiresAt = tokens.ExpiresAt;
        a.Scope = tokens.Scope;
        a.Status = AccountStatus.Connected;
        a.LastError = null;
        a.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var a = await GetOrCreateAsync(db, ct);
        a.ProtectedAccessToken = null;
        a.ProtectedRefreshToken = null;
        a.ExpiresAt = null;
        a.Scope = null;
        a.Status = AccountStatus.Disconnected;
        a.LastError = null;
        a.ConnectedAt = null;
        a.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkInvalidAsync(string reason, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var a = await GetOrCreateAsync(db, ct);
        a.Status = AccountStatus.NeedsReauth;
        a.LastError = reason;
        a.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    private static async Task<YotoAccount> GetOrCreateAsync(AppDbContext db, CancellationToken ct)
    {
        var a = await db.YotoAccounts.SingleOrDefaultAsync(x => x.Id == YotoAccount.SingletonId, ct);
        if (a is null)
        {
            a = new YotoAccount { Id = YotoAccount.SingletonId };
            db.YotoAccounts.Add(a);
        }
        return a;
    }
}
