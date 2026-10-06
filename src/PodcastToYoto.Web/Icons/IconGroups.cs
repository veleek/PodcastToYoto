namespace PodcastToYoto.Web.Icons;

public sealed record IconGroup(string Name, IReadOnlyList<IconCandidate> Icons);

/// <summary>
/// Buckets the icon library for browsing. Plain keyword lists checked against each icon's title and tag words;
/// the first group that matches wins, everything else lands in "Other".
/// </summary>
public static class IconGroups
{
    private static readonly string[] NumberWords =
    [
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve",
        "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen", "twenty", "hundred", "number",
    ];

    private static readonly (string Name, HashSet<string> Words)[] Groups =
    [
        ("Numbers", Set(NumberWords)),
        ("Letters", Set("letter", "alphabet", "abc")),
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
            "hedgehog", "squirrel", "deer", "panda", "koala", "kangaroo", "crocodile", "spider", "snail", "worm", "parrot")),
        ("Food & drink", Set("food", "drink", "fruit", "vegetable", "apple", "banana", "orange", "strawberry", "cherry", "grape",
            "lemon", "pear", "carrot", "cake", "cupcake", "pizza", "burger", "sandwich", "icecream", "ice", "cream", "cookie",
            "biscuit", "bread", "cheese", "egg", "milk", "tea", "coffee", "juice", "candy", "sweet", "chocolate", "lollipop",
            "donut", "doughnut", "pie", "soup", "breakfast", "lunch", "dinner")),
        ("Vehicles", Set("vehicle", "transport", "car", "bus", "train", "plane", "aeroplane", "airplane", "boat", "ship",
            "truck", "lorry", "bike", "bicycle", "tractor", "helicopter", "submarine", "taxi", "ambulance", "van", "scooter")),
        ("Nature & weather", Set("nature", "weather", "tree", "flower", "leaf", "plant", "forest", "mountain", "sea", "ocean",
            "wave", "beach", "river", "rain", "snow", "snowflake", "cloud", "sun", "sunny", "rainbow", "storm", "lightning",
            "wind", "volcano", "island", "rock", "shell")),
        ("Music & sound", Set("music", "note", "song", "sing", "guitar", "piano", "drum", "violin", "trumpet", "instrument",
            "microphone", "headphone", "speaker", "radio", "dance")),
        ("Home & things", Set("house", "home", "bed", "book", "toy", "ball", "clock", "lamp", "key", "door", "chair", "cup",
            "hat", "shoe", "sock", "bag", "gift", "present", "balloon", "crown", "castle", "tent", "map", "treasure")),
    ];

    public const string Other = "Other";

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
            .ToList();
    }

    /// <summary>Index into the group list, or -1 for Other.</summary>
    private static int Classify(string title, ISet<string> words)
    {
        // Bare numerals ("1", "10", "Number 3") count as numbers.
        if (words.Any(w => w.All(char.IsDigit))) return 0;
        for (var i = 0; i < Groups.Length; i++)
            if (words.Overlaps(Groups[i].Words)) return i;
        // Single letters ("A", "Letter B")
        if (title.Trim().Length == 1 && char.IsLetter(title.Trim()[0])) return 1;
        return -1;
    }

    private static List<IconCandidate> Sorted(List<IconCandidate> icons) =>
        icons.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ToList();

    private static HashSet<string> Set(params string[] words) =>
        words.SelectMany(IconMatcher.Words).ToHashSet(StringComparer.Ordinal);
}
