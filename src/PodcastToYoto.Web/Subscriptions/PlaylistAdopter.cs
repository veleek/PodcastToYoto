using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PodcastToYoto.Web.Data;
using PodcastToYoto.Yoto;

namespace PodcastToYoto.Web.Subscriptions;

public sealed record AdoptResult(bool KeptAsFavourites, int Tracks, int Matched, int NewFavourites, int ReusedAudio, IReadOnlyList<string> Unmatched)
{
    public string Describe()
    {
        var head = Tracks == 0 ? "Playlist attached (it was empty)."
            : KeptAsFavourites
                ? $"Playlist attached. {Matched}/{Tracks} existing tracks matched to episodes and kept as favourites ({NewFavourites} new), " +
                  $"{ReusedAudio} reuse their existing Yoto audio." +
                  (Unmatched.Count == 0 ? "" : $" Not matched (will be removed on next sync): {string.Join(", ", Unmatched)}.")
                : $"Playlist attached. Its {Tracks} existing tracks will be replaced by the planned episodes; " +
                  $"{ReusedAudio} matched episodes reuse audio already on Yoto (no re-upload).";
        return head + " Automatic syncing is paused for this playlist: check the plan with Dry run, then Sync now, then re-enable it in Settings.";
    }
}

/// <summary>
/// Points a playlist at an existing Yoto playlist. Its current tracks are matched to the episodes of all the playlist's
/// podcasts so their audio is reused (no re-upload); optionally the matches also become favourites so they stay on the card.
/// </summary>
public sealed class PlaylistAdopter(
    IDbContextFactory<AppDbContext> dbFactory,
    YotoApiClient yoto,
    TimeProvider time,
    ILogger<PlaylistAdopter> logger)
{
    public async Task<AdoptResult> AttachAsync(int playlistId, string cardId, bool keepAsFavourites, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.Playlists
            .Include(x => x.Subscriptions).ThenInclude(x => x.Episodes).ThenInclude(e => e.Audio)
            .AsSplitQuery()
            .SingleAsync(x => x.Id == playlistId, ct);

        using var doc = await yoto.GetContentAsync(cardId, ct);
        var card = YotoCardReader.Card(doc);

        s.YotoCardId = cardId;
        s.LastPublishedHash = null; // force a publish on next sync

        // An existing playlist is about to be overwritten: don't let the scheduler do that before the user has
        // reviewed the plan. They re-enable automatic syncing in Settings.
        s.Enabled = false;

        // Keep the Yoto playlist's current name.
        var existingTitle = Str(card, "title");
        if (!string.IsNullOrWhiteSpace(existingTitle)) s.Name = existingTitle;

        var result = await AdoptTracksAsync(db, s, card, keepAsFavourites, ct);

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Playlist {Id} attached to Yoto playlist {CardId}: {Result}", playlistId, cardId, result.Describe());
        return result;
    }

    private async Task<AdoptResult> AdoptTracksAsync(AppDbContext db, Playlist s, JsonElement card, bool keepAsFavourites, CancellationToken ct)
    {
        var candidates = s.Subscriptions.SelectMany(x => x.Episodes)
            .OrderByDescending(e => e.PublishedAt ?? DateTimeOffset.MinValue)
            .Select(e => (TrackMatcher.Normalize(e.Title), e))
            .ToList();

        int tracks = 0, matched = 0, newFavs = 0, reused = 0;
        var unmatched = new List<string>();

        foreach (var track in YotoCardReader.Tracks(card))
        {
            tracks++;
            var ep = TrackMatcher.Match(track.Title, candidates);
            if (ep is null)
            {
                unmatched.Add(track.Title ?? "(untitled)");
                continue;
            }

            matched++;
            if (keepAsFavourites && !ep.IsFavourite)
            {
                ep.IsFavourite = true;
                newFavs++;
            }

            if (ep.AudioId is null && ep.Audio is null && track.Sha is not null)
            {
                ep.Audio = await YotoAudioRefs.FindOrAddAsync(db, track.Sha, track.Duration, track.FileSize,
                    track.ChannelsJson, track.FormatJson, time.GetUtcNow(), ct);
                reused++;
            }
        }

        return new AdoptResult(keepAsFavourites, tracks, matched, newFavs, reused, unmatched);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
