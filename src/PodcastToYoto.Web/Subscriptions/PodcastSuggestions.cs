namespace PodcastToYoto.Web.Subscriptions;

public sealed record PodcastSuggestion(string Name, string FeedUrl, string Note);

/// <summary>Kids' podcasts offered as one-click picks when adding a podcast (feeds checked 2026-10-08).</summary>
public static class PodcastSuggestions
{
    public static readonly IReadOnlyList<PodcastSuggestion> All =
    [
        new("Silly Stories for Kids", "https://feeds.megaphone.fm/EUAET6896760602", "Samuel Ramsden"),
        new("But Why: A Podcast for Curious Kids", "https://podcasts.vpr.net/but-why", "Vermont Public"),
        new("Flip and Mozi", "https://rss.art19.com/guide-to-how-to-be-an-earthling-", "Tinkercast"),
        new("Who Smarted?", "https://feeds.megaphone.fm/GLSS8637059451", "Atomic Entertainment"),
    ];
}
