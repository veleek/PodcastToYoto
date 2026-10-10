using PodcastToYoto.Web.Data;
using PodcastToYoto.Web.Icons;
using PodcastToYoto.Web.Sync;
using SkiaSharp;

namespace PodcastToYoto.Tests;

public class LogoIconTests
{
    /// <summary>Left half red, right half blue, size×size.</summary>
    private static RgbaImage Split(int size)
    {
        var px = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var i = (y * size + x) * 4;
            if (x < size / 2) px[i] = 255; else px[i + 2] = 255;
            px[i + 3] = 255;
        }
        return new RgbaImage(size, size, px);
    }

    [Fact]
    public void Shrink_averages_into_16x16()
    {
        var small = LogoIconGenerator.ShrinkCentre(Split(320), 1, 16);
        Assert.Equal(16, small.Width);
        Assert.Equal((byte)255, small.Pixels[0]);          // top-left: red
        Assert.Equal((byte)255, small.Pixels[15 * 4 + 2]); // top-right: blue
        Assert.Equal((byte)255, small.Pixels[3]);
    }

    [Fact]
    public void Centre_crop_keeps_the_middle()
    {
        // The centre third of a red|blue split is still half red, half blue.
        var small = LogoIconGenerator.ShrinkCentre(Split(300), 1 / 3.0, 16);
        Assert.Equal((byte)255, small.Pixels[0]);
        Assert.Equal((byte)255, small.Pixels[15 * 4 + 2]);
    }

    [Fact]
    public void Posterise_limits_colours()
    {
        var rnd = new Random(1);
        var px = new byte[16 * 16 * 4];
        rnd.NextBytes(px);
        var p = LogoIconGenerator.Posterise(new RgbaImage(16, 16, px), 4);
        var colours = Enumerable.Range(0, 256).Select(i => (p.Pixels[i * 4], p.Pixels[i * 4 + 1], p.Pixels[i * 4 + 2])).Distinct().Count();
        Assert.InRange(colours, 1, 4);
    }

    [Fact]
    public void Generates_16x16_pngs_for_every_crop_and_style()
    {
        var variants = LogoIconGenerator.Generate(Split(100));
        Assert.Equal(LogoIconGenerator.Crops.Length * LogoIconGenerator.Styles.Length, variants.Count);
        foreach (var v in variants)
        {
            using var bmp = SKBitmap.Decode(v.Png);
            Assert.Equal(16, bmp.Width);
            Assert.Equal(16, bmp.Height);
        }
    }

    [Fact]
    public void Icon_precedence()
    {
        var pod = new Subscription { Name = "p", FeedUrl = "https://x", UseDefaultIcon = true, LogoIconMediaId = "logo", LogoIcon = LogoIconMode.Fallback };
        var ep = new Episode { Guid = "g", Title = "t", EnclosureUrl = "https://x/1.mp3" };

        Assert.Equal("yoto:#logo", SyncEngine.IconFor(ep, pod, null)); // fallback: no emoji match
        pod.LogoIcon = LogoIconMode.Off;
        Assert.Null(SyncEngine.IconFor(ep, pod, null));
        pod.LogoIcon = LogoIconMode.Always;
        Assert.Equal("yoto:#logo", SyncEngine.IconFor(ep, pod, null));
        ep.IconMediaId = "picked";
        Assert.Equal("yoto:#picked", SyncEngine.IconFor(ep, pod, null)); // hand-picked always wins
        ep.IconMediaId = Episode.PodcastDefaultIcon;
        pod.LogoIcon = LogoIconMode.Fallback;
        Assert.Equal("yoto:#logo", SyncEngine.IconFor(ep, pod, null)); // cleared: podcast default
        pod.LogoIcon = LogoIconMode.Off;
        Assert.Null(SyncEngine.IconFor(ep, pod, null));
        ep.IconMediaId = Episode.NoIcon;
        pod.LogoIcon = LogoIconMode.Always;
        Assert.Null(SyncEngine.IconFor(ep, pod, null)); // cleared: no icon at all, even over "always"
    }
}
