namespace PodcastToYoto.Yoto;

public sealed class YotoOAuthOptions
{
    public const string DefaultScopes = "user:content:manage offline_access";

    public required string ClientId { get; init; }
    public string Audience { get; init; } = "https://api.yotoplay.com";
    public Uri AuthorizeEndpoint { get; init; } = new("https://login.yotoplay.com/authorize");
    public Uri TokenEndpoint { get; init; } = new("https://login.yotoplay.com/oauth/token");
    public string Scopes { get; init; } = DefaultScopes;

    /// <summary>Refresh this long before the access token actually expires.</summary>
    public TimeSpan RefreshSkew { get; init; } = TimeSpan.FromSeconds(60);
}
