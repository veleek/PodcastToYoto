using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using PodcastToYoto.Web.Data;

namespace PodcastToYoto.Web.Subscriptions;

public sealed class ExportFile
{
    public int Version { get; set; } = 1;
    public DateTimeOffset ExportedAt { get; set; }
    public List<ExportedSubscription> Subscriptions { get; set; } = [];
}

public sealed class ExportedSubscription
{
    public required SubscriptionSettings Settings { get; set; }
    public string? YotoCardId { get; set; }

    /// <summary>Episode GUIDs pinned as favourites.</summary>
    public List<string> Favourites { get; set; } = [];
}

public sealed record ImportResult(int Created, int Updated, int FavouritesSet, int FavouritesNotFound, IReadOnlyList<string> Errors)
{
    public string Describe() =>
        $"{Created} created, {Updated} updated, {FavouritesSet} favourites set" +
        (FavouritesNotFound > 0 ? $", {FavouritesNotFound} favourite episode(s) not found in the feed" : "") + "." +
        (Errors.Count > 0 ? " Errors: " + string.Join("; ", Errors) : "");
}

/// <summary>Export / import of subscriptions + favourites as JSON (backup, or seeding a fresh install).</summary>
public sealed class SubscriptionTransfer(
    IDbContextFactory<AppDbContext> dbFactory,
    SubscriptionService subscriptions,
    TimeProvider time,
    ILogger<SubscriptionTransfer> logger)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<ExportFile> ExportAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var subs = await db.Subscriptions.AsNoTracking().Include(s => s.Episodes).OrderBy(s => s.Name).ToListAsync(ct);

        return new ExportFile
        {
            ExportedAt = time.GetUtcNow(),
            Subscriptions = subs.Select(s => new ExportedSubscription
            {
                Settings = SubscriptionSettings.From(s),
                YotoCardId = s.YotoCardId,
                Favourites = s.Episodes.Where(e => e.IsFavourite).Select(e => e.Guid).ToList(),
            }).ToList(),
        };
    }

    public static ExportFile Parse(Stream json) =>
        JsonSerializer.Deserialize<ExportFile>(json, Json) ?? throw new JsonException("Empty file.");

    /// <summary>
    /// Matches existing subscriptions on (feed URL, playlist id). Matches are updated; others are created and polled.
    /// Favourites in the file are authoritative for each imported subscription.
    /// </summary>
    public async Task<ImportResult> ImportAsync(ExportFile file, CancellationToken ct = default)
    {
        if (file.Version != 1) throw new InvalidOperationException($"Unsupported export version {file.Version}.");

        int created = 0, updated = 0, favSet = 0, favMissing = 0;
        var errors = new List<string>();
        var existing = await subscriptions.ListAsync(ct);

        foreach (var x in file.Subscriptions)
        {
            var validation = new List<ValidationResult>();
            if (!Validator.TryValidateObject(x.Settings, new ValidationContext(x.Settings), validation, validateAllProperties: true))
            {
                errors.Add($"'{x.Settings.Name}': {string.Join(" ", validation.Select(v => v.ErrorMessage))}");
                continue;
            }

            try
            {
                int id;
                var match = existing.FirstOrDefault(s =>
                    string.Equals(s.FeedUrl, x.Settings.FeedUrl.Trim(), StringComparison.Ordinal) && s.YotoCardId == x.YotoCardId);

                if (match is not null)
                {
                    id = match.Id;
                    await subscriptions.UpdateSettingsAsync(id, x.Settings, ct);
                    updated++;
                }
                else
                {
                    (id, _) = await subscriptions.CreateAsync(x.Settings, ct);
                    if (x.YotoCardId is not null) await subscriptions.SetPlaylistAsync(id, x.YotoCardId, ct);
                    created++;
                }

                var (set, missing) = await ApplyFavouritesAsync(id, x.Favourites, ct);
                favSet += set;
                favMissing += missing;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Import of '{Name}' failed", x.Settings.Name);
                errors.Add($"'{x.Settings.Name}': {ex.Message}");
            }
        }

        var result = new ImportResult(created, updated, favSet, favMissing, errors);
        logger.LogInformation("Import: {Result}", result.Describe());
        return result;
    }

    private async Task<(int Set, int Missing)> ApplyFavouritesAsync(int subscriptionId, List<string> guids, CancellationToken ct)
    {
        var wanted = guids.ToHashSet(StringComparer.Ordinal);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var eps = await db.Episodes.Where(e => e.SubscriptionId == subscriptionId).ToListAsync(ct);
        foreach (var e in eps) e.IsFavourite = wanted.Contains(e.Guid);
        await db.SaveChangesAsync(ct);

        var found = eps.Count(e => e.IsFavourite);
        return (found, wanted.Count - found);
    }
}
