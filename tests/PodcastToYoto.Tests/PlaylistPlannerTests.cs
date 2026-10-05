using PodcastToYoto.Web.Data;
using PodcastToYoto.Web.Sync;

namespace PodcastToYoto.Tests;

public class PlaylistPlannerTests
{
    private static readonly DateTimeOffset Day0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Episodes e1..eN, eN newest.</summary>
    private static List<Episode> Eps(int n) => Enumerable.Range(1, n).Select(i => new Episode
    {
        Id = i, Guid = $"g{i}", Title = $"e{i}", EnclosureUrl = $"https://x/{i}.mp3",
        PublishedAt = Day0.AddDays(i), FirstSeenAt = Day0,
    }).ToList();

    private static Subscription Sub(int recent = 3, int max = 100, PlaylistOrder order = PlaylistOrder.NewestFirst, bool trailers = false) =>
        new() { Name = "s", FeedUrl = "https://x", RecentCount = recent, MaxTracks = max, Order = order, IncludeTrailers = trailers };

    private static string[] Titles(PlannedPlaylist p) => p.Episodes.Select(e => e.Title).ToArray();

    [Fact]
    public void Takes_newest_n_newest_first()
    {
        var p = PlaylistPlanner.Plan(Sub(recent: 3), Eps(10));
        Assert.Equal(new[] { "e10", "e9", "e8" }, Titles(p));
        Assert.Empty(p.Warnings);
    }

    [Fact]
    public void Oldest_first_reverses()
    {
        var p = PlaylistPlanner.Plan(Sub(recent: 3, order: PlaylistOrder.OldestFirst), Eps(10));
        Assert.Equal(new[] { "e8", "e9", "e10" }, Titles(p));
    }

    [Fact]
    public void Favourites_are_added_on_top_of_recent_in_date_order()
    {
        var eps = Eps(10);
        eps[1].IsFavourite = true;  // e2
        eps[8].IsFavourite = true;  // e9 (already recent)

        var p = PlaylistPlanner.Plan(Sub(recent: 3), eps);
        Assert.Equal(new[] { "e10", "e9", "e8", "e2" }, Titles(p));
    }

    [Fact]
    public void Trailers_excluded_unless_enabled()
    {
        var eps = Eps(5);
        eps[4].EpisodeType = "trailer"; // e5

        Assert.Equal(new[] { "e4", "e3" }, Titles(PlaylistPlanner.Plan(Sub(recent: 2), eps)));
        Assert.Equal(new[] { "e5", "e4" }, Titles(PlaylistPlanner.Plan(Sub(recent: 2, trailers: true), eps)));
    }

    [Fact]
    public void Episodes_missing_from_feed_only_included_as_favourites()
    {
        var eps = Eps(5);
        eps[4].MissingFromFeedSince = Day0; // e5 gone
        eps[3].MissingFromFeedSince = Day0; // e4 gone but favourite
        eps[3].IsFavourite = true;

        var p = PlaylistPlanner.Plan(Sub(recent: 2), eps);
        Assert.Equal(new[] { "e4", "e3", "e2" }, Titles(p));
    }

    [Fact]
    public void Over_limit_drops_oldest_non_favourites_first()
    {
        var eps = Eps(10);
        eps[0].IsFavourite = true; // e1

        var p = PlaylistPlanner.Plan(Sub(recent: 5, max: 4), eps);
        Assert.Equal(new[] { "e10", "e9", "e8", "e1" }, Titles(p));
        Assert.Single(p.Warnings);
    }

    [Fact]
    public void Too_many_favourites_drops_oldest_favourites()
    {
        var eps = Eps(6);
        foreach (var e in eps) e.IsFavourite = true;

        var p = PlaylistPlanner.Plan(Sub(recent: 1, max: 3), eps);
        Assert.Equal(new[] { "e6", "e5", "e4" }, Titles(p));
        Assert.Contains("favourites", p.Warnings.Single());
    }
}
