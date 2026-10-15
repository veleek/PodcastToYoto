using Microsoft.EntityFrameworkCore;
using PodcastToYoto.Web.Data;

namespace PodcastToYoto.Web.Subscriptions;

/// <summary>Audio rows for tracks already on Yoto (uploaded outside this app), referenced by their transcoded hash.</summary>
public static class YotoAudioRefs
{
    /// <summary>The Audio row for <paramref name="sha"/> (tracked or pending), adding one if there's none yet.</summary>
    public static async Task<Audio> FindOrAddAsync(AppDbContext db, string sha, long? duration, long? fileSize,
        string? channelsJson, string? formatJson, DateTimeOffset now, CancellationToken ct)
    {
        return db.Audio.Local.FirstOrDefault(a => a.TranscodedSha256 == sha)
            ?? await db.Audio.FirstOrDefaultAsync(a => a.TranscodedSha256 == sha, ct)
            ?? db.Audio.Add(new Audio
            {
                // Source file unknown (uploaded outside this app); key it by the transcoded hash instead.
                SourceSha256 = $"yoto:{sha}",
                TranscodedSha256 = sha,
                DurationSeconds = duration,
                FileSize = fileSize,
                ChannelsJson = channelsJson,
                FormatJson = formatJson,
                UploadedAt = now,
            }).Entity;
    }
}
