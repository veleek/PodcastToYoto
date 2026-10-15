using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

// StreamProbe — throwaway server for the stream-track spike (see notes in docs/stream-triggers.md).
// Serves an MP3 for Yoto "stream" tracks and logs everything about each request the player makes.
//
//   GET|HEAD /p/{anything}            serve the probe audio (Range supported)
//   GET|HEAD /t/{trigger}/{anything}  same, and fire the Home Assistant webhook for {trigger}
//     ?delay=ms     wait before answering (timeout tolerance)
//     ?status=404   answer with that status instead
//     ?redirect=1   302 to the same URL without "redirect" (redirect handling)
//   GET /hits[?n=50]                  recent requests as JSON (keep this behind auth)
//
// Config (appsettings / env / args): Probe:Audio, Probe:DataDir, Probe:HaWebhookUrl, Probe:TriggerDedupe.

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; });
builder.Logging.AddFilter("Microsoft", LogLevel.Warning).AddFilter("System.Net.Http", LogLevel.Warning);
builder.Services.AddHttpClient();

var app = builder.Build();
var config = app.Configuration;
var audioPath = Path.GetFullPath(config["Probe:Audio"] ?? Path.Combine(AppContext.BaseDirectory, "probe.mp3"));
var dataDir = Path.GetFullPath(config["Probe:DataDir"] ?? ".stream-probe");
var webhook = config["Probe:HaWebhookUrl"];
var dedupe = TimeSpan.TryParse(config["Probe:TriggerDedupe"], out var d) ? d : TimeSpan.FromMinutes(1);
Directory.CreateDirectory(dataDir);
var logPath = Path.Combine(dataDir, "hits.jsonl");
if (!File.Exists(audioPath)) throw new FileNotFoundException("Probe:Audio not found", audioPath);

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = false };
var recent = new ConcurrentQueue<Hit>();
var lastFired = new ConcurrentDictionary<string, DateTimeOffset>();
var writeLock = new SemaphoreSlim(1);
long nextId = 0;
var log = app.Logger;

log.LogInformation("Audio {Audio}; log {Log}; HA webhook {Webhook}", audioPath, logPath, webhook is null ? "off" : "on");

// Log every request except /hits: headers on arrival (a streamed response can stay open for minutes),
// then status, bytes actually sent, duration and whether the client hung up.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/hits")) { await next(); return; }

    var id = Interlocked.Increment(ref nextId);
    var at = DateTimeOffset.UtcNow;
    var headers = ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
    var remote = ctx.Connection.RemoteIpAddress?.ToString();
    var client = headers.TryGetValue("X-Forwarded-For", out var xff) ? xff.Split(',')[0].Trim() : remote;
    var url = ctx.Request.Path + ctx.Request.QueryString;
    headers.TryGetValue("Range", out var range);
    headers.TryGetValue("User-Agent", out var ua);
    log.LogInformation("#{Id} → {Method} {Url} from {Client} range={Range} ua={UA}", id, ctx.Request.Method, url, client, range ?? "-", ua ?? "-");

    var counter = new CountingStream(ctx.Response.Body);
    ctx.Response.Body = counter;
    ctx.Items["hit"] = id;
    var sw = Stopwatch.StartNew();
    string? error = null;
    try { await next(); }
    catch (Exception ex) when (ex is OperationCanceledException or IOException) { error = ex.GetType().Name; }
    finally
    {
        sw.Stop();
        var hit = new Hit(id, at, ctx.Request.Method, url, client, remote, ctx.Request.Protocol, headers,
            ctx.Response.StatusCode, counter.Written, Math.Round(sw.Elapsed.TotalMilliseconds),
            ctx.RequestAborted.IsCancellationRequested || error is not null, ctx.Items["trigger"] as string);
        log.LogInformation("#{Id} ← {Status} {Bytes:N0} B in {Ms:N0} ms{Aborted}{Trigger}", id, hit.Status, hit.Bytes, hit.Ms,
            hit.Aborted ? " (client aborted)" : "", hit.Trigger is null ? "" : $" trigger={hit.Trigger}");
        recent.Enqueue(hit);
        while (recent.Count > 500) recent.TryDequeue(out _);
        await writeLock.WaitAsync();
        try { await File.AppendAllTextAsync(logPath, JsonSerializer.Serialize(hit, json) + "\n"); }
        finally { writeLock.Release(); }
    }
});

app.MapMethods("/p/{**rest}", ["GET", "HEAD"], (HttpRequest req) => Serve(req.HttpContext));
app.MapMethods("/t/{trigger}/{**rest}", ["GET", "HEAD"], (HttpContext ctx, string trigger, IHttpClientFactory http) =>
{
    // Only the start of a playback counts: not HEAD, not a later Range chunk, and not again within the dedupe window.
    var range = ctx.Request.Headers.Range.ToString();
    var isStart = HttpMethods.IsGet(ctx.Request.Method) && (range.Length == 0 || range.StartsWith("bytes=0-"));
    var now = DateTimeOffset.UtcNow;
    if (isStart && now - lastFired.GetValueOrDefault(trigger) >= dedupe)
    {
        lastFired[trigger] = now;
        ctx.Items["trigger"] = webhook is null ? $"{trigger} (no webhook)" : trigger;
        if (webhook is not null) _ = Fire(http, trigger, ctx, now);
    }
    return Serve(ctx);
});
app.MapGet("/hits", (int? n) => Results.Json(recent.Reverse().Take(n ?? 50), new JsonSerializerOptions(json) { WriteIndented = true }));
app.MapFallback(() => Results.NotFound());

app.Run();

async Task<IResult> Serve(HttpContext ctx)
{
    var q = ctx.Request.Query;
    if (int.TryParse(q["delay"], out var delay)) await Task.Delay(delay, ctx.RequestAborted);
    if (int.TryParse(q["status"], out var status)) return Results.StatusCode(status);
    if (q.ContainsKey("redirect"))
    {
        var rest = q.Where(kv => kv.Key != "redirect").Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value.ToString())}");
        var proto = ctx.Request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? ctx.Request.Scheme;
        var query = string.Join('&', rest);
        return Results.Redirect($"{proto}://{ctx.Request.Host}{ctx.Request.PathBase}{ctx.Request.Path}{(query.Length > 0 ? "?" + query : "")}");
    }
    return Results.File(audioPath, "audio/mpeg", enableRangeProcessing: true);
}

async Task Fire(IHttpClientFactory http, string trigger, HttpContext ctx, DateTimeOffset at)
{
    var body = new
    {
        trigger,
        at,
        hit = ctx.Items["hit"],
        client = ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim() ?? ctx.Connection.RemoteIpAddress?.ToString(),
        userAgent = ctx.Request.Headers.UserAgent.ToString(),
        path = ctx.Request.Path.Value,
    };
    var sw = Stopwatch.StartNew();
    try
    {
        // Buffered (not chunked) so the body arrives with a Content-Length.
        using var content = new StringContent(JsonSerializer.Serialize(body, json), System.Text.Encoding.UTF8, "application/json");
        using var resp = await http.CreateClient().PostAsync(webhook, content);
        log.LogInformation("Webhook {Trigger} → {Status} in {Ms:N0} ms", trigger, (int)resp.StatusCode, sw.ElapsedMilliseconds);
    }
    catch (Exception ex)
    {
        log.LogWarning("Webhook {Trigger} failed: {Error}", trigger, ex.Message);
    }
}

sealed record Hit(
    long Id, DateTimeOffset At, string Method, string Url, string? Client, string? RemoteIp, string Protocol,
    Dictionary<string, string> Headers, int Status, long Bytes, double Ms, bool Aborted, string? Trigger);

/// <summary>Pass-through response stream that counts bytes, so the log shows how much audio the player actually pulled.</summary>
sealed class CountingStream(Stream inner) : Stream
{
    public long Written { get; private set; }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, count); Written += count; }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await inner.WriteAsync(buffer, ct);
        Written += buffer.Length;
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
}
