using SkiaSharp;

namespace PodcastToYoto.Web.Icons;

/// <summary>A candidate 16x16 icon made from a podcast's artwork.</summary>
public sealed record LogoIconVariant(string Key, string Crop, string Style, byte[] Png)
{
    public string DataUrl => $"data:image/png;base64,{Convert.ToBase64String(Png)}";
}

/// <summary>
/// Makes 16x16 player icons from podcast artwork, for podcasts whose episodes don't map well onto Yoto's public icons.
/// Artwork is usually a detailed square, so a straight shrink tends to be mush; centre crops and punchier colours give
/// options that read better on the player's tiny display.
/// </summary>
public sealed class LogoIconGenerator(IHttpClientFactory httpFactory)
{
    public const string HttpClientName = "artwork";
    public const int Size = 16;
    private const long MaxImageBytes = 20 * 1024 * 1024;

    /// <summary>Fraction of the artwork's shorter side kept, centred.</summary>
    public static readonly (string Name, double Fraction)[] Crops =
    [
        ("Whole", 1),
        ("Centre ¾", 0.75),
        ("Centre ½", 0.5),
        ("Centre ⅓", 1 / 3.0),
    ];

    public static readonly string[] Styles = ["Natural", "Bold", "Posterised"];

    public async Task<IReadOnlyList<LogoIconVariant>> GenerateAsync(string imageUrl, CancellationToken ct = default)
    {
        var http = httpFactory.CreateClient(HttpClientName);
        using var resp = await http.GetAsync(imageUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength > MaxImageBytes) throw new InvalidOperationException("Artwork is too large.");

        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        using var codec = SKCodec.Create(new SKMemoryStream(bytes)) ?? throw new InvalidOperationException("Couldn't decode the artwork image.");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var rgba = SKBitmap.Decode(codec, info) ?? throw new InvalidOperationException("Couldn't decode the artwork image.");
        return Generate(new RgbaImage(rgba.Width, rgba.Height, rgba.Bytes));
    }

    public static IReadOnlyList<LogoIconVariant> Generate(RgbaImage source)
    {
        var variants = new List<LogoIconVariant>();
        foreach (var (crop, fraction) in Crops)
        {
            var small = ShrinkCentre(source, fraction, Size);
            foreach (var style in Styles)
            {
                var styled = style switch
                {
                    "Bold" => Adjust(small, saturation: 1.5, contrast: 1.35),
                    "Posterised" => Posterise(Adjust(small, saturation: 1.3, contrast: 1.2), colours: 6),
                    _ => small,
                };
                variants.Add(new LogoIconVariant($"{crop}|{style}", crop, style, ToPng(styled)));
            }
        }
        return variants;
    }

    /// <summary>
    /// Crops the centred square covering <paramref name="fraction"/> of the shorter side, then shrinks it to
    /// <paramref name="size"/>² by averaging every source pixel into its target pixel (the most faithful big → tiny shrink).
    /// </summary>
    public static RgbaImage ShrinkCentre(RgbaImage src, double fraction, int size)
    {
        var side = Math.Max(size, (int)Math.Round(Math.Min(src.Width, src.Height) * fraction));
        side = Math.Min(side, Math.Min(src.Width, src.Height));
        int x0 = (src.Width - side) / 2, y0 = (src.Height - side) / 2;

        var dst = new byte[size * size * 4];
        for (var ty = 0; ty < size; ty++)
        {
            int sy0 = y0 + ty * side / size, sy1 = Math.Max(sy0 + 1, y0 + (ty + 1) * side / size);
            for (var tx = 0; tx < size; tx++)
            {
                int sx0 = x0 + tx * side / size, sx1 = Math.Max(sx0 + 1, x0 + (tx + 1) * side / size);
                long r = 0, g = 0, b = 0, a = 0, n = 0;
                for (var y = sy0; y < sy1; y++)
                for (var x = sx0; x < sx1; x++)
                {
                    var i = (y * src.Width + x) * 4;
                    var pa = src.Pixels[i + 3];
                    // Weight colour by alpha so transparent pixels don't drag colours towards black.
                    r += src.Pixels[i] * pa;
                    g += src.Pixels[i + 1] * pa;
                    b += src.Pixels[i + 2] * pa;
                    a += pa;
                    n++;
                }

                var o = (ty * size + tx) * 4;
                if (a > 0)
                {
                    dst[o] = (byte)(r / a);
                    dst[o + 1] = (byte)(g / a);
                    dst[o + 2] = (byte)(b / a);
                }
                dst[o + 3] = (byte)(a / n);
            }
        }
        return new RgbaImage(size, size, dst);
    }

    /// <summary>Scales saturation (around each pixel's luma) and contrast (around mid-grey).</summary>
    public static RgbaImage Adjust(RgbaImage src, double saturation, double contrast)
    {
        var dst = (byte[])src.Pixels.Clone();
        for (var i = 0; i < dst.Length; i += 4)
        {
            double r = dst[i], g = dst[i + 1], b = dst[i + 2];
            var luma = 0.299 * r + 0.587 * g + 0.114 * b;
            r = luma + (r - luma) * saturation;
            g = luma + (g - luma) * saturation;
            b = luma + (b - luma) * saturation;
            dst[i] = Clamp((r - 128) * contrast + 128);
            dst[i + 1] = Clamp((g - 128) * contrast + 128);
            dst[i + 2] = Clamp((b - 128) * contrast + 128);
        }
        return new RgbaImage(src.Width, src.Height, dst);
    }

    /// <summary>Reduces to at most <paramref name="colours"/> flat colours (k-means), like hand-drawn pixel art.</summary>
    public static RgbaImage Posterise(RgbaImage src, int colours)
    {
        var count = src.Width * src.Height;
        var px = new (double R, double G, double B)[count];
        for (var p = 0; p < count; p++) px[p] = (src.Pixels[p * 4], src.Pixels[p * 4 + 1], src.Pixels[p * 4 + 2]);

        // Deterministic seeds: evenly spaced pixels in brightness order.
        var byLuma = px.OrderBy(c => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B).ToArray();
        var k = Math.Min(colours, count);
        var centres = Enumerable.Range(0, k).Select(j => byLuma[(int)((j + 0.5) * count / k)]).ToArray();
        var assign = new int[count];

        for (var iter = 0; iter < 12; iter++)
        {
            for (var p = 0; p < count; p++)
            {
                var best = 0;
                var bestD = double.MaxValue;
                for (var c = 0; c < k; c++)
                {
                    double dr = px[p].R - centres[c].R, dg = px[p].G - centres[c].G, db = px[p].B - centres[c].B;
                    var d = dr * dr + dg * dg + db * db;
                    if (d < bestD) { bestD = d; best = c; }
                }
                assign[p] = best;
            }

            for (var c = 0; c < k; c++)
            {
                double r = 0, g = 0, b = 0;
                var n = 0;
                for (var p = 0; p < count; p++)
                {
                    if (assign[p] != c) continue;
                    r += px[p].R; g += px[p].G; b += px[p].B; n++;
                }
                if (n > 0) centres[c] = (r / n, g / n, b / n);
            }
        }

        var dst = (byte[])src.Pixels.Clone();
        for (var p = 0; p < count; p++)
        {
            var c = centres[assign[p]];
            dst[p * 4] = Clamp(c.R);
            dst[p * 4 + 1] = Clamp(c.G);
            dst[p * 4 + 2] = Clamp(c.B);
        }
        return new RgbaImage(src.Width, src.Height, dst);
    }

    private static byte[] ToPng(RgbaImage image)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        System.Runtime.InteropServices.Marshal.Copy(image.Pixels, 0, bitmap.GetPixels(), image.Pixels.Length);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte Clamp(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
}

/// <summary>Unpremultiplied RGBA pixels, row by row.</summary>
public sealed record RgbaImage(int Width, int Height, byte[] Pixels);
