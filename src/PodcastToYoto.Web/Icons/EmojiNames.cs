using System.Globalization;

namespace PodcastToYoto.Web.Icons;

/// <summary>
/// Unicode names for emoji / pictographic code points (e.g. U+1FA9C → "ladder"), from the embedded
/// emoji-names.txt (generated from the Unicode Character Database). Skin-tone modifiers, variation selectors
/// and ZWJ aren't in the table, so they are naturally ignored.
/// </summary>
public static class EmojiNames
{
    private static readonly Lazy<IReadOnlyDictionary<int, string>> Table = new(Load);

    public static bool TryGet(int codePoint, out string name) => Table.Value.TryGetValue(codePoint, out name!);

    private static IReadOnlyDictionary<int, string> Load()
    {
        using var stream = typeof(EmojiNames).Assembly.GetManifestResourceStream("emoji-names.txt")
            ?? throw new InvalidOperationException("Embedded resource emoji-names.txt is missing.");
        using var reader = new StreamReader(stream);

        var map = new Dictionary<int, string>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var sep = line.IndexOf(';');
            if (sep > 0 && int.TryParse(line.AsSpan(0, sep), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp))
                map[cp] = line[(sep + 1)..];
        }
        return map;
    }
}
