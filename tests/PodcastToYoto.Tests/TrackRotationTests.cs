using PodcastToYoto.Web.Data;
using PodcastToYoto.Web.Subscriptions;
using PodcastToYoto.Web.Sync;

namespace PodcastToYoto.Tests;

public class TrackRotationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 7, 0, 0, TimeSpan.Zero); // a Monday
    private static int _nextId = 5000;

    private static Subscription Source(int perWeek, params string[] titles)
    {
        var s = new Subscription
        {
            Id = 7, Kind = SourceKind.YotoPlaylist, SourceCardId = "src01", Name = "Peppa", FeedUrl = "https://my.yotoplay.com/cards/src01",
            RecentCount = perWeek, Selection = TrackSelection.RotateWeekly, UseOwnOrder = true, UseDefaultIcon = true,
        };
        for (var i = 0; i < titles.Length; i++) Add(s, titles[i]);
        return s;
    }

    private static void Add(Subscription s, string title) => s.Episodes.Add(new Episode
    {
        Id = Interlocked.Increment(ref _nextId), SubscriptionId = s.Id, Guid = $"yoto:{title}", Title = title,
        EnclosureUrl = $"yoto:#{title}", SourceIndex = s.Episodes.Count, FirstSeenAt = Now,
    });

    private static PlannedPlaylist Plan(Subscription s, DateTimeOffset at, Playlist? list = null) =>
        PlaylistPlanner.Plan(list ?? new Playlist { Name = "p" }, [s], at);

    /// <summary>What a sync does: plan, then record a new week.</summary>
    private static string[] Sync(Subscription s, DateTimeOffset at, Playlist? list = null)
    {
        var plan = Plan(s, at, list);
        TrackRotation.Record(plan.Rotations, plan.Episodes);
        return plan.Episodes.Select(e => e.Title).ToArray();
    }

    [Fact]
    public void Works_through_the_whole_source_without_repeats()
    {
        var s = Source(2, "t1", "t2", "t3", "t4", "t5");

        Assert.Equal(["t1", "t2"], Sync(s, Now));
        Assert.Equal(["t3", "t4"], Sync(s, Now.AddDays(7)));
        // t5 has never been on; t1 and t2 have been off longest. Shown in the source's order.
        Assert.Equal(["t1", "t5"], Sync(s, Now.AddDays(14)));
        Assert.Equal(["t2", "t3"], Sync(s, Now.AddDays(21)));
    }

    [Fact]
    public void Picks_stay_the_same_within_a_week()
    {
        var s = Source(2, "t1", "t2", "t3", "t4");

        // A dry run before the first sync previews the same picks, and records nothing.
        var preview = Plan(s, Now);
        Assert.True(Assert.Single(preview.Rotations).IsNew);
        Assert.Equal(0, s.RotationNumber);
        Assert.Null(s.RotationStartedAt);

        Assert.Equal(["t1", "t2"], Sync(s, Now.AddHours(1)));
        var week = Assert.Single(Plan(s, Now.AddDays(6)).Rotations);
        Assert.False(week.IsNew);
        Assert.Equal(Now.AddDays(7).AddHours(1), week.NextChangeAt);

        Assert.Equal(["t1", "t2"], Sync(s, Now.AddDays(3)));
        Assert.Equal(["t1", "t2"], Sync(s, Now.AddDays(7)));
        Assert.Equal(1, s.RotationNumber);

        Assert.Equal(["t3", "t4"], Sync(s, Now.AddDays(7).AddHours(1)));
    }

    [Fact]
    public void Weeks_keep_their_weekday_after_a_late_or_missed_sync()
    {
        var s = Source(1, "t1", "t2", "t3");
        Sync(s, Now);
        Sync(s, Now.AddDays(15).AddHours(5));
        Assert.Equal(Now.AddDays(14), s.RotationStartedAt);
        Assert.Equal(2, s.RotationNumber);
    }

    [Fact]
    public void Favourites_are_always_on_and_not_rotated()
    {
        var s = Source(1, "t1", "t2", "t3");
        s.Episodes[0].IsFavourite = true;

        Assert.Equal(["t1", "t2"], Sync(s, Now));
        Assert.Equal(["t1", "t3"], Sync(s, Now.AddDays(7)));
        Assert.Null(s.Episodes[0].RotationNumber);
    }

    [Fact]
    public void New_tracks_come_up_next_and_removed_ones_are_skipped()
    {
        var s = Source(1, "t1", "t2", "t3");
        Sync(s, Now);
        Add(s, "t4");
        s.Episodes[1].MissingFromFeedSince = Now; // t2

        Assert.Equal(["t3"], Sync(s, Now.AddDays(7))); // t3 is first in the source order of the never-used tracks
        Assert.Equal(["t4"], Sync(s, Now.AddDays(14)));
        Assert.Equal(["t1"], Sync(s, Now.AddDays(21)));
    }

    [Fact]
    public void Tracks_left_off_by_a_cap_are_not_used_up()
    {
        var s = Source(3, "t1", "t2", "t3", "t4");
        Assert.Equal(["t1", "t2"], Sync(s, Now, new Playlist { Name = "p", MaxTracks = 2 }));
        Assert.Null(s.Episodes[2].RotationNumber);

        // Same week: still the same two.
        Assert.Equal(["t1", "t2"], Sync(s, Now.AddDays(1)));
        Assert.Equal(["t1", "t3", "t4"], Sync(s, Now.AddDays(7)));
    }

    [Fact]
    public void Changing_the_count_or_mode_starts_a_new_week()
    {
        var s = Source(1, "t1", "t2", "t3");
        Sync(s, Now);

        var settings = SubscriptionSettings.From(s);
        settings.Name = "Peppa";
        settings.ApplyTo(s);
        Assert.False(Assert.Single(Plan(s, Now.AddDays(1)).Rotations).IsNew); // nothing changed

        settings.RecentCount = 2;
        settings.ApplyTo(s);
        Assert.Equal(["t2", "t3"], Sync(s, Now.AddDays(1)));
        Assert.Equal(Now.AddDays(1), s.RotationStartedAt);
    }

    [Fact]
    public void Rotating_early_picks_the_next_tracks_and_keeps_the_changeover()
    {
        var s = Source(1, "t1", "t2", "t3");
        Assert.False(TrackRotation.RotateEarly(s)); // nothing to rotate yet
        Sync(s, Now);

        Assert.True(TrackRotation.RotateEarly(s));
        Assert.True(Assert.Single(Plan(s, Now.AddDays(2)).Rotations).IsNew);
        Assert.Equal(["t2"], Sync(s, Now.AddDays(2)));
        Assert.Equal(Now, s.RotationStartedAt);
        Assert.Equal(["t2"], Sync(s, Now.AddDays(3)));
        Assert.Equal(["t3"], Sync(s, Now.AddDays(7)));
    }

    [Fact]
    public void First_n_ignores_the_rotation()
    {
        var s = Source(2, "t1", "t2", "t3");
        s.Selection = TrackSelection.First;
        Assert.Equal(["t1", "t2"], Sync(s, Now));
        Assert.Empty(Plan(s, Now).Rotations);
        Assert.Equal(["t1", "t2"], Sync(s, Now.AddDays(7)));
    }
}
