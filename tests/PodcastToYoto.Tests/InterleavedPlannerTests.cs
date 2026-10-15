using PodcastToYoto.Web.Data;
using PodcastToYoto.Web.Sync;

namespace PodcastToYoto.Tests;

public class InterleavedPlannerTests
{
    private static readonly DateTimeOffset Day0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static int _nextId = 5000;

    /// <summary>A podcast whose episodes are named {prefix}{day} and published on those days.</summary>
    private static Subscription Pod(int id, string prefix, int[] days, int position, PlaylistOrder? ownOrder = null, bool enabled = true)
    {
        var s = new Subscription
        {
            Id = id, Name = prefix, FeedUrl = $"https://x/{prefix}", Position = position, Enabled = enabled,
            Order = ownOrder ?? PlaylistOrder.NewestFirst, UseOwnOrder = ownOrder is not null,
        };
        s.Episodes.AddRange(days.Select(d => new Episode
        {
            Id = Interlocked.Increment(ref _nextId), SubscriptionId = id, Guid = $"{prefix}{d}", Title = $"{prefix}{d}",
            EnclosureUrl = $"https://x/{prefix}{d}.mp3", PublishedAt = Day0.AddDays(d), FirstSeenAt = Day0,
        }));
        return s;
    }

    /// <summary>A Yoto playlist source whose picked tracks are the titles, in that track order.</summary>
    private static Subscription Yoto(int id, int position, PlaylistOrder order, params string[] titles)
    {
        var s = new Subscription
        {
            Id = id, Kind = SourceKind.YotoPlaylist, SourceCardId = $"card{id}", Name = $"yoto{id}",
            FeedUrl = $"https://my.yotoplay.com/cards/card{id}", Position = position, RecentCount = 0, Order = order, UseOwnOrder = true,
        };
        for (var i = 0; i < titles.Length; i++)
        {
            s.Episodes.Add(new Episode
            {
                Id = Interlocked.Increment(ref _nextId), SubscriptionId = id, Guid = $"yoto:{titles[i]}", Title = titles[i],
                EnclosureUrl = $"yoto:#{titles[i]}", SourceIndex = i, FirstSeenAt = Day0, IsFavourite = true,
            });
        }
        return s;
    }

    private static Playlist List(int max = 100, bool grouped = false) =>
        new() { Name = "p", MaxTracks = max, Order = PlaylistOrder.Interleaved, GroupByPodcast = grouped };

    private static string[] Titles(PlannedPlaylist p) => p.Episodes.Select(e => e.Title).ToArray();

    [Fact]
    public void Takes_one_from_each_source_in_turn_and_carries_on_when_one_runs_out()
    {
        var p = PlaylistPlanner.Plan(List(), [Pod(1, "a", [1, 4, 7], 0), Pod(2, "b", [2, 5], 1), Pod(3, "c", [3, 6, 8, 9], 2)]);
        Assert.Equal(new[] { "a7", "b5", "c9", "a4", "b2", "c8", "a1", "c6", "c3" }, Titles(p));
        Assert.Empty(p.Warnings);
    }

    [Fact]
    public void Rotation_follows_source_position_and_skips_excluded_sources()
    {
        var p = PlaylistPlanner.Plan(List(), [Pod(1, "a", [1, 4], 2), Pod(2, "b", [2, 5], 0), Pod(3, "c", [3, 6], 1, enabled: false)]);
        Assert.Equal(new[] { "b5", "a4", "b2", "a1" }, Titles(p));
    }

    [Fact]
    public void Each_podcast_uses_its_own_order_and_defaults_to_newest_first()
    {
        var p = PlaylistPlanner.Plan(List(), [Pod(1, "a", [1, 4, 7], 0), Pod(2, "b", [2, 5, 8], 1, ownOrder: PlaylistOrder.OldestFirst)]);
        Assert.Equal(new[] { "a7", "b2", "a4", "b5", "a1", "b8" }, Titles(p));
    }

    [Fact]
    public void Yoto_playlist_sources_take_turns_in_their_own_order()
    {
        var p = PlaylistPlanner.Plan(List(), [Pod(1, "a", [1, 4], 0), Yoto(2, 1, PlaylistOrder.OldestFirst, "t1", "t2", "t3")]);
        Assert.Equal(new[] { "a4", "t3", "a1", "t2", "t1" }, Titles(p));
    }

    [Fact]
    public void Grouping_is_ignored()
    {
        var subs = new[] { Pod(1, "a", [1, 4], 0), Pod(2, "b", [2, 5], 1) };
        Assert.Equal(Titles(PlaylistPlanner.Plan(List(), subs)), Titles(PlaylistPlanner.Plan(List(grouped: true), subs)));
    }

    [Fact]
    public void Playlist_cap_drops_the_oldest_before_interleaving()
    {
        var p = PlaylistPlanner.Plan(List(max: 4), [Pod(1, "a", [1, 2, 3], 0), Pod(2, "b", [4, 5, 6], 1)]);
        Assert.Equal(new[] { "a3", "b6", "b5", "b4" }, Titles(p));
        Assert.Single(p.Warnings);
    }
}
