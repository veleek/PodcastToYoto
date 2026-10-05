using System.Text.Json;

namespace PodcastToYoto.Yoto;

public sealed record YotoTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string? Scope)
{
    public bool IsExpiring(TimeSpan skew, DateTimeOffset now) => now + skew >= ExpiresAt;
}

public interface ITokenStore
{
    Task<YotoTokens?> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(YotoTokens tokens, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);

    /// <summary>Called when Yoto rejects the refresh token; the user must log in again.</summary>
    Task MarkInvalidAsync(string reason, CancellationToken ct = default);
}

/// <summary>
/// JSON file token store. Writes are atomic (temp file + rename) because refresh tokens are
/// single-use: losing the newly issued one means the user has to log in again.
/// </summary>
public sealed class FileTokenStore(string path) : ITokenStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<YotoTokens?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(path)) return null;
        await using var fs = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<YotoTokens>(fs, Json, ct);
    }

    public async Task SaveAsync(YotoTokens tokens, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        await using (var fs = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(fs, tokens, Json, ct);
        }
        File.Move(tmp, path, overwrite: true);
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task MarkInvalidAsync(string reason, CancellationToken ct = default) => Task.CompletedTask;
}
