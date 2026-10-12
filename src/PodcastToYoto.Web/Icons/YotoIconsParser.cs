using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace PodcastToYoto.Web.Icons;

/// <summary>One icon from a yotoicons.com results page.</summary>
public sealed record YotoIconsEntry(int Id, string? Title, string? Category, IReadOnlyList<string> Tags, string? Artist, int? Downloads);

/// <summary>A parsed results page. <paramref name="Total"/> is the site's "We've got N icons" count, when present.</summary>
public sealed record YotoIconsPage(int? Total, IReadOnlyList<YotoIconsEntry> Icons);

/// <summary>
/// Scrapes a yotoicons.com results page (<c>/icons?tag=…&amp;page=…</c>). The site has no API: each result is an
/// <c>&lt;img src="/static/uploads/{id}.png"&gt;</c> followed by "@artist", and clicking it calls a JS function whose
/// arguments carry the metadata (id, category, title, tags, artist, downloads). Parsing is deliberately forgiving:
/// if the onclick metadata isn't there, results still come back with their id and artist.
/// </summary>
public static partial class YotoIconsParser
{
    public const int PageSize = 25;

    [GeneratedRegex(@"(?:we've got|here are all)\s+([\d,]+)\s+(?:of our\s+)?icons?", RegexOptions.IgnoreCase)]
    private static partial Regex TotalRx();

    [GeneratedRegex(@"\sonclick\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.IgnoreCase)]
    private static partial Regex OnClickRx();

    /// <summary>JS literals in an argument list: '…', "…" or a number.</summary>
    [GeneratedRegex(@"'((?:\\.|[^'\\])*)'|""((?:\\.|[^""\\])*)""|(?<![\w.])(-?\d+)(?![\w.])")]
    private static partial Regex ArgRx();

    [GeneratedRegex(@"/static/uploads/(\d+)\.png", RegexOptions.IgnoreCase)]
    private static partial Regex ImageRx();

    [GeneratedRegex(@">\s*@([^<>]+?)\s*<")]
    private static partial Regex ArtistRx();

    public static YotoIconsPage Parse(string html)
    {
        var decodedText = WebUtility.HtmlDecode(html);
        var totalMatch = TotalRx().Match(decodedText);
        int? total = totalMatch.Success && int.TryParse(totalMatch.Groups[1].Value.Replace(",", ""), out var t) ? t : null;

        var byId = new Dictionary<int, YotoIconsEntry>();
        foreach (Match m in OnClickRx().Matches(html))
        {
            var js = WebUtility.HtmlDecode(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
            if (FromOnClick(js) is { } entry) byId.TryAdd(entry.Id, entry);
        }

        // Order and completeness come from the images; the onclick metadata fills in the details where it was found.
        var images = ImageRx().Matches(html);
        var icons = new List<YotoIconsEntry>();
        var seen = new HashSet<int>();
        for (var i = 0; i < images.Count; i++)
        {
            if (!int.TryParse(images[i].Groups[1].Value, out var id) || !seen.Add(id)) continue;
            if (byId.TryGetValue(id, out var known))
            {
                icons.Add(known);
                continue;
            }

            var start = images[i].Index + images[i].Length;
            var end = i + 1 < images.Count ? images[i + 1].Index : html.Length;
            var artist = ArtistRx().Match(html, start, end - start);
            icons.Add(new YotoIconsEntry(id, null, null, [], artist.Success ? WebUtility.HtmlDecode(artist.Groups[1].Value) : null, null));
        }

        // Metadata without an image (shouldn't happen, but don't lose it).
        icons.AddRange(byId.Values.Where(e => seen.Add(e.Id)));
        return new YotoIconsPage(total, icons);
    }

    /// <summary>
    /// <c>fn('12583', 'animals', 'Grannies Bingo', 'bingo granny', 'curiouscat', '26')</c> → entry. Null when the
    /// first argument isn't a numeric id (tabs, buttons and other handlers on the page).
    /// </summary>
    internal static YotoIconsEntry? FromOnClick(string js)
    {
        var open = js.IndexOf('(');
        if (open < 0) return null;
        var args = ArgRx().Matches(js, open)
            .Select(a => a.Groups[1].Success ? Unescape(a.Groups[1].Value)
                : a.Groups[2].Success ? Unescape(a.Groups[2].Value)
                : a.Groups[3].Value)
            .ToList();
        if (args.Count < 3 || !int.TryParse(args[0].Trim(), out var id) || id <= 0) return null;

        string? Arg(int i) => i < args.Count && !string.IsNullOrWhiteSpace(args[i]) ? args[i].Trim() : null;
        var tags = (Arg(3) ?? "")
            .Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new YotoIconsEntry(id, Arg(2), Arg(1), tags, Arg(4)?.TrimStart('@'),
            int.TryParse(Arg(5), out var downloads) ? downloads : null);
    }

    private static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 == s.Length) { sb.Append(s[i]); continue; }
            var c = s[++i];
            sb.Append(c switch { 'n' => ' ', 't' => ' ', _ => c });
        }
        return sb.ToString();
    }
}
