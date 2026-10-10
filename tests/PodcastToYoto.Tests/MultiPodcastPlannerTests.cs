using PodcastToYoto.Web.Data;
using PodcastToYoto.Web.Sync;

namespace PodcastToYoto.Tests;

public class MultiPodcastPlannerTests
{
    private static readonly DateTimeOffset Day0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static int _nextId;

    /// <summary>A podcast whose episodes are named {prefix}{day} and published on those days.</summary>
    private static Subscription Pod(int id, string prefix, int[] days, int recent = 25, int max = 100,
        PlaylistOrder order = PlaylistOrder.NewestFirst, int position = 0, bool enabled = true)
    {
        var s = new Subscription
        {
            Id = id, Name = prefix, FeedUrl = $"https://x/{prefix}", RecentCount = recent, MaxTracks = max,
            Order = order, Position = position, Enabled = enabled,
        };
        s.Episodes.AddRange(days.Select(d => new Episode
        {
            Id = Interlocked.Increment(ref _nextId), SubscriptionId = id, Guid = $"{prefix}{d}", Title = $"{prefix}{d}",
            EnclosureUrl = $"https://x/{prefix}{d}.mp3", PublishedAt = Day0.AddDays(d), FirstSeenAt = Day0,
        }));
        return s;
    }

    private static Playlist List(int max = 100, TrackOrder order = TrackOrder.NewestFirst) =>
        new() { Name = "p", MaxTracks = max, Order = order };

    private static string[] Titles(PlannedPlaylist p) => p.Episodes.Select(e => e.Title).ToArray();

    [Theory]
    [InlineData(PlaylistOrder.NewestFirst, TrackOrder.NewestFirst)]
    [InlineData(PlaylistOrder.OldestFirst, TrackOrder.OldestFirst)]
    public void Single_podcast_playlist_matches_the_podcast_plan(PlaylistOrder podcastOrder, TrackOrder playlistOrder)
    {
        // Migrated one-podcast playlists must publish exactly what the old subscription did, or every card republishes.
        var pod = Pod(1, "a", [1, 2, 3, 3, 4, 5, 6, 7], recent: 4, max: 5, order: podcastOrder);
        pod.Episodes[0].IsFavourite = true;
        pod.Episodes[1].IsFavourite = true;

        var old = PlaylistPlanner.Plan(pod, pod.Episodes);
        var now = PlaylistPlanner.Plan(List(max: 5, order: playlistOrder), [pod]);

        Assert.Equal(old.Episodes.Select(e => e.Id), now.Episodes.Select(e => e.Id));
    }

    [Fact]
    public void By_date_interleaves_podcasts()
    {
        var p = PlaylistPlanner.Plan(List(), [Pod(1, "a", [1, 4]), Pod(2, "b", [2, 3], position: 1)]);
        Assert.Equal(new[] { "a4", "b3", "b2", "a1" }, Titles(p));

        var oldest = PlaylistPlanner.Plan(List(order: TrackOrder.OldestFirst), [Pod(1, "a", [1, 4]), Pod(2, "b", [2, 3], position: 1)]);
        Assert.Equal(new[] { "a1", "b2", "b3", "a4" }, Titles(oldest));
    }

    [Fact]
    public void Grouped_follows_podcast_order_and_each_podcasts_own_order()
    {
        var a = Pod(1, "a", [1, 4], position: 1);
        var b = Pod(2, "b", [2, 3], position: 0, order: PlaylistOrder.OldestFirst);

        var p = PlaylistPlanner.Plan(List(order: TrackOrder.GroupedByPodcast), [a, b]);
        Assert.Equal(new[] { "b2", "b3", "a4", "a1" }, Titles(p));
    }

    [Fact]
    public void Each_podcast_keeps_its_own_limits()
    {
        var p = PlaylistPlanner.Plan(List(), [Pod(1, "a", [1, 2, 3, 4], recent: 1), Pod(2, "b", [1, 2, 3, 4], recent: 3, max: 2, position: 1)]);
        Assert.Equal(new[] { "a4", "b4", "b3" }, Titles(p));
        Assert.Contains(p.Warnings, w => w.StartsWith("b: Capped at 2"));
    }

    [Fact]
    public void Playlist_limit_drops_oldest_non_favourites_across_podcasts()
    {
        var a = Pod(1, "a", [1, 5]);
        var b = Pod(2, "b", [2, 3], position: 1);
        b.Episodes[0].IsFavourite = true; // b2

        var p = PlaylistPlanner.Plan(List(max: 3, order: TrackOrder.GroupedByPodcast), [a, b]);
        Assert.Equal(new[] { "a5", "b3", "b2" }, Titles(p));
        Assert.Contains(p.Warnings, w => w.StartsWith("Playlist trimmed to 3"));
    }

    [Fact]
    public void Excluded_podcasts_are_left_out()
    {
        var p = PlaylistPlanner.Plan(List(), [Pod(1, "a", [1]), Pod(2, "b", [2], enabled: false)]);
        Assert.Equal(new[] { "a1" }, Titles(p));
    }

    [Fact]
    public void Empty_playlist_warns()
    {
        var p = PlaylistPlanner.Plan(List(), []);
        Assert.Empty(p.Episodes);
        Assert.Single(p.Warnings);
    }
}
