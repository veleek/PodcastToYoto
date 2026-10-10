using System.Text;
using PodcastToYoto.Web.Data;
using PodcastToYoto.Web.Subscriptions;

namespace PodcastToYoto.Tests;

public class TransferTests
{
    [Fact]
    public void Version_1_exports_become_one_podcast_playlists()
    {
        const string json = """
            {
              "version": 1,
              "exportedAt": "2026-10-06T00:00:00+00:00",
              "subscriptions": [
                {
                  "settings": { "name": "Silly Stories", "feedUrl": "https://x/feed", "enabled": false, "titleOverride": "Bedtime",
                                "recentCount": 10, "maxTracks": 50, "order": "OldestFirst", "includeTrailers": false,
                                "matchIcons": false, "pollIntervalMinutes": 30 },
                  "yotoCardId": "ABC",
                  "favourites": ["g1"],
                  "icons": { "g2": "m2" }
                }
              ]
            }
            """;
        var file = SubscriptionTransfer.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        var p = Assert.Single(file.AllPlaylists());

        Assert.Equal("Bedtime", p.Settings.Name);
        Assert.False(p.Settings.Enabled);
        Assert.Equal(50, p.Settings.MaxTracks);
        Assert.Equal(PlaylistOrder.OldestFirst, p.Settings.Order);
        Assert.False(p.Settings.GroupByPodcast);
        Assert.Equal(30, p.Settings.PollIntervalMinutes);
        Assert.Equal("ABC", p.YotoCardId);

        var pod = Assert.Single(p.Podcasts);
        Assert.Equal("Silly Stories", pod.Settings.Name);
        Assert.Equal(10, pod.Settings.RecentCount);
        Assert.False(pod.Settings.MatchIcons);
        Assert.Equal(["g1"], pod.Favourites);
        Assert.Equal("m2", pod.Icons["g2"]);
    }

    [Theory]
    [InlineData("GroupedByPodcast", null, PlaylistOrder.NewestFirst, true)]
    [InlineData("OldestFirst", "true", PlaylistOrder.OldestFirst, true)]
    [InlineData("OldestFirst", null, PlaylistOrder.OldestFirst, false)]
    public void Playlist_order_reads_old_and_new_files(string order, string? group, PlaylistOrder expectedOrder, bool expectedGroup)
    {
        var groupJson = group is null ? "" : $", \"groupByPodcast\": {group}";
        var json = $$"""
            { "version": 2, "playlists": [ { "settings": { "name": "Mix", "order": "{{order}}"{{groupJson}} },
              "podcasts": [ { "settings": { "name": "a", "feedUrl": "https://x/a" } } ] } ] }
            """;
        var p = Assert.Single(SubscriptionTransfer.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json))).AllPlaylists());
        Assert.Equal(expectedOrder, p.Settings.Order);
        Assert.Equal(expectedGroup, p.Settings.GroupByPodcast);

        var written = System.Text.Json.JsonSerializer.Serialize(p.Settings, SubscriptionTransfer.Json);
        var back = System.Text.Json.JsonSerializer.Deserialize<PlaylistSettings>(written, SubscriptionTransfer.Json)!;
        Assert.Equal(expectedOrder, back.Order);
        Assert.Equal(expectedGroup, back.GroupByPodcast);
    }
}
