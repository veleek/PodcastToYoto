namespace PodcastToYoto.Yoto;

public sealed class YotoOAuthOptions
{
    // user:content:view / user:icons:manage are listed explicitly: some endpoints (GET /content/{cardId},
    // public icons) check for them even though user:content:manage is documented to include them.
    public const string DefaultScopes = "user:content:view user:content:manage user:icons:manage offline_access";

    public required string ClientId { get; init; }
    public string Audience { get; init; } = "https://api.yotoplay.com";
    public Uri AuthorizeEndpoint { get; init; } = new("https://login.yotoplay.com/authorize");
    public Uri TokenEndpoint { get; init; } = new("https://login.yotoplay.com/oauth/token");
    public string Scopes { get; init; } = DefaultScopes;

    /// <summary>Refresh this long before the access token actually expires.</summary>
    public TimeSpan RefreshSkew { get; init; } = TimeSpan.FromSeconds(60);
}
