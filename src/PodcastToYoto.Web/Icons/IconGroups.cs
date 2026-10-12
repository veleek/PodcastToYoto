namespace PodcastToYoto.Web.Icons;

public sealed record IconGroup(string Name, IReadOnlyList<IconCandidate> Icons);

/// <summary>
/// Buckets the icon library for browsing, with hard-coded keyword lists (applied whenever the library is loaded):
/// 1. "Yoto Daily …" titles, then titles with the word "Number(s)", then letter titles ("A", "Letter B"). These go by
///    the title only, so "Earth 2" or an envelope tagged "letter" don't end up there.
/// 2. Otherwise the first group whose keywords appear in the title ("Snowflake" → Nature &amp; weather, even though
///    it's tagged "ice"), then the first whose keywords appear in the tags; everything else lands in "Other".
///    Keywords are whole words, so compounds ("Spiderweb", "Snowman") need listing on their own.
/// </summary>
public static class IconGroups
{
    private static readonly (string Name, HashSet<string> Words)[] Groups =
    [
        (YotoDaily, []),
        (Numbers, Set("number")),
        (Letters, Set("letter", "alphabet", "abc")),
        ("Space", Set("space", "planet", "moon", "star", "rocket", "astronaut", "earth", "mars", "jupiter", "saturn", "venus",
            "mercury", "neptune", "uranus", "pluto", "comet", "galaxy", "alien", "ufo", "satellite", "telescope", "spaceship")),
        ("People", Set("person", "people", "boy", "girl", "man", "woman", "baby", "child", "kid", "family", "princess", "prince",
            "king", "queen", "knight", "pirate", "doctor", "nurse", "teacher", "police", "firefighter", "chef", "farmer",
            "astronaut", "wizard", "witch", "fairy", "mermaid", "superhero", "ninja", "clown", "mum", "mom", "dad", "grandma",
            "grandpa", "friend", "face", "hand")),
        ("Animals", Set("animal", "dog", "puppy", "cat", "kitten", "bird", "fish", "horse", "pony", "cow", "pig", "sheep", "goat",
            "lion", "tiger", "bear", "rabbit", "bunny", "duck", "chicken", "frog", "mouse", "elephant", "monkey", "giraffe",
            "zebra", "dinosaur", "dino", "insect", "bug", "butterfly", "bee", "ladybird", "ladybug", "owl", "fox", "wolf",
            "penguin", "whale", "dolphin", "shark", "octopus", "crab", "snake", "turtle", "tortoise", "unicorn", "dragon",
            "hedgehog", "squirrel", "deer", "panda", "koala", "kangaroo", "crocodile", "spider", "snail", "worm", "parrot",
            "spiderweb", "cobweb", "ducky", "duckie", "narwhal", "pegasus", "merhorse", "heron")),
        ("Food & drink", Set("food", "drink", "fruit", "vegetable", "apple", "banana", "orange", "strawberry", "cherry", "grape",
            "lemon", "pear", "carrot", "cake", "cupcake", "pizza", "burger", "sandwich", "icecream", "ice", "cream", "cookie",
            "biscuit", "bread", "cheese", "egg", "milk", "tea", "coffee", "juice", "candy", "sweet", "chocolate", "lollipop",
            "donut", "doughnut", "pie", "soup", "breakfast", "lunch", "dinner", "milkshake", "watermelon")),
        ("Vehicles", Set("vehicle", "transport", "car", "bus", "train", "plane", "aeroplane", "airplane", "boat", "ship",
            "truck", "lorry", "bike", "bicycle", "tractor", "helicopter", "submarine", "taxi", "ambulance", "van", "scooter")),
        ("Nature & weather", Set("nature", "weather", "tree", "flower", "leaf", "plant", "forest", "mountain", "sea", "ocean",
            "wave", "beach", "river", "rain", "snow", "snowflake", "cloud", "sun", "sunny", "rainbow", "storm", "lightning",
            "wind", "volcano", "island", "rock", "shell", "snowman")),
        ("Music & sound", Set("music", "note", "song", "sing", "guitar", "piano", "drum", "violin", "trumpet", "instrument",
            "microphone", "headphone", "speaker", "radio", "dance", "dancer", "sax", "cassette")),
        ("Home & things", Set("house", "home", "bed", "book", "toy", "ball", "clock", "lamp", "key", "door", "chair", "cup",
            "hat", "shoe", "sock", "bag", "gift", "present", "balloon", "crown", "castle", "tent", "map", "treasure",
            "envelope", "mail", "post", "parcel", "football", "baseball")),
    ];

    private const string Other = "Other";
    private const string YotoDaily = "Yoto Daily";
    private const string Numbers = "Numbers";
    private const string Letters = "Letters";

    /// <summary>The leading groups that are only ever matched on the title (see the class summary).</summary>
    private const int TitleOnlyGroups = 3;

    public static IReadOnlyList<IconGroup> Build(IEnumerable<(IconCandidate Icon, ISet<string> Words)> icons)
    {
        var buckets = Groups.Select(g => (g.Name, List: new List<IconCandidate>())).ToList();
        var other = new List<IconCandidate>();

        foreach (var (icon, words) in icons)
        {
            var index = Classify(icon.Title, words);
            (index < 0 ? other : buckets[index].List).Add(icon);
        }

        return buckets
            .Where(b => b.List.Count > 0)
            .Select(b => new IconGroup(b.Name, Sorted(b.List)))
            .Append(new IconGroup(Other, Sorted(other)))
            .Where(g => g.Icons.Count > 0)
            .OrderBy(g => g.Name == YotoDaily) // matched first, but shown last (after Other)
            .ToList();
    }

    /// <summary>Index into the group list, or -1 for Other.</summary>
    private static int Classify(string title, ISet<string> words)
    {
        var t = title.Trim();
        if (t.StartsWith(YotoDaily, StringComparison.OrdinalIgnoreCase)) return 0;

        var titleWords = IconMatcher.Words(t).ToHashSet(StringComparer.Ordinal);
        if (titleWords.Overlaps(Groups[1].Words)) return 1;
        if ((t.Length == 1 && char.IsLetter(t[0])) || titleWords.Overlaps(Groups[2].Words)) return 2;

        for (var i = TitleOnlyGroups; i < Groups.Length; i++)
            if (titleWords.Overlaps(Groups[i].Words)) return i;
        for (var i = TitleOnlyGroups; i < Groups.Length; i++)
            if (words.Overlaps(Groups[i].Words)) return i;
        return -1;
    }


    private static List<IconCandidate> Sorted(List<IconCandidate> icons) =>
        icons.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ToList();

    private static HashSet<string> Set(params string[] words) =>
        words.SelectMany(IconMatcher.Words).ToHashSet(StringComparer.Ordinal);
}
