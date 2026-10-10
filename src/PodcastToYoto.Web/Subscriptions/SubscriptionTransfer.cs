using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using PodcastToYoto.Web.Data;

namespace PodcastToYoto.Web.Subscriptions;

/// <summary>
/// Export file. Version 2 holds <see cref="Playlists"/>; version 1 (before playlists could hold several podcasts) held
/// <see cref="Subscriptions"/>, one podcast per playlist. Both import.
/// </summary>
public sealed class ExportFile
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public DateTimeOffset ExportedAt { get; set; }
    public List<ExportedPlaylist> Playlists { get; set; } = [];

    /// <summary>Version 1 only.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<LegacyExportedSubscription>? Subscriptions { get; set; }

    /// <summary>The playlists in this file, converting a version 1 file.</summary>
    public IReadOnlyList<ExportedPlaylist> AllPlaylists() =>
        Version == 1 ? (Subscriptions ?? []).Select(s => s.ToPlaylist()).ToList() : Playlists;
}

public sealed class ExportedPlaylist
{
    public required PlaylistSettings Settings { get; set; }
    public string? YotoCardId { get; set; }

    /// <summary>In playlist order.</summary>
    public List<ExportedPodcast> Podcasts { get; set; } = [];
}

public sealed class ExportedPodcast
{
    public required SubscriptionSettings Settings { get; set; }

    /// <summary>Episode GUIDs pinned as favourites.</summary>
    public List<string> Favourites { get; set; } = [];

    /// <summary>Hand-picked icons: episode GUID → Yoto icon media id.</summary>
    public Dictionary<string, string> Icons { get; set; } = new();

    /// <summary>Custom logo icon uploaded to Yoto (media id and display URL).</summary>
    public string? LogoIconMediaId { get; set; }
    public string? LogoIconUrl { get; set; }
}

/// <summary>A subscription as exported by version 1 (one podcast = one playlist).</summary>
public sealed class LegacyExportedSubscription
{
    public required LegacySettings Settings { get; set; }
    public string? YotoCardId { get; set; }
    public List<string> Favourites { get; set; } = [];
    public Dictionary<string, string> Icons { get; set; } = new();

    public sealed class LegacySettings
    {
        public string Name { get; set; } = "";
        public string FeedUrl { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public string? TitleOverride { get; set; }
        public int RecentCount { get; set; } = 25;
        public int MaxTracks { get; set; } = 100;
        public PlaylistOrder Order { get; set; } = PlaylistOrder.NewestFirst;
        public bool IncludeTrailers { get; set; }
        public bool MatchIcons { get; set; } = true;
        public int? PollIntervalMinutes { get; set; }
    }

    public ExportedPlaylist ToPlaylist() => new()
    {
        Settings = new PlaylistSettings
        {
            Name = Settings.TitleOverride ?? Settings.Name,
            Enabled = Settings.Enabled,
            MaxTracks = Settings.MaxTracks,
            Order = Settings.Order == PlaylistOrder.OldestFirst ? TrackOrder.OldestFirst : TrackOrder.NewestFirst,
            PollIntervalMinutes = Settings.PollIntervalMinutes,
        },
        YotoCardId = YotoCardId,
        Podcasts =
        [
            new ExportedPodcast
            {
                Settings = new SubscriptionSettings
                {
                    Name = Settings.Name,
                    FeedUrl = Settings.FeedUrl,
                    RecentCount = Settings.RecentCount,
                    MaxTracks = Settings.MaxTracks,
                    Order = Settings.Order,
                    IncludeTrailers = Settings.IncludeTrailers,
                    MatchIcons = Settings.MatchIcons,
                },
                Favourites = Favourites,
                Icons = Icons,
            },
        ],
    };
}

public sealed record ImportResult(int Created, int Updated, int PodcastsAdded, int FavouritesSet, int FavouritesNotFound, IReadOnlyList<string> Errors)
{
    public string Describe() =>
        $"{Created} playlist(s) created, {Updated} updated, {PodcastsAdded} podcast(s) added, {FavouritesSet} favourites set" +
        (FavouritesNotFound > 0 ? $", {FavouritesNotFound} favourite episode(s) not found in the feed" : "") + "." +
        (Errors.Count > 0 ? " Errors: " + string.Join("; ", Errors) : "");
}

/// <summary>Export / import of playlists, their podcasts and favourites as JSON (backup, or seeding a fresh install).</summary>
public sealed class SubscriptionTransfer(
    IDbContextFactory<AppDbContext> dbFactory,
    PlaylistService playlists,
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
        var all = await db.Playlists.AsNoTracking()
            .Include(p => p.Subscriptions).ThenInclude(s => s.Episodes)
            .AsSplitQuery()
            .OrderBy(p => p.Name)
            .ToListAsync(ct);

        return new ExportFile
        {
            ExportedAt = time.GetUtcNow(),
            Playlists = all.Select(p => new ExportedPlaylist
            {
                Settings = PlaylistSettings.From(p),
                YotoCardId = p.YotoCardId,
                Podcasts = p.Subscriptions.OrderBy(s => s.Position).ThenBy(s => s.Id).Select(s => new ExportedPodcast
                {
                    Settings = SubscriptionSettings.From(s),
                    Favourites = s.Episodes.Where(e => e.IsFavourite).Select(e => e.Guid).ToList(),
                    Icons = s.Episodes.Where(e => e.IconMediaId is not null).ToDictionary(e => e.Guid, e => e.IconMediaId!),
                    LogoIconMediaId = s.LogoIconMediaId,
                    LogoIconUrl = s.LogoIconUrl,
                }).ToList(),
            }).ToList(),
        };
    }

    public static ExportFile Parse(Stream json) =>
        JsonSerializer.Deserialize<ExportFile>(json, Json) ?? throw new JsonException("Empty file.");

    /// <summary>
    /// Playlists are matched to existing ones by Yoto playlist id, or by name when neither has one. Matches are updated;
    /// others are created. Within a playlist, podcasts are matched by feed URL; new ones are added and polled.
    /// Favourites in the file are authoritative for each imported podcast.
    /// </summary>
    public async Task<ImportResult> ImportAsync(ExportFile file, CancellationToken ct = default)
    {
        if (file.Version is < 1 or > ExportFile.CurrentVersion) throw new InvalidOperationException($"Unsupported export version {file.Version}.");

        int created = 0, updated = 0, added = 0, favSet = 0, favMissing = 0;
        var errors = new List<string>();
        var existing = await playlists.ListAsync(ct);

        foreach (var x in file.AllPlaylists())
        {
            var invalid = Validate(x.Settings).Concat(x.Podcasts.SelectMany(p => Validate(p.Settings).Select(e => $"{p.Settings.Name}: {e}"))).ToList();
            if (x.Podcasts.Count == 0) invalid.Add("no podcasts");
            if (invalid.Count > 0)
            {
                errors.Add($"'{x.Settings.Name}': {string.Join(" ", invalid)}");
                continue;
            }

            try
            {
                var match = existing.FirstOrDefault(p => x.YotoCardId is not null
                    ? p.YotoCardId == x.YotoCardId
                    : p.YotoCardId is null && string.Equals(p.Name, x.Settings.Name.Trim(), StringComparison.Ordinal));

                int id;
                List<int> podcastIds;
                if (match is not null)
                {
                    id = match.Id;
                    await playlists.UpdateSettingsAsync(id, x.Settings, ct);
                    podcastIds = [];
                    foreach (var p in x.Podcasts)
                    {
                        var same = match.Podcasts.FirstOrDefault(m => string.Equals(m.FeedUrl, p.Settings.FeedUrl.Trim(), StringComparison.Ordinal));
                        if (same is not null)
                        {
                            await subscriptions.UpdateSettingsAsync(same.Id, p.Settings, ct);
                            podcastIds.Add(same.Id);
                        }
                        else
                        {
                            podcastIds.Add((await subscriptions.AddAsync(id, p.Settings, ct)).Id);
                            added++;
                        }
                    }
                    updated++;
                }
                else
                {
                    var (newId, firstId, _) = await playlists.CreateAsync(x.Settings, x.Podcasts[0].Settings, ct);
                    id = newId;
                    podcastIds = [firstId];
                    foreach (var p in x.Podcasts.Skip(1)) podcastIds.Add((await subscriptions.AddAsync(id, p.Settings, ct)).Id);
                    added += x.Podcasts.Count;
                    if (x.YotoCardId is not null) await playlists.SetCardAsync(id, x.YotoCardId, ct);
                    created++;
                }

                for (var i = 0; i < x.Podcasts.Count; i++)
                {
                    var p = x.Podcasts[i];
                    var (set, missing) = await ApplyFavouritesAsync(podcastIds[i], p.Favourites, p.Icons, ct);
                    favSet += set;
                    favMissing += missing;
                    if (p.LogoIconMediaId is not null)
                        await subscriptions.SetLogoIconAsync(podcastIds[i], p.LogoIconMediaId, p.LogoIconUrl, p.Settings.LogoIcon, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Import of '{Name}' failed", x.Settings.Name);
                errors.Add($"'{x.Settings.Name}': {ex.Message}");
            }
        }

        var result = new ImportResult(created, updated, added, favSet, favMissing, errors);
        logger.LogInformation("Import: {Result}", result.Describe());
        return result;
    }

    private static IEnumerable<string> Validate(object settings)
    {
        var validation = new List<ValidationResult>();
        Validator.TryValidateObject(settings, new ValidationContext(settings), validation, validateAllProperties: true);
        return validation.Select(v => v.ErrorMessage ?? "invalid");
    }

    private async Task<(int Set, int Missing)> ApplyFavouritesAsync(
        int subscriptionId, List<string> guids, Dictionary<string, string> icons, CancellationToken ct)
    {
        var wanted = guids.ToHashSet(StringComparer.Ordinal);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var eps = await db.Episodes.Where(e => e.SubscriptionId == subscriptionId).ToListAsync(ct);
        foreach (var e in eps)
        {
            e.IsFavourite = wanted.Contains(e.Guid);
            e.IconMediaId = icons.GetValueOrDefault(e.Guid);
        }
        await db.SaveChangesAsync(ct);

        var found = eps.Count(e => e.IsFavourite);
        return (found, wanted.Count - found);
    }
}
