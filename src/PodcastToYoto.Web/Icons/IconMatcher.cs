using System.Text;

namespace PodcastToYoto.Web.Icons;

/// <param name="Credit">Where the icon came from, for tooltips (e.g. "@artist on yotoicons.com"); null for Yoto's own.</param>
public sealed record IconCandidate(string MediaId, string Title, string? Url, IReadOnlyList<string> Tags, string? Credit = null)
{
    public string Ref => $"yoto:#{MediaId}";
}

public sealed record IconMatch(IconCandidate Icon, string Reason);

/// <summary>Pre-tokenised icon library for <see cref="IconMatcher"/>.</summary>
public sealed class IconIndex
{
    /// <param name="Matchable">False for extras (e.g. cached yotoicons.com icons): browsable and searchable in the
    /// pickers, but never chosen automatically, so the UI's automatic match stays the same as the sync's.</param>
    internal sealed record Entry(IconCandidate Icon, HashSet<string> TitleWords, HashSet<string> TagWords, bool Matchable);

    internal IReadOnlyList<Entry> Entries { get; }
    private readonly IReadOnlyList<IconCandidate> _library;
    private readonly Dictionary<string, IconCandidate> _byId;

    /// <summary>Number of icons, extras included.</summary>
    public int Count => Entries.Count;

    public IconIndex(IEnumerable<IconCandidate> icons) : this(icons, []) { }

    private IconIndex(IEnumerable<IconCandidate> icons, IEnumerable<IconCandidate> extras)
    {
        _library = icons as IReadOnlyList<IconCandidate> ?? icons.ToList();
        // The library can list the same media more than once (different displayIconIds); keep one per media id so
        // lookups and UI keys are unique.
        Entries = _library.Select(i => (Icon: i, Matchable: true))
            .Concat(extras.Select(i => (Icon: i, Matchable: false)))
            .DistinctBy(x => x.Icon.MediaId)
            .Select(x => new Entry(x.Icon,
                IconMatcher.Words(x.Icon.Title).ToHashSet(StringComparer.Ordinal),
                x.Icon.Tags.SelectMany(IconMatcher.Words).ToHashSet(StringComparer.Ordinal),
                x.Matchable))
            .ToList();
        _byId = new Dictionary<string, IconCandidate>(StringComparer.Ordinal);
        foreach (var e in Entries) _byId.TryAdd(e.Icon.MediaId, e.Icon);
    }

    public IconCandidate? Find(string mediaId) => _byId.GetValueOrDefault(mediaId);

    /// <summary>
    /// This library plus <paramref name="extras"/> (replacing any extras added before). Extras show up in browsing,
    /// search and <see cref="Find"/>, but <see cref="IconMatcher"/> never picks them.
    /// </summary>
    public IconIndex WithExtras(IEnumerable<IconCandidate> extras) => new(_library, extras);

    private IconIndex? _withoutExtras;

    /// <summary>Just the library, without any extras (cached once).</summary>
    public IconIndex WithoutExtras => _withoutExtras ??= Count == _library.Count ? this : new IconIndex(_library);

    private IReadOnlyList<IconGroup>? _groups;

    /// <summary>The whole library bucketed for browsing (Numbers, Letters, Space, People, Animals, …, Other).</summary>
    public IReadOnlyList<IconGroup> Groups =>
        _groups ??= IconGroups.Build(Entries.Select(e => (e.Icon, (ISet<string>)new HashSet<string>(e.TitleWords.Concat(e.TagWords)))));

    /// <summary>
    /// Icons whose title or tags contain every query word as a word prefix ("tre" finds "Tree", "Treasure Map").
    /// Title hits rank above tag-only hits; then shorter titles first. Empty query → everything, alphabetically.
    /// </summary>
    public IReadOnlyList<IconCandidate> Search(string? query, int limit = 120)
    {
        var words = IconMatcher.Words(query ?? "").ToList();
        if (words.Count == 0)
            return Entries.Select(e => e.Icon).OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase).Take(limit).ToList();

        static bool Has(HashSet<string> set, string w) => set.Any(x => x.StartsWith(w, StringComparison.Ordinal));

        return Entries
            .Select(e =>
            {
                int score = 0;
                foreach (var w in words)
                {
                    if (Has(e.TitleWords, w)) score += 2;
                    else if (Has(e.TagWords, w)) score += 1;
                    else return (e, score: -1);
                }
                return (e, score);
            })
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.e.TitleWords.Count)
            .ThenBy(x => x.e.Icon.Title, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(x => x.e.Icon)
            .ToList();
    }
}

/// <summary>
/// Picks a Yoto icon for an episode title with plain keyword matching — no fuzzy/AI magic:
/// 1. For each emoji in the title (in order), take its Unicode name ("🪜" → "ladder", "🌳" → "deciduous tree"),
///    drop filler words, and score icons: +3 per word in the icon title, +1 per word in its tags.
///    The first emoji with any match wins; ties go to the icon with the shortest title.
/// 2. If no emoji matches, try the title's own words against icon titles only.
/// </summary>
public static class IconMatcher
{
    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "the", "of", "with", "in", "on", "for", "to", "at", "by",
        "sign", "symbol", "emoji", "black", "white", "heavy", "large", "small", "medium", "ornament",
        "face", "building", "button", "mark",
    };

    /// <summary>Hand-tuned keywords where the Unicode name is unhelpful for finding an icon.</summary>
    private static readonly Dictionary<int, string[]> Aliases = new()
    {
        [0x1F4A8] = ["wind"],              // 💨 dash symbol
        [0x1F3E0] = ["house", "home"],     // 🏠 house building
        [0x1F682] = ["train"],             // 🚂 steam locomotive
        [0x1F3F4] = ["flag", "pirate"],    // 🏴 waving black flag (🏴‍☠️ pirate flag is a ZWJ sequence)
        [0x1F300] = ["cyclone", "tornado", "spiral"],
        [0x1F964] = ["cup", "drink", "milkshake"],
        [0x1F9CB] = ["bubble tea", "drink", "cup"],
        [0x1F333] = ["tree"],
        [0x1F332] = ["tree"],
    };

    public static IconMatch? Match(string title, IconIndex index)
    {
        if (index.Count == 0) return null;

        foreach (var cp in Emojis(title))
        {
            var words = Keywords(cp);
            if (words.Count == 0) continue;
            var best = Best(index, words, includeTags: true);
            if (best is not null) return new IconMatch(best.Icon, $"{char.ConvertFromUtf32(cp)} → {string.Join(" ", words)}");
        }

        var titleWords = Words(StripEmoji(title)).Where(w => w.Length >= 4 && !Filler.Contains(w)).Distinct().ToList();
        foreach (var w in titleWords)
        {
            var best = Best(index, [w], includeTags: false);
            if (best is not null) return new IconMatch(best.Icon, $"title word “{w}”");
        }

        return null;
    }

    /// <summary>Distinct emoji code points in order of appearance.</summary>
    public static IEnumerable<int> Emojis(string text)
    {
        var seen = new HashSet<int>();
        foreach (var rune in text.EnumerateRunes())
        {
            if (EmojiNames.TryGet(rune.Value, out _) && seen.Add(rune.Value))
                yield return rune.Value;
        }
    }

    public static IReadOnlyList<string> Keywords(int codePoint)
    {
        if (Aliases.TryGetValue(codePoint, out var alias)) return alias.SelectMany(Words).Distinct().ToList();
        return EmojiNames.TryGet(codePoint, out var name)
            ? Words(name).Where(w => !Filler.Contains(w)).Distinct().ToList()
            : [];
    }

    /// <summary>Lower-case alphanumeric words, crudely singularised ("trees" → "tree", "berries" → "berry").</summary>
    public static IEnumerable<string> Words(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text.ToLowerInvariant().Append(' '))
        {
            if (char.IsLetterOrDigit(ch)) { sb.Append(ch); continue; }
            if (sb.Length > 0) { yield return Singular(sb.ToString()); sb.Clear(); }
        }
    }

    private static string Singular(string w) =>
        w.Length > 4 && w.EndsWith("ies", StringComparison.Ordinal) ? w[..^3] + "y"
        : w.Length > 4 && (w.EndsWith("ches", StringComparison.Ordinal) || w.EndsWith("shes", StringComparison.Ordinal)
                           || w.EndsWith("sses", StringComparison.Ordinal) || w.EndsWith("xes", StringComparison.Ordinal)) ? w[..^2]
        : w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss", StringComparison.Ordinal) ? w[..^1]
        : w;

    private static string StripEmoji(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
            if (!EmojiNames.TryGet(rune.Value, out _)) sb.Append(rune.ToString());
        return sb.ToString();
    }

    private static IconIndex.Entry? Best(IconIndex index, IReadOnlyList<string> words, bool includeTags)
    {
        IconIndex.Entry? best = null;
        int bestScore = 0;
        foreach (var e in index.Entries)
        {
            if (!e.Matchable) continue;
            var score = 0;
            foreach (var w in words)
            {
                if (e.TitleWords.Contains(w)) score += 3;
                else if (includeTags && e.TagWords.Contains(w)) score += 1;
            }

            if (score == 0) continue;
            if (best is null || score > bestScore
                || (score == bestScore && (e.TitleWords.Count < best.TitleWords.Count
                    || (e.TitleWords.Count == best.TitleWords.Count && string.CompareOrdinal(e.Icon.Title, best.Icon.Title) < 0))))
            {
                best = e;
                bestScore = score;
            }
        }
        return best;
    }
}
