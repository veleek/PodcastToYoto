using PodcastToYoto.Feeds;

namespace PodcastToYoto.Tests;

public class PodcastFeedParserTests
{
    private const string Feed = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd">
          <channel>
            <title> Silly Stories for Kids </title>
            <itunes:author>Samuel Ramsden</itunes:author>
            <description>Made up on the spot.</description>
            <itunes:image href="https://example.com/cover.jpg"/>
            <item>
              <title>🪜 Rocking the Ladder 🪜</title>
              <guid isPermaLink="false">guid-2</guid>
              <pubDate>Sun, 28 Sep 2026 07:00:00 GMT</pubDate>
              <enclosure url="https://example.com/2.mp3" type="audio/mpeg" length="22100000"/>
              <itunes:duration>15:39</itunes:duration>
              <itunes:episodeType>full</itunes:episodeType>
            </item>
            <item>
              <title>✂️🧋Get a Job🧋✂️</title>
              <guid>guid-1</guid>
              <pubDate>Thu, 01 Oct 2026 10:00:00 -0000</pubDate>
              <enclosure url="https://example.com/1.mp3" type="audio/mpeg" length="22600000"/>
              <itunes:duration>959</itunes:duration>
            </item>
            <item>
              <title>No guid</title>
              <pubDate>Tue, 23 Sep 2026 12:30:00 PDT</pubDate>
              <enclosure url="https://example.com/3.mp3" type="audio/mpeg" length="0"/>
              <itunes:duration>1:02:03</itunes:duration>
              <itunes:episodeType>bonus</itunes:episodeType>
            </item>
            <item>
              <title>No enclosure (skipped)</title>
              <guid>guid-x</guid>
            </item>
          </channel>
        </rss>
        """;

    [Fact]
    public void Parses_channel_metadata()
    {
        var feed = PodcastFeedParser.Parse(Feed);

        Assert.Equal("Silly Stories for Kids", feed.Title);
        Assert.Equal("Samuel Ramsden", feed.Author);
        Assert.Equal("https://example.com/cover.jpg", feed.ImageUrl);
    }

    [Fact]
    public void Skips_items_without_enclosure_and_sorts_newest_first()
    {
        var feed = PodcastFeedParser.Parse(Feed);

        Assert.Equal(new[] { "guid-1", "guid-2", "https://example.com/3.mp3" }, feed.Episodes.Select(e => e.Guid));
    }

    [Fact]
    public void Parses_episode_fields()
    {
        var e = PodcastFeedParser.Parse(Feed).Episodes.Single(x => x.Guid == "guid-2");

        Assert.Equal("🪜 Rocking the Ladder 🪜", e.Title);
        Assert.Equal(new DateTimeOffset(2026, 9, 28, 7, 0, 0, TimeSpan.Zero), e.Published);
        Assert.Equal(TimeSpan.FromSeconds(15 * 60 + 39), e.Duration);
        Assert.Equal(22_100_000, e.EnclosureLength);
        Assert.Equal("full", e.EpisodeType);
    }

    [Fact]
    public void Missing_guid_falls_back_to_enclosure_url_and_zero_length_is_null()
    {
        var e = PodcastFeedParser.Parse(Feed).Episodes.Single(x => x.Title == "No guid");

        Assert.Equal("https://example.com/3.mp3", e.Guid);
        Assert.Null(e.EnclosureLength);
        Assert.Equal("bonus", e.EpisodeType);
    }

    [Fact]
    public void Ignores_doctype()
    {
        var xml = "<?xml version=\"1.0\"?><!DOCTYPE rss [<!ENTITY x \"boom\">]><rss><channel><title>T</title></channel></rss>";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));

        Assert.Equal("T", PodcastFeedParser.Parse(stream).Title);
    }

    [Theory]
    [InlineData("Thu, 01 Oct 2026 10:00:00 -0000", "2026-10-01T10:00:00+00:00")]
    [InlineData("Thu, 01 Oct 2026 10:00:00 +0000", "2026-10-01T10:00:00+00:00")]
    [InlineData("Sun, 28 Sep 2026 07:00:00 GMT", "2026-09-28T07:00:00+00:00")]
    [InlineData("Tue, 23 Sep 2026 12:30:00 PDT", "2026-09-23T12:30:00-07:00")]
    [InlineData("Tue, 23 Sep 2026 12:30:00 +1000", "2026-09-23T12:30:00+10:00")]
    [InlineData("3 Sep 2026 08:15:00 EST", "2026-09-03T08:15:00-05:00")]
    [InlineData("Wed, 3 Sep 2026 08:15 +0130", "2026-09-03T08:15:00+01:30")]
    public void Parses_rfc822_dates(string input, string expected)
    {
        Assert.Equal(DateTimeOffset.Parse(expected), PodcastFeedParser.ParseRfc822(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a date")]
    public void Unparseable_dates_are_null(string? input)
    {
        Assert.Null(PodcastFeedParser.ParseRfc822(input));
    }

    [Theory]
    [InlineData("959", 959.0)]
    [InlineData("15:59", 959.0)]
    [InlineData("0:15:59", 959.0)]
    [InlineData("1:02:03", 3723.0)]
    [InlineData("959.5", 959.5)]
    public void Parses_durations(string input, double seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), PodcastFeedParser.ParseDuration(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    public void Unparseable_durations_are_null(string? input)
    {
        Assert.Null(PodcastFeedParser.ParseDuration(input));
    }
}
