using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PodcastToYoto.Web.Data;
using PodcastToYoto.Web.Options;
using PodcastToYoto.Web.Subscriptions;

namespace PodcastToYoto.Web.Backups;

public sealed record BackupFile(string Name, long Size, DateTimeOffset CreatedAt);

/// <summary>
/// Nightly backup of everything in the data directory that matters, as one zip:
/// <c>app.db</c> (SQLite online backup, safe while the app is running), the Data Protection <c>keys/</c> (needed to
/// decrypt the stored Yoto tokens) and <c>export.json</c> (human-readable subscriptions + favourites + icons).
/// Keeps the newest <see cref="BackupOptions.Keep"/> zips.
/// </summary>
public sealed class BackupService(
    IDbContextFactory<AppDbContext> dbFactory,
    SubscriptionTransfer transfer,
    IOptionsMonitor<BackupOptions> options,
    TimeProvider time,
    ILogger<BackupService> logger,
    DataPaths paths) : BackgroundService
{
    private const string Prefix = "podcast-to-yoto-";
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string BackupDirectory => Path.GetFullPath(options.CurrentValue.Directory, paths.Root);

    public IReadOnlyList<BackupFile> List()
    {
        var dir = new DirectoryInfo(BackupDirectory);
        if (!dir.Exists) return [];
        return dir.GetFiles($"{Prefix}*.zip")
            .OrderByDescending(f => f.Name, StringComparer.Ordinal) // names sort by timestamp
            .Select(f => new BackupFile(f.Name, f.Length, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero)))
            .ToList();
    }

    /// <summary>Full path of a backup by file name, or null if it isn't one of ours (prevents path tricks).</summary>
    public string? Resolve(string name)
    {
        if (!name.StartsWith(Prefix, StringComparison.Ordinal) || !name.EndsWith(".zip", StringComparison.Ordinal)
            || name.Contains('/') || name.Contains('\\') || name.Contains(".."))
            return null;
        var path = Path.Combine(BackupDirectory, name);
        return File.Exists(path) ? path : null;
    }

    public async Task<BackupFile> BackupNowAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var dir = BackupDirectory;
            Directory.CreateDirectory(dir);

            var stamp = time.GetLocalNow().ToString("yyyyMMdd-HHmmss");
            var zipPath = Path.Combine(dir, $"{Prefix}{stamp}.zip");
            var work = Path.Combine(dir, $".work-{stamp}");
            Directory.CreateDirectory(work);

            try
            {
                // 1. SQLite online backup (consistent snapshot while the app keeps running).
                var dbCopy = Path.Combine(work, "app.db");
                await using (var db = await dbFactory.CreateDbContextAsync(ct))
                {
                    using var source = new SqliteConnection(db.Database.GetConnectionString());
                    using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbCopy, Pooling = false }.ToString());
                    source.Open();
                    target.Open();
                    source.BackupDatabase(target);
                }

                // 2. Human-readable export.
                var export = await transfer.ExportAsync(ct);
                await File.WriteAllBytesAsync(Path.Combine(work, "export.json"),
                    JsonSerializer.SerializeToUtf8Bytes(export, SubscriptionTransfer.Json), ct);

                // 3. Zip db + export + Data Protection keys.
                var tmpZip = zipPath + ".tmp";
                using (var zip = ZipFile.Open(tmpZip, ZipArchiveMode.Create))
                {
                    zip.CreateEntryFromFile(dbCopy, "app.db", CompressionLevel.Optimal);
                    zip.CreateEntryFromFile(Path.Combine(work, "export.json"), "export.json", CompressionLevel.Optimal);
                    var keys = new DirectoryInfo(paths.Keys);
                    if (keys.Exists)
                        foreach (var key in keys.GetFiles("*.xml"))
                            zip.CreateEntryFromFile(key.FullName, $"keys/{key.Name}", CompressionLevel.Optimal);
                }
                File.Move(tmpZip, zipPath, overwrite: true);
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch (IOException) { }
            }

            Prune();
            var info = new FileInfo(zipPath);
            logger.LogInformation("Backup written: {Path} ({SizeKb:F0} KB)", zipPath, info.Length / 1024.0);
            return new BackupFile(info.Name, info.Length, time.GetUtcNow());
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Prune()
    {
        var keep = Math.Max(1, options.CurrentValue.Keep);
        foreach (var old in List().Skip(keep))
        {
            try
            {
                File.Delete(Path.Combine(BackupDirectory, old.Name));
                logger.LogInformation("Deleted old backup {Name}", old.Name);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Couldn't delete old backup {Name}", old.Name);
            }
        }
    }

    /// <summary>Next scheduled run (local time), or null when disabled.</summary>
    public DateTimeOffset? NextRun()
    {
        var o = options.CurrentValue;
        if (!o.Enabled) return null;
        var now = time.GetLocalNow();
        var today = new DateTimeOffset(DateOnly.FromDateTime(now.DateTime).ToDateTime(o.TimeOfDay), now.Offset);
        return today > now ? today : today.AddDays(1);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var next = NextRun();
            var wait = next is null ? TimeSpan.FromHours(1) : next.Value - time.GetLocalNow();
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            // Re-evaluate at least hourly so config / DST / clock changes are picked up.
            try
            {
                await Task.Delay(wait < TimeSpan.FromHours(1) ? wait : TimeSpan.FromHours(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (next is null || time.GetLocalNow() < next.Value) continue;

            try
            {
                await BackupNowAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Nightly backup failed");
            }
        }
    }
}
