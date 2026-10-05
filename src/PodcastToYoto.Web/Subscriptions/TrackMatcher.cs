using System.Globalization;
using System.Text;

namespace PodcastToYoto.Web.Subscriptions;

/// <summary>Matches track titles from an existing (often hand-made) Yoto playlist to feed episodes.</summary>
public static class TrackMatcher
{
    private const int MinContainsLength = 6;

    /// <summary>Lower-case letters and digits only; strips emoji, punctuation, whitespace and diacritics.</summary>
    public static string Normalize(string? title)
    {
        if (string.IsNullOrEmpty(title)) return "";
        var sb = new StringBuilder(title.Length);
        foreach (var ch in title.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Best episode for a track title: exact normalised match first, otherwise the longest episode title contained in
    /// the track title (handles "Silly Stories - Get a Job.mp3"-style names). Returns null if nothing plausible matches.
    /// </summary>
    public static T? Match<T>(string? trackTitle, IReadOnlyList<(string Normalized, T Item)> episodes) where T : class
    {
        var t = Normalize(trackTitle);
        if (t.Length == 0) return null;

        foreach (var (n, item) in episodes)
            if (n == t) return item;

        T? best = null;
        var bestLen = 0;
        foreach (var (n, item) in episodes)
        {
            if (n.Length >= MinContainsLength && n.Length > bestLen && t.Contains(n, StringComparison.Ordinal))
            {
                best = item;
                bestLen = n.Length;
            }
        }
        return best;
    }
}
