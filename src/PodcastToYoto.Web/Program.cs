using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PodcastToYoto.Web.Components;
using PodcastToYoto.Web.Backups;
using PodcastToYoto.Web.Data;
using PodcastToYoto.Web.Icons;
using PodcastToYoto.Feeds;
using PodcastToYoto.Web.Options;
using PodcastToYoto.Web.Subscriptions;
using PodcastToYoto.Web.Sync;
using PodcastToYoto.Web.Yoto;
using PodcastToYoto.Yoto;

var builder = WebApplication.CreateBuilder(args);

// ---- Options -----------------------------------------------------------------------------------
builder.Services.AddOptions<AppOptions>().BindConfiguration(AppOptions.Section).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<YotoOptions>().BindConfiguration(YotoOptions.Section).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<SyncOptions>().BindConfiguration(SyncOptions.Section);
builder.Services.AddOptions<BackupOptions>().BindConfiguration(BackupOptions.Section);

var appOptions = builder.Configuration.GetSection(AppOptions.Section).Get<AppOptions>() ?? new AppOptions();
var dataDir = Path.GetFullPath(appOptions.DataDirectory, builder.Environment.ContentRootPath);
Directory.CreateDirectory(dataDir);

// ---- Infrastructure ----------------------------------------------------------------------------
builder.Services.AddSingleton(TimeProvider.System);
var paths = new DataPaths(dataDir);
builder.Services.AddSingleton(paths);

builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite($"Data Source={paths.Database}"));

builder.Services.AddDataProtection()
    .SetApplicationName("PodcastToYoto")
    .PersistKeysToFileSystem(new DirectoryInfo(paths.Keys));

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

// Feeds: redirects followed (feeds often move hosts), modest timeout.
builder.Services.AddKeyedSingleton("feeds", (_, _) => new HttpClient(new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(15),
    AutomaticDecompression = DecompressionMethods.All,
    AllowAutoRedirect = true,
})
{
    Timeout = TimeSpan.FromMinutes(1),
});
builder.Services.AddSingleton(sp => new FeedFetcher(sp.GetRequiredKeyedService<HttpClient>("feeds")));
builder.Services.AddSingleton<SubscriptionService>();
builder.Services.AddSingleton<PlaylistService>();
builder.Services.AddSingleton<SubscriptionNotices>();
builder.Services.AddSingleton<PlaylistAdopter>();
builder.Services.AddSingleton<SubscriptionTransfer>();

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
builder.Services.AddSingleton(sp => new YotoAudioUploader(sp.GetRequiredService<YotoApiClient>()));

// ---- Sync --------------------------------------------------------------------------------------
var tempDir = paths.Temp;
if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); // leftovers from a crash
builder.Services.AddKeyedSingleton("downloads", (_, _) => new HttpClient(EpisodeDownloader.CreateHandler())
{
    Timeout = TimeSpan.FromMinutes(10),
});
builder.Services.AddSingleton(sp => new EpisodeDownloader(sp.GetRequiredKeyedService<HttpClient>("downloads"), tempDir));
builder.Services.AddSingleton<AudioPipeline>();
builder.Services.AddSingleton<IconCatalog>();
builder.Services.AddHttpClient(LogoIconGenerator.HttpClientName, c => c.Timeout = TimeSpan.FromMinutes(1));
builder.Services.AddSingleton<LogoIconGenerator>();
builder.Services.AddSingleton<SyncActivity>();
builder.Services.AddOptions<HomeAssistantOptions>().BindConfiguration(HomeAssistantOptions.Section);
builder.Services.AddHttpClient(HomeAssistantChannel.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<INotificationChannel, LogNotificationChannel>();
builder.Services.AddSingleton<INotificationChannel, HomeAssistantChannel>();
builder.Services.AddSingleton<Notifier>();
builder.Services.AddSingleton<INotificationSink>(sp => sp.GetRequiredService<Notifier>());
builder.Services.AddSingleton<SyncEngine>();
builder.Services.AddSingleton<SyncScheduler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SyncScheduler>());

// ---- Backups -----------------------------------------------------------------------------------
// One instance serves both the UI (Settings → Back up now) and the hosted nightly loop.
builder.Services.AddSingleton<BackupService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BackupService>());

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
app.MapGet("/backups/{name}", (string name, BackupService backups) =>
    backups.Resolve(name) is { } path
        ? Results.File(path, "application/zip", name)
        : Results.NotFound()).ExcludeFromDescription();
app.MapGet("/export.json", async (SubscriptionTransfer transfer, TimeProvider time, CancellationToken ct) =>
{
    var file = await transfer.ExportAsync(ct);
    var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(file, SubscriptionTransfer.Json);
    return Results.File(bytes, "application/json", $"podcast-to-yoto-{time.GetUtcNow():yyyyMMdd-HHmm}.json");
}).ExcludeFromDescription();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Logger.LogInformation("Data directory: {DataDir}; public URL: {PublicUrl}",
    dataDir, app.Services.GetRequiredService<IOptions<AppOptions>>().Value.PublicBaseUrl);

app.Run();
