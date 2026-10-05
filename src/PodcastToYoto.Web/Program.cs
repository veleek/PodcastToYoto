using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PodcastToYoto.Web.Components;
using PodcastToYoto.Web.Data;
using PodcastToYoto.Web.Options;
using PodcastToYoto.Web.Yoto;
using PodcastToYoto.Yoto;

var builder = WebApplication.CreateBuilder(args);

// ---- Options -----------------------------------------------------------------------------------
builder.Services.AddOptions<AppOptions>().BindConfiguration(AppOptions.Section).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<YotoOptions>().BindConfiguration(YotoOptions.Section).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<SyncOptions>().BindConfiguration(SyncOptions.Section);

var appOptions = builder.Configuration.GetSection(AppOptions.Section).Get<AppOptions>() ?? new AppOptions();
var dataDir = Path.GetFullPath(appOptions.DataDirectory, builder.Environment.ContentRootPath);
Directory.CreateDirectory(dataDir);

// ---- Infrastructure ----------------------------------------------------------------------------
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(dataDir, "app.db")}"));

builder.Services.AddDataProtection()
    .SetApplicationName("PodcastToYoto")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));

// Behind Traefik: trust X-Forwarded-* (the app must only be reachable through the proxy).
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

// One long-lived HttpClient for Yoto (pooled connections recycled for DNS changes).
builder.Services.AddKeyedSingleton("yoto", (_, _) => new HttpClient(new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(15),
    AutomaticDecompression = DecompressionMethods.All,
})
{
    Timeout = TimeSpan.FromMinutes(10),
});

// ---- Yoto --------------------------------------------------------------------------------------
builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<IOptions<YotoOptions>>().Value;
    return new YotoOAuthOptions { ClientId = o.ClientId, Scopes = o.Scopes };
});
builder.Services.AddSingleton<ITokenStore, DbTokenStore>();
builder.Services.AddSingleton(sp => new YotoAuthClient(
    sp.GetRequiredKeyedService<HttpClient>("yoto"), sp.GetRequiredService<YotoOAuthOptions>(), sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(sp => new YotoTokenProvider(
    sp.GetRequiredService<YotoAuthClient>(), sp.GetRequiredService<ITokenStore>(), sp.GetRequiredService<YotoOAuthOptions>(), sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(sp => new YotoApiClient(
    sp.GetRequiredKeyedService<HttpClient>("yoto"), sp.GetRequiredService<YotoTokenProvider>()));
builder.Services.AddSingleton<YotoAccountService>();

// ---- Web ---------------------------------------------------------------------------------------
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddHealthChecks().AddCheck<YotoAccountHealthCheck>("yoto");

var app = builder.Build();

// Apply migrations on startup (single-instance app, SQLite).
await using (var scope = app.Services.CreateAsyncScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
}

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
}

app.UseAntiforgery();

app.MapStaticAssets();
app.MapHealthChecks("/health");
app.MapYotoAuth();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Logger.LogInformation("Data directory: {DataDir}; public URL: {PublicUrl}",
    dataDir, app.Services.GetRequiredService<IOptions<AppOptions>>().Value.PublicBaseUrl);

app.Run();
