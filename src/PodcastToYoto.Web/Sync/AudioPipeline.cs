using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PodcastToYoto.Feeds;
using PodcastToYoto.Web.Data;
using PodcastToYoto.Yoto;

namespace PodcastToYoto.Web.Sync;

/// <summary>Makes sure an episode has transcoded audio on Yoto: download → hash → reuse or upload → transcode.</summary>
public sealed class AudioPipeline(EpisodeDownloader downloader, YotoAudioUploader uploader, TimeProvider time, ILogger<AudioPipeline> logger)
{
    /// <summary>Ensures <paramref name="episode"/>.Audio is set. Saves the Audio row immediately so a crash doesn't lose the upload.</summary>
    public async Task<Audio> EnsureAsync(AppDbContext db, Episode episode, CancellationToken ct)
    {
        if (episode.Audio is not null) return episode.Audio;
        if (episode.AudioId is { } id)
        {
            episode.Audio = await db.Audio.SingleAsync(a => a.Id == id, ct);
            return episode.Audio;
        }

        // A track from a Yoto playlist source: its audio is already on Yoto (normally linked when the source is read).
        if (episode.EnclosureUrl.StartsWith(YotoCardReader.RefPrefix, StringComparison.Ordinal))
        {
            var sha = episode.EnclosureUrl[YotoCardReader.RefPrefix.Length..];
            episode.Audio = await Subscriptions.YotoAudioRefs.FindOrAddAsync(db, sha, episode.DurationSeconds, episode.EnclosureLength,
                null, null, time.GetUtcNow(), ct);
            await db.SaveChangesAsync(ct);
            return episode.Audio;
        }

        var sw = Stopwatch.StartNew();
        using var file = await downloader.DownloadAsync(episode.EnclosureUrl, ct);
        var downloaded = sw.Elapsed;

        var audio = await db.Audio.SingleOrDefaultAsync(a => a.SourceSha256 == file.Sha256Hex, ct);
        if (audio is null)
        {
            sw.Restart();
            var t = await uploader.UploadAsync(file, $"{Slug(episode.Title)}.mp3", ct);
            audio = new Audio
            {
                SourceSha256 = file.Sha256Hex,
                TranscodedSha256 = t.TranscodedSha256,
                DurationSeconds = t.Duration,
                FileSize = t.FileSize,
                ChannelsJson = t.Channels?.ToJsonString(),
                FormatJson = t.Format?.ToJsonString(),
                UploadedAt = time.GetUtcNow(),
            };
            db.Audio.Add(audio);
            logger.LogInformation("Uploaded '{Title}' ({SizeMb:F1} MB): download {Download:F0}s, upload+transcode {Upload:F0}s → yoto:#{Sha}",
                episode.Title, file.Length / 1_000_000.0, downloaded.TotalSeconds, sw.Elapsed.TotalSeconds, t.TranscodedSha256);
        }
        else
        {
            logger.LogInformation("Reusing existing upload for '{Title}' (same source file)", episode.Title);
        }

        episode.Audio = audio;
        await db.SaveChangesAsync(ct);
        return audio;
    }

    public static YotoTranscodedAudio ToTranscoded(Audio a) => new(
        a.TranscodedSha256,
        a.DurationSeconds,
        a.FileSize,
        a.ChannelsJson is null ? null : JsonNode.Parse(a.ChannelsJson),
        a.FormatJson is null ? null : JsonNode.Parse(a.FormatJson),
        default);

    private static string Slug(string title)
    {
        var chars = title.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_').ToArray();
        var s = new string(chars).Trim();
        return s.Length == 0 ? "episode" : s.Length > 80 ? s[..80] : s;
    }
}
