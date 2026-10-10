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
        Assert.Equal(TrackOrder.OldestFirst, p.Settings.Order);
        Assert.Equal(30, p.Settings.PollIntervalMinutes);
        Assert.Equal("ABC", p.YotoCardId);

        var pod = Assert.Single(p.Podcasts);
        Assert.Equal("Silly Stories", pod.Settings.Name);
        Assert.Equal(10, pod.Settings.RecentCount);
        Assert.False(pod.Settings.MatchIcons);
        Assert.Equal(["g1"], pod.Favourites);
        Assert.Equal("m2", pod.Icons["g2"]);
    }
}
