using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using PodcastToYoto.Web.Options;
using PodcastToYoto.Yoto;

namespace PodcastToYoto.Web.Yoto;

/// <summary>
/// Browser OAuth flow: /auth/login → Yoto → /auth/callback. PKCE verifier + state are kept in a short-lived,
/// Data-Protected cookie so nothing needs server-side session state.
/// </summary>
public static class YotoAuthEndpoints
{
    private const string CookieName = "yoto_login";
    private const string ProtectorPurpose = "PodcastToYoto.YotoLogin.v1";

    private sealed record LoginState(string State, string Verifier, DateTimeOffset IssuedAt);

    public static IEndpointRouteBuilder MapYotoAuth(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth").ExcludeFromDescription();

        group.MapGet("/login", (HttpContext http, YotoAuthClient auth, IOptions<AppOptions> appOptions,
            IDataProtectionProvider dp, TimeProvider time) =>
        {
            // The state cookie must be set on the same host Yoto redirects back to.
            var publicBase = new Uri(appOptions.Value.PublicBaseUrl);
            if (!string.Equals(http.Request.Host.Host, publicBase.Host, StringComparison.OrdinalIgnoreCase))
                return Results.Redirect(new Uri(publicBase, "/auth/login").AbsoluteUri);

            var pkce = PkcePair.Create();
            var state = PkcePair.CreateState();
            var protector = dp.CreateProtector(ProtectorPurpose);
            var payload = protector.Protect(JsonSerializer.Serialize(new LoginState(state, pkce.Verifier, time.GetUtcNow())));

            http.Response.Cookies.Append(CookieName, payload, new CookieOptions
            {
                HttpOnly = true,
                Secure = appOptions.Value.PublicBaseUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase),
                SameSite = SameSiteMode.Lax, // must survive the top-level redirect back from login.yotoplay.com
                Path = "/auth",
                MaxAge = TimeSpan.FromMinutes(10),
            });

            var url = auth.BuildAuthorizeUrl(appOptions.Value.YotoRedirectUri, state, pkce);
            return Results.Redirect(url.AbsoluteUri);
        });

        group.MapGet("/callback", async (HttpContext http, YotoAuthClient auth, YotoTokenProvider tokens,
            IOptions<AppOptions> appOptions, IDataProtectionProvider dp, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("YotoAuth");
            var q = http.Request.Query;

            IResult Fail(string message)
            {
                logger.LogWarning("Yoto login failed: {Message}", message);
                http.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/auth" });
                return Results.Redirect("/?authError=" + Uri.EscapeDataString(message));
            }

            if (!string.IsNullOrEmpty(q["error"]))
                return Fail($"{q["error"]}: {q["error_description"]}");

            var code = q["code"].ToString();
            if (string.IsNullOrEmpty(code))
                return Fail("No authorization code returned.");

            if (!http.Request.Cookies.TryGetValue(CookieName, out var cookie))
                return Fail("Login session expired or cookie missing. Try again.");

            LoginState? login;
            try
            {
                login = JsonSerializer.Deserialize<LoginState>(dp.CreateProtector(ProtectorPurpose).Unprotect(cookie));
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException)
            {
                return Fail("Login session cookie was invalid. Try again.");
            }

            if (login is null || !string.Equals(login.State, q["state"].ToString(), StringComparison.Ordinal))
                return Fail("State mismatch. Try again.");

            try
            {
                var t = await auth.ExchangeCodeAsync(code, login.Verifier, appOptions.Value.YotoRedirectUri, ct);
                await tokens.SetTokensAsync(t, ct);
                logger.LogInformation("Yoto account connected; scopes {Scopes}, token expires {ExpiresAt:u}", t.Scope, t.ExpiresAt);
            }
            catch (YotoAuthException ex)
            {
                return Fail(ex.Message);
            }

            http.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/auth" });
            return Results.Redirect("/");
        });

        return app;
    }
}
