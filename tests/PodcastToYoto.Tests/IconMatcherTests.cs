using PodcastToYoto.Web.Icons;

namespace PodcastToYoto.Tests;

public class IconMatcherTests
{
    private static IconIndex Library() => new(
    [
        new IconCandidate("ladder", "Ladder", null, ["climb"]),
        new IconCandidate("tree", "Tree", null, ["forest", "nature"]),
        new IconCandidate("xmastree", "Christmas Tree", null, ["tree"]),
        new IconCandidate("hat", "Top Hat", null, ["magic"]),
        new IconCandidate("dog", "Dog", null, ["pet", "animal"]),
        new IconCandidate("map", "Treasure Map", null, ["pirate"]),
        new IconCandidate("carrot", "Carrot", null, ["vegetable"]),
        new IconCandidate("job", "Briefcase", null, ["job", "work"]),
    ]);

    [Theory]
    [InlineData("🪜 Rocking the Ladder 🪜", "ladder")]
    [InlineData("🌳🌲 Forgetting the Forest 🌲🌳", "tree")]          // alias "tree"; shortest title wins over "Christmas Tree"
    [InlineData("🎩💨 Catch That Hat 💨🎩", "hat")]                  // "top hat" → +3 +3 on "Top Hat"
    [InlineData("🐶🐱🐰 Pim Pim Gets a Pet 🐠🐭🦆", "dog")]           // first emoji that matches wins
    [InlineData("🥕🏴‍☠️ Making a Map 🏴‍☠️🥕", "carrot")]
    public void Matches_emoji(string title, string expectedMediaId) =>
        Assert.Equal(expectedMediaId, IconMatcher.Match(title, Library())?.Icon.MediaId);

    [Fact]
    public void Falls_back_to_title_words_against_icon_titles_only()
    {
        Assert.Equal("map", IconMatcher.Match("Making a Treasure Map", Library())?.Icon.MediaId);
        Assert.Null(IconMatcher.Match("Get a Job", Library())); // "job" is only a tag, and too short anyway
    }

    [Fact]
    public void No_match_returns_null() =>
        Assert.Null(IconMatcher.Match("🦄 Unicorn Party", Library()));

    [Fact]
    public void Ignores_variation_selectors_and_skin_tones()
    {
        Assert.Equal(new[] { 0x2702 }, IconMatcher.Emojis("✂️✂️").ToArray());       // U+FE0F dropped, duplicate removed
        Assert.Equal(new[] { 0x1F44B }, IconMatcher.Emojis("👋🏽").ToArray());         // skin tone modifier dropped
    }

    [Theory]
    [InlineData(0x1FA9C, "ladder")]
    [InlineData(0x1F3A9, "top hat")]
    [InlineData(0x2702, "scissor")]   // "black scissors": filler dropped, singularised
    public void Keywords_from_unicode_names(int cp, string expected) =>
        Assert.Equal(expected, string.Join(" ", IconMatcher.Keywords(cp)));

    [Fact]
    public void Search_matches_word_prefixes_title_before_tags()
    {
        var titles = Library().Search("tre").Select(i => i.Title).ToArray();
        // "Tree" and "Treasure Map" by title (shortest first), "Christmas Tree" by title too; nothing tag-only here.
        Assert.Equal(new[] { "Tree", "Christmas Tree", "Treasure Map" }, titles);

        Assert.Equal(new[] { "Briefcase" }, Library().Search("work").Select(i => i.Title).ToArray()); // tag match
        Assert.Empty(Library().Search("tree hat"));                                                  // all words must match
    }

    [Fact]
    public void Search_empty_lists_everything_alphabetically() =>
        Assert.Equal("Briefcase", Library().Search("").First().Title);

    [Fact]
    public void Find_by_media_id()
    {
        Assert.Equal("Top Hat", Library().Find("hat")?.Title);
        Assert.Null(Library().Find("nope"));
    }

    [Fact]
    public void Groups_bucket_by_keywords_with_other_and_yoto_daily_last()
    {
        var groups = new IconIndex(
        [
            new IconCandidate("1", "Dog", null, ["pet"]),
            new IconCandidate("2", "Tree", null, []),
            new IconCandidate("3", "Ladder", null, []),
            new IconCandidate("4", "Numbers - 3", null, ["numbers", "3"]),
            new IconCandidate("5", "B", null, []),
            new IconCandidate("6", "Saturn", null, ["planet"]),
            new IconCandidate("7", "Earth 2", null, ["space", "planet"]),       // a digit isn't a number icon
            new IconCandidate("8", "02_Snake", null, ["snake"]),
            new IconCandidate("9", "Envelope", null, ["letter", "mail"]),      // tagged "letter", but not a letter
            new IconCandidate("10", "Snowflake", null, ["ice", "weather"]),    // title beats the "ice" tag
            new IconCandidate("11", "Yoto Daily - Quiz", null, ["yoto", "daily", "question"]),
            new IconCandidate("12", "Spiderweb", null, ["px", "spiderweb"]),
            new IconCandidate("13", "Baking with Yoto - Pancakes", null, []),  // "king" is a People word, but not inside "baking"
        ]).Groups;

        string GroupOf(string title) => groups.Single(g => g.Icons.Any(i => i.Title == title)).Name;

        Assert.Equal("Animals", GroupOf("Dog"));
        Assert.Equal("Nature & weather", GroupOf("Tree"));
        Assert.Equal("Numbers", GroupOf("Numbers - 3"));
        Assert.Equal("Space", GroupOf("Earth 2"));
        Assert.Equal("Animals", GroupOf("02_Snake"));
        Assert.Equal("Home & things", GroupOf("Envelope"));
        Assert.Equal("Nature & weather", GroupOf("Snowflake"));
        Assert.Equal("Yoto Daily", GroupOf("Yoto Daily - Quiz"));
        Assert.Equal("Animals", GroupOf("Spiderweb"));
        Assert.Equal("Other", GroupOf("Baking with Yoto - Pancakes"));
        Assert.Equal("Letters", GroupOf("B"));
        Assert.Equal("Space", GroupOf("Saturn"));
        Assert.Equal("Other", GroupOf("Ladder"));
        Assert.Equal(["Other", "Yoto Daily"], groups.TakeLast(2).Select(g => g.Name)); // Yoto Daily after Other
    }
}
