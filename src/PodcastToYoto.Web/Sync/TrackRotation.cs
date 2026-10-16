using PodcastToYoto.Web.Data;

namespace PodcastToYoto.Web.Sync;

/// <summary>
/// Weekly rotation for a Yoto playlist source (<see cref="TrackSelection.RotateWeekly"/>). Each week puts on the
/// <see cref="Subscription.RecentCount"/> tracks that have been off the playlist longest (never-used ones first, then by
/// the rotation that last used them; ties in the source's order), so it works through the whole source without repeats.
/// Favourites are always on and aren't rotated. The picks only change when a week is up, so every sync and dry run in a
/// week sees the same tracks. Pure: a sync records a new week with <see cref="Record"/>.
/// </summary>
public static class TrackRotation
{
    public static readonly TimeSpan Period = TimeSpan.FromDays(7);

    /// <summary>A source's rotation for the week containing "now". <see cref="IsNew"/> = not recorded yet.</summary>
    public sealed record Week(Subscription Source, int Number, DateTimeOffset StartedAt, IReadOnlyList<Episode> Tracks, bool IsNew)
    {
        public DateTimeOffset NextChangeAt => StartedAt + Period;
    }

    /// <param name="ordered">The source's tracks, in its order.</param>
    public static Week Current(Subscription s, IReadOnlyList<Episode> ordered, DateTimeOffset now)
    {
        var candidates = ordered.Where(e => e.MissingFromFeedSince is null && !e.IsFavourite).ToList();
        if (s.RotationNumber > 0 && s.RotationStartedAt is { } start && now < start + Period)
            return new Week(s, s.RotationNumber, start, candidates.Where(e => e.RotationNumber == s.RotationNumber).ToList(), IsNew: false);

        // Weeks keep to the first week's weekday and time, however late the sync that starts one runs.
        var newStart = s.RotationNumber > 0 && s.RotationStartedAt is { } last
            ? last + TimeSpan.FromTicks((now - last).Ticks / Period.Ticks * Period.Ticks)
            : now;
        var picked = new HashSet<Episode>(
            candidates.OrderBy(e => e.RotationNumber ?? 0).Take(Math.Max(0, s.RecentCount)), // stable: ties stay in source order
            ReferenceEqualityComparer.Instance);
        return new Week(s, s.RotationNumber + 1, newStart, candidates.Where(picked.Contains).ToList(), IsNew: true);
    }

    /// <summary>
    /// Makes the next sync pick the next tracks now, without moving the weekly changeover: the current week is pushed back
    /// a week, so the new one starts at the current week's start. False if no week has been picked yet.
    /// </summary>
    public static bool RotateEarly(Subscription s)
    {
        if (s.RotationNumber == 0 || s.RotationStartedAt is not { } start) return false;
        s.RotationStartedAt = start - Period;
        return true;
    }

    /// <summary>
    /// Records new weeks after a sync. Only tracks that made it onto the playlist (past the caps) count as used, so the
    /// week's picks stay the same until it's up.
    /// </summary>
    public static void Record(IEnumerable<Week> weeks, IEnumerable<Episode> planned)
    {
        var onPlaylist = new HashSet<Episode>(planned, ReferenceEqualityComparer.Instance);
        foreach (var w in weeks.Where(w => w.IsNew))
        {
            w.Source.RotationNumber = w.Number;
            w.Source.RotationStartedAt = w.StartedAt;
            foreach (var e in w.Tracks.Where(onPlaylist.Contains)) e.RotationNumber = w.Number;
        }
    }
}
