using System.Text.Json;
using PodcastToYoto.Web.Data;
using PodcastToYoto.Web.Subscriptions;
using PodcastToYoto.Web.Sync;
using PodcastToYoto.Yoto;

namespace PodcastToYoto.Tests;

public class YotoPlaylistSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);

    // Shape of GET /content/{cardId} for a playlist built in the Yoto app (trimmed from a real response).
    private const string CardJson = """
    {
      "card": {
        "cardId": "src01",
        "title": "Peppa Pig Storytime",
        "content": {
          "chapters": [
            { "key": "00", "title": "Peppa Pig Stories - America", "display": { "icon16x16": "yoto:#iconA" },
              "tracks": [ { "key": "01", "title": "Peppa Pig Stories - America", "trackUrl": "yoto:#shaA", "type": "audio",
                            "format": "opus", "duration": 289, "fileSize": 3536466, "display": { "icon16x16": "yoto:#iconA" } } ] },
            { "key": "01", "title": "Chapter title only", "display": { "icon16x16": "yoto:#iconB" },
              "tracks": [ { "key": "01", "trackUrl": "yoto:#shaB", "type": "audio", "format": "opus", "channels": "stereo",
                            "duration": 293, "fileSize": 3440981, "ambient": null } ] },
            { "key": "02", "title": "Radio",
              "tracks": [ { "key": "01", "title": "Radio", "trackUrl": "https://example.com/live.mp3", "type": "stream", "format": "mp3" } ] }
          ]
        },
        "metadata": { "cover": { "imageL": "https://example.com/cover.png" } }
      }
    }
    """;

    private static List<YotoCardTrack> Tracks()
    {
        using var doc = JsonDocument.Parse(CardJson);
        return YotoCardReader.Tracks(YotoCardReader.Card(doc)).ToList();
    }

    [Fact]
    public void Reads_tracks_in_order_with_chapter_fallbacks()
    {
        var t = Tracks();
        Assert.Equal(3, t.Count);

        Assert.Equal(("Peppa Pig Stories - America", "shaA", 289L, "yoto:#iconA", "\"opus\""), (t[0].Title, t[0].Sha, t[0].Duration, t[0].IconRef, t[0].FormatJson));
        Assert.Null(t[0].ChannelsJson);

        Assert.Equal(("Chapter title only", "shaB", "yoto:#iconB", "\"stereo\""), (t[1].Title, t[1].Sha, t[1].IconRef, t[1].ChannelsJson));

        Assert.Equal("stream", t[2].Type);
        Assert.Null(t[2].Sha);
    }

    [Fact]
    public void Reads_title_and_cover()
    {
        using var doc = JsonDocument.Parse(CardJson);
        var card = YotoCardReader.Card(doc);
        Assert.Equal("Peppa Pig Storytime", YotoCardReader.Title(card));
        Assert.Equal("https://example.com/cover.png", YotoCardReader.CoverUrl(card));
    }

    private static Subscription Source(int first = 0, int max = 100, int position = 0, PlaylistOrder order = PlaylistOrder.NewestFirst, int id = 9) => new()
    {
        Id = id, Kind = SourceKind.YotoPlaylist, SourceCardId = "src01", Name = "Peppa", FeedUrl = "https://my.yotoplay.com/cards/src01",
        RecentCount = first, MaxTracks = max, Position = position, Order = order, UseOwnOrder = true,
        UseDefaultIcon = true,
    };

    [Fact]
    public void Merge_adds_uploaded_tracks_and_skips_streams()
    {
        var s = Source();
        var (merge, streams) = YotoTrackMerger.Merge(s, Tracks(), Now);

        Assert.Equal(new MergeResult(2, 0, 0, 0), merge);
        Assert.Equal(1, streams);
        Assert.Equal(["yoto:shaA", "yoto:shaB"], s.Episodes.Select(e => e.Guid));
        Assert.Equal([0, 1], s.Episodes.Select(e => e.SourceIndex ?? -1));
        Assert.All(s.Episodes, e => Assert.Null(e.PublishedAt));
        Assert.All(s.Episodes, e => Assert.False(e.IsFavourite)); // new tracks are never picked automatically
        Assert.Equal("yoto:#shaA", s.Episodes[0].EnclosureUrl);
        Assert.Equal("yoto:#iconA", s.Episodes[0].SourceIconRef);
    }

    [Fact]
    public void Merge_follows_renames_reorders_and_removals()
    {
        var s = Source();
        YotoTrackMerger.Merge(s, Tracks(), Now);
        s.Episodes[0].IsFavourite = true;

        // B moved first and renamed, A removed.
        var later = new[] { Tracks()[1] with { Title = "Renamed" } };
        var (merge, _) = YotoTrackMerger.Merge(s, later, Now.AddDays(1));

        Assert.Equal(new MergeResult(0, 1, 1, 0), merge);
        var a = s.Episodes.Single(e => e.Guid == "yoto:shaA");
        var b = s.Episodes.Single(e => e.Guid == "yoto:shaB");
        Assert.Equal(Now.AddDays(1), a.MissingFromFeedSince);
        Assert.True(a.IsFavourite);
        Assert.Equal(("Renamed", 0), (b.Title, b.SourceIndex ?? -1));

        // A back again.
        (merge, _) = YotoTrackMerger.Merge(s, Tracks(), Now.AddDays(2));
        Assert.Equal(1, merge.Reappeared);
        Assert.Null(a.MissingFromFeedSince);
    }

    private static int _nextId = 1000;

    private static Subscription WithTracks(Subscription s, params string[] titles)
    {
        for (var i = 0; i < titles.Length; i++)
        {
            s.Episodes.Add(new Episode
            {
                Id = Interlocked.Increment(ref _nextId), SubscriptionId = s.Id, Guid = $"yoto:{titles[i]}", Title = titles[i],
                EnclosureUrl = $"yoto:#{titles[i]}", SourceIndex = i, FirstSeenAt = Now,
            });
        }
        return s;
    }

    private static Subscription Pod(int id, string prefix, int[] days, int position)
    {
        var s = new Subscription { Id = id, Name = prefix, FeedUrl = $"https://x/{prefix}", Position = position };
        s.Episodes.AddRange(days.Select(d => new Episode
        {
            Id = Interlocked.Increment(ref _nextId), SubscriptionId = id, Guid = $"{prefix}{d}", Title = $"{prefix}{d}",
            EnclosureUrl = $"https://x/{prefix}{d}.mp3", PublishedAt = Now.AddDays(d), FirstSeenAt = Now,
        }));
        return s;
    }

    private static string[] Titles(PlannedPlaylist p) => p.Episodes.Select(e => e.Title).ToArray();

    [Fact]
    public void Picked_tracks_plus_the_first_n_in_its_order()
    {
        var s = WithTracks(Source(), "t1", "t2", "t3", "t4");
        s.Episodes[2].IsFavourite = true;
        var list = new Playlist { Name = "p" };

        Assert.Equal(["t3"], Titles(PlaylistPlanner.Plan(list, [s])));

        s.RecentCount = 2;
        Assert.Equal(["t1", "t2", "t3"], Titles(PlaylistPlanner.Plan(list, [s])));

        // Reversed: the first N are the last tracks.
        s.Order = PlaylistOrder.OldestFirst;
        Assert.Equal(["t4", "t3"], Titles(PlaylistPlanner.Plan(list, [s])));

        s.Order = PlaylistOrder.TitleDescending;
        s.RecentCount = 1;
        Assert.Equal(["t4", "t3"], Titles(PlaylistPlanner.Plan(list, [s])));
    }

    [Fact]
    public void Removed_tracks_drop_out_of_the_first_n_but_stay_when_picked()
    {
        var s = WithTracks(Source(first: 2), "t1", "t2", "t3");
        s.Episodes[0].MissingFromFeedSince = Now;
        Assert.Equal(["t2", "t3"], Titles(PlaylistPlanner.Plan(new Playlist { Name = "p" }, [s])));

        s.Episodes[0].IsFavourite = true;
        Assert.Equal(["t1", "t2", "t3"], Titles(PlaylistPlanner.Plan(new Playlist { Name = "p" }, [s])));
    }

    [Fact]
    public void Ungrouped_date_order_keeps_the_source_together_in_its_own_order()
    {
        var src = WithTracks(Source(first: 10, position: 0), "t1", "t2");
        var a = Pod(1, "a", [1, 4], position: 1);
        var b = Pod(2, "b", [2, 3], position: 2);

        Assert.Equal(["t1", "t2", "a4", "b3", "b2", "a1"], Titles(PlaylistPlanner.Plan(new Playlist { Name = "p" }, [src, a, b])));

        src.Position = 3;
        Assert.Equal(["a1", "b2", "b3", "a4", "t1", "t2"],
            Titles(PlaylistPlanner.Plan(new Playlist { Name = "p", Order = PlaylistOrder.OldestFirst }, [src, a, b])));

        src.Order = PlaylistOrder.OldestFirst;
        Assert.Equal(["a4", "b3", "b2", "a1", "t2", "t1"], Titles(PlaylistPlanner.Plan(new Playlist { Name = "p" }, [src, a, b])));
    }

    [Fact]
    public void Grouped_uses_its_own_order_and_title_orders_mix_in()
    {
        var src = WithTracks(Source(first: 10, position: 1), "b-track", "a-track");
        var pod = Pod(1, "c", [1, 2], position: 0);

        Assert.Equal(["c2", "c1", "b-track", "a-track"],
            Titles(PlaylistPlanner.Plan(new Playlist { Name = "p", GroupByPodcast = true }, [src, pod])));

        // The playlist's order doesn't reorder a Yoto source's group; its own order does.
        Assert.Equal(["c1", "c2", "b-track", "a-track"],
            Titles(PlaylistPlanner.Plan(new Playlist { Name = "p", GroupByPodcast = true, Order = PlaylistOrder.OldestFirst }, [src, pod])));
        src.Order = PlaylistOrder.TitleAscending;
        Assert.Equal(["c2", "c1", "a-track", "b-track"],
            Titles(PlaylistPlanner.Plan(new Playlist { Name = "p", GroupByPodcast = true }, [src, pod])));
        src.Order = PlaylistOrder.NewestFirst;

        Assert.Equal(["a-track", "b-track", "c1", "c2"],
            Titles(PlaylistPlanner.Plan(new Playlist { Name = "p", Order = PlaylistOrder.TitleAscending }, [src, pod])));
    }

    [Fact]
    public void Caps_drop_the_last_tracks_in_its_order()
    {
        var src = WithTracks(Source(first: 10, max: 2), "t1", "t2", "t3");
        Assert.Equal(["t1", "t2"], Titles(PlaylistPlanner.Plan(new Playlist { Name = "p" }, [src])));

        src.Episodes[2].IsFavourite = true; // non-picked tracks go first
        Assert.Equal(["t1", "t3"], Titles(PlaylistPlanner.Plan(new Playlist { Name = "p" }, [src])));

        src.MaxTracks = 100;
        src.Episodes[2].IsFavourite = false;
        Assert.Equal(["t1", "t2"], Titles(PlaylistPlanner.Plan(new Playlist { Name = "p", MaxTracks = 2 }, [src])));
    }

    [Fact]
    public void Source_icon_is_the_default_and_hand_picked_wins()
    {
        var src = Source();
        var e = new Episode { Guid = "yoto:x", Title = "x", EnclosureUrl = "yoto:#x", SourceIconRef = "yoto:#iconA" };
        Assert.Equal("yoto:#iconA", SyncEngine.IconFor(e, src, null));

        e.IconMediaId = "mine";
        Assert.Equal("yoto:#mine", SyncEngine.IconFor(e, src, null));

        e.IconMediaId = Episode.NoIcon;
        Assert.Null(SyncEngine.IconFor(e, src, null));
    }
}
