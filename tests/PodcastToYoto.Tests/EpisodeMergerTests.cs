using PodcastToYoto.Feeds;
using PodcastToYoto.Web.Data;
using PodcastToYoto.Web.Subscriptions;

namespace PodcastToYoto.Tests;

public class EpisodeMergerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddHours(1);

    private static PodcastEpisode Fe(string guid, string title = "t", string url = "https://x/a.mp3", int? seconds = 900) =>
        new(guid, title, T0, url, "audio/mpeg", 1000, seconds is null ? null : TimeSpan.FromSeconds(seconds.Value), "full", null, null);

    private static Subscription Sub(params Episode[] eps)
    {
        var s = new Subscription { Name = "s", FeedUrl = "https://x/feed" };
        s.Episodes.AddRange(eps);
        return s;
    }

    private static Episode Ep(string guid, string title = "t") => new()
    {
        Guid = guid, Title = title, PublishedAt = T0, EnclosureUrl = "https://x/a.mp3", EnclosureLength = 1000,
        DurationSeconds = 900, EpisodeType = "full", FirstSeenAt = T0,
    };

    [Fact]
    public void Adds_new_episodes()
    {
        var s = Sub();
        var r = EpisodeMerger.Merge(s, [Fe("a"), Fe("b")], T1);

        Assert.Equal(new MergeResult(2, 0, 0, 0), r);
        Assert.All(s.Episodes, e => Assert.Equal(T1, e.FirstSeenAt));
        Assert.Equal(900, s.Episodes[0].DurationSeconds);
    }

    [Fact]
    public void Unchanged_episode_is_not_counted_as_updated()
    {
        var s = Sub(Ep("a"));
        var r = EpisodeMerger.Merge(s, [Fe("a")], T1);

        Assert.Equal(MergeResult.Empty, r);
    }

    [Fact]
    public void Updates_changed_fields_but_keeps_favourite_and_audio()
    {
        var e = Ep("a", "old");
        e.IsFavourite = true;
        e.AudioId = 7;
        var s = Sub(e);

        var r = EpisodeMerger.Merge(s, [Fe("a", "new", "https://x/b.mp3")], T1);

        Assert.Equal(1, r.Updated);
        Assert.Equal("new", e.Title);
        Assert.Equal("https://x/b.mp3", e.EnclosureUrl);
        Assert.True(e.IsFavourite);
        Assert.Equal(7, e.AudioId);
    }

    [Fact]
    public void Marks_missing_once_and_clears_when_back()
    {
        var e = Ep("a");
        var s = Sub(e, Ep("b"));

        var r1 = EpisodeMerger.Merge(s, [Fe("b")], T1);
        Assert.Equal(1, r1.MarkedMissing);
        Assert.Equal(T1, e.MissingFromFeedSince);

        var r2 = EpisodeMerger.Merge(s, [Fe("b")], T1.AddHours(1));
        Assert.Equal(0, r2.MarkedMissing);
        Assert.Equal(T1, e.MissingFromFeedSince); // unchanged

        var r3 = EpisodeMerger.Merge(s, [Fe("a"), Fe("b")], T1.AddHours(2));
        Assert.Equal(1, r3.Reappeared);
        Assert.Null(e.MissingFromFeedSince);
        Assert.Equal(2, s.Episodes.Count);
    }

    [Fact]
    public void Duplicate_guids_in_feed_take_the_first()
    {
        var s = Sub();
        var r = EpisodeMerger.Merge(s, [Fe("a", "first"), Fe("a", "second")], T1);

        Assert.Equal(1, r.Added);
        Assert.Equal("first", Assert.Single(s.Episodes).Title);
    }
}
