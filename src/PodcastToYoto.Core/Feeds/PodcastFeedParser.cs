using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PodcastToYoto.Feeds;

/// <summary>Minimal RSS 2.0 + iTunes namespace parser for podcast feeds.</summary>
public static partial class PodcastFeedParser
{
    private static readonly XNamespace Itunes = "http://www.itunes.com/dtds/podcast-1.0.dtd";

    public static PodcastFeed Parse(Stream xml)
    {
        var doc = XDocument.Load(xml);
        return Parse(doc);
    }

    public static PodcastFeed Parse(XDocument doc)
    {
        var channel = doc.Root?.Element("channel") ?? throw new FormatException("Not an RSS feed: missing <channel>.");

        var episodes = new List<PodcastEpisode>();
        foreach (var item in channel.Elements("item"))
        {
            var enclosure = item.Element("enclosure");
            var url = enclosure?.Attribute("url")?.Value;
            if (string.IsNullOrWhiteSpace(url)) continue; // not playable

            var guid = item.Element("guid")?.Value.Trim();
            episodes.Add(new PodcastEpisode(
                Guid: string.IsNullOrEmpty(guid) ? url : guid,
                Title: item.Element("title")?.Value.Trim() ?? "(untitled)",
                Published: ParseRfc822(item.Element("pubDate")?.Value),
                EnclosureUrl: url,
                EnclosureType: enclosure?.Attribute("type")?.Value,
                EnclosureLength: long.TryParse(enclosure?.Attribute("length")?.Value, out var len) && len > 0 ? len : null,
                Duration: ParseDuration(item.Element(Itunes + "duration")?.Value),
                EpisodeType: item.Element(Itunes + "episodeType")?.Value.Trim(),
                ImageUrl: item.Element(Itunes + "image")?.Attribute("href")?.Value,
                Description: item.Element("description")?.Value));
        }

        // Newest first; undated items sink to the bottom.
        episodes.Sort((a, b) => Nullable.Compare(b.Published, a.Published));

        return new PodcastFeed(
            Title: channel.Element("title")?.Value.Trim() ?? "(untitled podcast)",
            Author: channel.Element(Itunes + "author")?.Value.Trim(),
            Description: channel.Element("description")?.Value.Trim(),
            ImageUrl: channel.Element(Itunes + "image")?.Attribute("href")?.Value
                      ?? channel.Element("image")?.Element("url")?.Value,
            Episodes: episodes);
    }

    /// <summary>Parses RFC 822/1123 dates as found in the wild ("Wed, 01 Oct 2026 10:00:00 -0000", "... GMT", "... PDT").</summary>
    public static DateTimeOffset? ParseRfc822(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim();

        // Drop optional leading day name.
        var comma = s.IndexOf(',');
        if (comma >= 0) s = s[(comma + 1)..].Trim();

        // Normalise zone to +hh:mm.
        var m = ZoneRegex().Match(s);
        string offset = "+00:00";
        if (m.Success)
        {
            var zone = m.Groups["zone"].Value;
            offset = zone switch
            {
                "GMT" or "UT" or "UTC" or "Z" => "+00:00",
                "EST" => "-05:00", "EDT" => "-04:00",
                "CST" => "-06:00", "CDT" => "-05:00",
                "MST" => "-07:00", "MDT" => "-06:00",
                "PST" => "-08:00", "PDT" => "-07:00",
                _ when zone.Length == 5 && (zone[0] == '+' || zone[0] == '-') => $"{zone[..3]}:{zone[3..]}",
                _ => "+00:00",
            };
            s = s[..m.Index].Trim();
        }

        string[] formats = ["d MMM yyyy HH:mm:ss", "d MMM yyyy HH:mm", "dd MMM yyyy HH:mm:ss", "dd MMM yyyy HH:mm"];
        if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var local)
            && TimeSpan.TryParse(offset.TrimStart('+'), CultureInfo.InvariantCulture, out var off))
        {
            return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), off);
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var fallback) ? fallback : null;
    }

    /// <summary>itunes:duration may be "959", "15:59" or "0:15:59".</summary>
    public static TimeSpan? ParseDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Trim().Split(':');
        double total = 0;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return null;
            total = total * 60 + n;
        }
        return TimeSpan.FromSeconds(total);
    }

    [GeneratedRegex(@"\s(?<zone>[+-]\d{4}|[A-Z]{1,4})$")]
    private static partial Regex ZoneRegex();
}
