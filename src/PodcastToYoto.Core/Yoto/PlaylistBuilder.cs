namespace PodcastToYoto.Yoto;

public sealed record PlaylistEntry(string Title, YotoTranscodedAudio Audio, string? IconRef = null);

/// <summary>Builds the POST /content body: one chapter per episode, one track per chapter.</summary>
public static class PlaylistBuilder
{
    public static YotoContentRequest Build(
        string title,
        IReadOnlyList<PlaylistEntry> entries,
        string? cardId = null,
        string? coverUrl = null,
        string? description = null,
        string? author = null)
    {
        var width = entries.Count >= 100 ? 3 : 2;
        var chapters = new List<YotoChapter>(entries.Count);

        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var key = (i + 1).ToString().PadLeft(width, '0');
            var label = (i + 1).ToString();
            var display = e.IconRef is null ? null : new YotoDisplay { Icon16x16 = e.IconRef };

            chapters.Add(new YotoChapter
            {
                Key = key,
                Title = e.Title,
                OverlayLabel = label,
                Display = display,
                Tracks =
                [
                    new YotoTrack
                    {
                        Key = "01",
                        Title = e.Title,
                        TrackUrl = e.Audio.TrackUrl,
                        Duration = e.Audio.Duration,
                        FileSize = e.Audio.FileSize,
                        Channels = e.Audio.Channels?.DeepClone(),
                        Format = e.Audio.Format?.DeepClone(),
                        OverlayLabel = label,
                        Display = display,
                    },
                ],
            });
        }

        var totalDuration = entries.Sum(e => e.Audio.Duration ?? 0);
        var totalSize = entries.Sum(e => e.Audio.FileSize ?? 0);

        return new YotoContentRequest
        {
            CardId = cardId,
            Title = title,
            Content = new YotoContentBody { Chapters = chapters },
            Metadata = new YotoMetadata
            {
                Description = description,
                Author = author,
                Cover = coverUrl is null ? null : new YotoCover { ImageL = coverUrl },
                Media = new YotoMediaInfo { Duration = totalDuration, FileSize = totalSize },
            },
        };
    }
}
