namespace PodcastToYoto.Web.Components;

public static class Format
{
    public static string Local(DateTimeOffset? value, string format = "yyyy-MM-dd HH:mm") =>
        value is null ? "—" : value.Value.ToLocalTime().ToString(format);

    public static string Date(DateTimeOffset? value) => Local(value, "yyyy-MM-dd");

    public static string Duration(long? seconds)
    {
        if (seconds is null) return "—";
        var t = TimeSpan.FromSeconds(seconds.Value);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t:mm\\:ss}" : t.ToString(@"m\:ss");
    }

    public static string Interval(TimeSpan t) =>
        t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes} min"
        : t.Minutes == 0 ? $"{(int)t.TotalHours} h"
        : $"{(int)t.TotalHours} h {t.Minutes} min";

    public static string Until(DateTimeOffset? value, DateTimeOffset now)
    {
        if (value is null) return "—";
        var d = value.Value - now;
        return d.TotalMinutes < 1 ? "due now"
            : d.TotalMinutes < 60 ? $"in {(int)Math.Ceiling(d.TotalMinutes)} min"
            : d.TotalHours < 48 ? $"in {(int)d.TotalHours} h {d.Minutes} min"
            : $"in {(int)d.TotalDays} days";
    }

    /// <summary>When a weekly rotation next picks new tracks: "new tracks Mon 20 Oct 07:00 (in 6 days)".</summary>
    public static string NextRotation(PodcastToYoto.Web.Sync.TrackRotation.Week week, DateTimeOffset now) =>
        week.IsNew ? "new tracks picked on the next sync"
        : $"new tracks {Local(week.NextChangeAt, "ddd d MMM HH:mm")} ({Until(week.NextChangeAt, now)})";

    /// <summary>"NoChange" → "No change", "DryRun" → "Dry run".</summary>
    public static string Words(Enum value)
    {
        var name = value.ToString();
        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i > 0 && char.IsUpper(c)) sb.Append(' ').Append(char.ToLowerInvariant(c));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    public static string Result(PodcastToYoto.Web.Data.SyncResult r) => Words(r);

    public static string Ago(DateTimeOffset? value, DateTimeOffset now)
    {
        if (value is null) return "never";
        var d = now - value.Value;
        return d.TotalSeconds < 60 ? "just now"
            : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes} min ago"
            : d.TotalHours < 48 ? $"{(int)d.TotalHours} h ago"
            : $"{(int)d.TotalDays} days ago";
    }

    /// <summary>A Yoto playlist source's order: the date orders mean its track order.</summary>
    public static string TrackOrder(PodcastToYoto.Web.Data.PlaylistOrder order) => order switch
    {
        PodcastToYoto.Web.Data.PlaylistOrder.OldestFirst => "reverse track order",
        PodcastToYoto.Web.Data.PlaylistOrder.TitleAscending => "A–Z order",
        PodcastToYoto.Web.Data.PlaylistOrder.TitleDescending => "Z–A order",
        _ => "track order",
    };

    public static string Order(PodcastToYoto.Web.Data.PlaylistOrder order) => order switch
    {
        PodcastToYoto.Web.Data.PlaylistOrder.OldestFirst => "oldest first",
        PodcastToYoto.Web.Data.PlaylistOrder.TitleAscending => "A–Z",
        PodcastToYoto.Web.Data.PlaylistOrder.TitleDescending => "Z–A",
        PodcastToYoto.Web.Data.PlaylistOrder.Interleaved => "one from each source in turn",
        _ => "newest first",
    };
}
