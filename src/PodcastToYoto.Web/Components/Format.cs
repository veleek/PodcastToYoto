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

    public static string Ago(DateTimeOffset? value, DateTimeOffset now)
    {
        if (value is null) return "never";
        var d = now - value.Value;
        return d.TotalSeconds < 60 ? "just now"
            : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes} min ago"
            : d.TotalHours < 48 ? $"{(int)d.TotalHours} h ago"
            : $"{(int)d.TotalDays} days ago";
    }
}
