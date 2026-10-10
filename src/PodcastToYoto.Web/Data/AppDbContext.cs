using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace PodcastToYoto.Web.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<YotoAccount> YotoAccounts => Set<YotoAccount>();
    public DbSet<Playlist> Playlists => Set<Playlist>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Episode> Episodes => Set<Episode>();
    public DbSet<Audio> Audio => Set<Audio>();
    public DbSet<SyncRun> SyncRuns => Set<SyncRun>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // SQLite can't order/compare DateTimeOffset natively; store as long. (Also applies to DateTimeOffset?.)
        builder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();

        // Enums as readable strings.
        builder.Properties<AccountStatus>().HaveConversion<string>();
        builder.Properties<PlaylistOrder>().HaveConversion<string>();
        builder.Properties<LogoIconMode>().HaveConversion<string>();
        builder.Properties<SyncTrigger>().HaveConversion<string>();
        builder.Properties<SyncResult>().HaveConversion<string>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<YotoAccount>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
        });

        b.Entity<Playlist>(e =>
        {
            e.HasMany(x => x.Subscriptions).WithOne(x => x.Playlist!).HasForeignKey(x => x.PlaylistId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Subscription>(e =>
        {
            e.HasIndex(x => new { x.PlaylistId, x.Position });
            e.HasMany(x => x.Episodes).WithOne(x => x.Subscription!).HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Episode>(e =>
        {
            e.HasIndex(x => new { x.SubscriptionId, x.Guid }).IsUnique();
            e.HasIndex(x => new { x.SubscriptionId, x.PublishedAt });
            e.HasOne(x => x.Audio).WithMany().HasForeignKey(x => x.AudioId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Audio>(e =>
        {
            e.HasIndex(x => x.SourceSha256).IsUnique();
        });

        b.Entity<SyncRun>(e =>
        {
            e.HasIndex(x => new { x.PlaylistId, x.StartedAt });
            e.HasOne(x => x.Playlist).WithMany().HasForeignKey(x => x.PlaylistId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
