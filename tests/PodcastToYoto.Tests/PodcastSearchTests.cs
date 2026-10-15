using PodcastToYoto.Web.Subscriptions;

namespace PodcastToYoto.Tests;

public class PodcastSearchTests
{
    [Fact]
    public void Parses_itunes_results_and_drops_ones_without_a_feed()
    {
        const string json = """
            {"resultCount":3,"results":[
              {"wrapperType":"track","kind":"podcast","collectionName":"Silly Stories for Kids","artistName":"Samuel Ramsden",
               "feedUrl":"https://feeds.megaphone.fm/EUAET6896760602","artworkUrl100":"https://img/100.jpg","artworkUrl600":"https://img/600.jpg","trackCount":212},
              {"kind":"podcast","collectionName":"Apple only","artistName":"Someone"},
              {"kind":"podcast","collectionName":"Duplicate","feedUrl":"https://feeds.megaphone.fm/EUAET6896760602"}
            ]}
            """;

        var results = PodcastSearch.ParseITunes(json);

        var r = Assert.Single(results);
        Assert.Equal(new PodcastSearchResult("Silly Stories for Kids", "Samuel Ramsden", "https://feeds.megaphone.fm/EUAET6896760602",
            "https://img/600.jpg", 212), r);
    }

    [Fact]
    public void Parses_podcast_index_results_and_drops_dead_feeds()
    {
        const string json = """
            {"status":"true","feeds":[
              {"id":1,"title":"But Why","url":"https://podcasts.vpr.net/but-why","author":"Vermont Public","image":"https://img/i.jpg","artwork":"https://img/a.jpg","episodeCount":400,"dead":0},
              {"id":2,"title":"Gone","url":"https://example.com/gone.xml","dead":1},
              {"id":3,"title":"No art","url":"https://example.com/feed.xml","ownerName":"Owner","image":""}
            ],"count":3}
            """;

        var results = PodcastSearch.ParsePodcastIndex(json);

        Assert.Equal([
            new PodcastSearchResult("But Why", "Vermont Public", "https://podcasts.vpr.net/but-why", "https://img/a.jpg", 400),
            new PodcastSearchResult("No art", "Owner", "https://example.com/feed.xml", null, null),
        ], results);
    }

    [Fact]
    public void Empty_or_unexpected_responses_give_no_results()
    {
        Assert.Empty(PodcastSearch.ParseITunes("{}"));
        Assert.Empty(PodcastSearch.ParsePodcastIndex("""{"status":"false","description":"bad key"}"""));
    }

    [Fact]
    public void Podcast_index_authorization_is_sha1_of_key_secret_and_date() =>
        Assert.Equal("5f8983664e541a83aaae7f3a47f1957fbca48aec", PodcastSearch.PodcastIndexAuthorization("KEY", "SECRET", "1700000000"));
}
