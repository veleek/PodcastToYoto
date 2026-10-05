using PodcastToYoto.Web.Subscriptions;

namespace PodcastToYoto.Tests;

public class TrackMatcherTests
{
    [Theory]
    [InlineData("✂️🧋Get a Job🧋✂️", "getajob")]
    [InlineData("🐶🐱🐰 Pim Pim Gets a Pet 🐠🐭🦆", "pimpimgetsapet")]
    [InlineData("Café Crème!", "cafecreme")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalizes(string? input, string expected) => Assert.Equal(expected, TrackMatcher.Normalize(input));

    private static readonly string[] Titles =
    [
        "✂️🧋Get a Job🧋✂️",
        "🪜 Rocking the Ladder 🪜",
        "🐶🐱🐰 Pim Pim Gets a Pet 🐠🐭🦆",
        "Pim Pim",
    ];

    private static List<(string, string)> Candidates() => Titles.Select(t => (TrackMatcher.Normalize(t), t)).ToList();

    [Theory]
    [InlineData("Get a Job", "✂️🧋Get a Job🧋✂️")]
    [InlineData("get-a-job", "✂️🧋Get a Job🧋✂️")]
    [InlineData("Silly Stories - Rocking the Ladder.mp3", "🪜 Rocking the Ladder 🪜")]
    [InlineData("S3E12 Pim Pim Gets a Pet", "🐶🐱🐰 Pim Pim Gets a Pet 🐠🐭🦆")] // longest contained title wins over "Pim Pim"
    [InlineData("Pim Pim", "Pim Pim")]
    public void Matches(string track, string expected) => Assert.Equal(expected, TrackMatcher.Match(track, Candidates()));

    [Theory]
    [InlineData("Something else entirely")]
    [InlineData("Job")]   // too short to be a containment match
    [InlineData("")]
    public void No_match(string track) => Assert.Null(TrackMatcher.Match(track, Candidates()));
}
