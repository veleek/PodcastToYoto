using PodcastToYoto.Web.Icons;

namespace PodcastToYoto.Tests;

public class YotoIconsTests
{
    // Shaped like a yotoicons.com results page: an image, the artist, and an onclick carrying the metadata.
    private const string Page = """
        <h2>We&#39;ve got 531 icons with that tag:</h2>
        <button onclick="setType('singles')">singles</button>
        <div class="icon" onclick="populate_icon_modal('757', 'plants', 'tree', 'trees forest nature', 'Amanite70', '26')">
          <img src="https://www.yotoicons.com/static/uploads/757.png"><p>@Amanite70</p>
        </div>
        <div class="icon" onclick='show(1158, "buildings", "Tom&#39;s house", "buildings home", "Bekybeks", 3)'>
          <img src="/static/uploads/1158.png"><p>@Bekybeks</p>
        </div>
        <div class="icon"><img src="/static/uploads/16.png"><p>@Leo The Lion</p></div>
        """;

    [Fact]
    public void Parses_total_metadata_and_falls_back_to_image_and_artist()
    {
        var page = YotoIconsParser.Parse(Page);

        Assert.Equal(531, page.Total);
        Assert.Equal([757, 1158, 16], page.Icons.Select(i => i.Id));

        var tree = page.Icons[0];
        Assert.Equal(("tree", "plants", "Amanite70", (int?)26), (tree.Title, tree.Category, tree.Artist, tree.Downloads));
        Assert.Equal(["trees", "forest", "nature"], tree.Tags);

        var house = page.Icons[1];
        Assert.Equal(("Tom's house", "buildings", (int?)3), (house.Title, house.Category, house.Downloads));

        var bare = page.Icons[2];
        Assert.Equal(("Leo The Lion", (string?)null), (bare.Artist, bare.Title));
        Assert.Empty(bare.Tags);
    }

    [Theory]
    [InlineData("building")]   // singular query finds the plural tag, as on the site
    [InlineData("buildings")]
    [InlineData("build")]      // word prefixes too
    [InlineData("home")]
    public void Cached_tags_are_searchable_singular_or_plural(string query)
    {
        var library = new IconIndex([new IconCandidate("tree", "Tree", null, ["nature"])])
            .WithExtras([new IconCandidate("yotoicons:1158", "Tom's house", "/yotoicons/1158.png", ["buildings", "home"], "@Bekybeks on yotoicons.com")]);

        Assert.Equal(["yotoicons:1158"], library.Search(query).Select(i => i.MediaId));
    }

    [Fact]
    public void Extras_are_browsable_but_never_auto_matched()
    {
        var library = new IconIndex([new IconCandidate("hat", "Top Hat", null, [])])
            .WithExtras([new IconCandidate("yotoicons:1", "Tree", null, ["forest"])]);

        Assert.Null(IconMatcher.Match("🌳 Forgetting the Forest", library));
        Assert.NotNull(library.Find("yotoicons:1"));
        Assert.Equal(2, library.Count);
        Assert.Equal(1, library.WithExtras([]).Count); // replaces, doesn't stack
    }

    [Theory]
    [InlineData("  @PangolinPaw ", "pangolinpaw")]
    [InlineData("Space   Rocket", "space rocket")]
    public void Normalises_queries(string query, string expected) =>
        Assert.Equal(expected, YotoIconsLibrary.Normalise(query));
}

public class YotoIconsImageTests
{
    [Fact]
    public void Converts_non_png_images_to_png()
    {
        // A 1x1 GIF (some yotoicons.com ".png" uploads are really GIFs or JPEGs).
        var gif = Convert.FromBase64String("R0lGODlhAQABAIAAAP///wAAACH5BAEAAAAALAAAAAABAAEAAAICRAEAOw==");
        var png = YotoIconsLibrary.ToPng(gif);
        Assert.NotNull(png);
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, png![..4]);
    }

    [Fact]
    public void Rejects_non_images() =>
        Assert.Null(YotoIconsLibrary.ToPng("<html>blocked</html>"u8.ToArray()));
}

public class YotoIconsContentTypeTests
{
    [Theory]
    [InlineData("/data/yotoicons/images/12.gif", "image/gif")]
    [InlineData("/data/yotoicons/images/12.png", "image/png")]
    public void Serves_gifs_as_gifs(string path, string expected) => Assert.Equal(expected, YotoIconsLibrary.ContentType(path));
}
