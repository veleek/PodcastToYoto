using System.Text.Json;
using System.Text.Json.Serialization;

namespace PodcastToYoto.Yoto;

/// <summary>OAuth2 authorization-code + PKCE against login.yotoplay.com.</summary>
public sealed class YotoAuthClient(HttpClient http, YotoOAuthOptions options, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public Uri BuildAuthorizeUrl(string redirectUri, string state, PkcePair pkce)
    {
        var query = new Dictionary<string, string>
        {
            ["audience"] = options.Audience,
            ["client_id"] = options.ClientId,
            ["response_type"] = "code",
            ["scope"] = options.Scopes,
            ["redirect_uri"] = redirectUri,
            ["state"] = state,
            ["code_challenge"] = pkce.Challenge,
            ["code_challenge_method"] = "S256",
        };
        var qs = string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return new Uri($"{options.AuthorizeEndpoint}?{qs}");
    }

    public Task<YotoTokens> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri, CancellationToken ct = default) =>
        RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = options.ClientId,
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["redirect_uri"] = redirectUri,
        }, previousRefreshToken: null, ct);

    public Task<YotoTokens> RefreshAsync(string refreshToken, CancellationToken ct = default) =>
        RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = options.ClientId,
            ["refresh_token"] = refreshToken,
        }, previousRefreshToken: refreshToken, ct);

    private async Task<YotoTokens> RequestTokenAsync(Dictionary<string, string> form, string? previousRefreshToken, CancellationToken ct)
    {
        using var resp = await http.PostAsync(options.TokenEndpoint, new FormUrlEncodedContent(form), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new YotoAuthException($"Token request ({form["grant_type"]}) failed: {(int)resp.StatusCode} {body}", (int)resp.StatusCode);

        var token = JsonSerializer.Deserialize<TokenResponse>(body)
                    ?? throw new YotoAuthException("Empty token response", (int)resp.StatusCode);

        var refresh = token.RefreshToken ?? previousRefreshToken
                      ?? throw new YotoAuthException("No refresh_token returned. Was offline_access requested?", (int)resp.StatusCode);

        return new YotoTokens(
            token.AccessToken,
            refresh,
            _time.GetUtcNow().AddSeconds(token.ExpiresIn),
            token.Scope);
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("scope")] string? Scope,
        [property: JsonPropertyName("token_type")] string? TokenType);
}

public sealed class YotoAuthException(string message, int? statusCode = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
}
