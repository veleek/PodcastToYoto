using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PodcastToYoto.Web.Data.Migrations
{
    /// <summary>
    /// Playlists become the top-level entity and can draw from several podcasts. Each existing subscription becomes a
    /// one-podcast playlist with the same id, Yoto playlist, title, cover, schedule, sync state and history, so nothing is
    /// re-uploaded or republished.
    /// </summary>
    public partial class MultiPodcastPlaylists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Playlists",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    YotoCardId = table.Column<string>(type: "TEXT", nullable: true),
                    MaxTracks = table.Column<int>(type: "INTEGER", nullable: false),
                    Order = table.Column<string>(type: "TEXT", nullable: false),
                    PollInterval = table.Column<TimeSpan>(type: "TEXT", nullable: true),
                    CoverImageUrl = table.Column<string>(type: "TEXT", nullable: true),
                    CoverSourceUrl = table.Column<string>(type: "TEXT", nullable: true),
                    CoverMediaUrl = table.Column<string>(type: "TEXT", nullable: true),
                    LastPublishedHash = table.Column<string>(type: "TEXT", nullable: true),
                    LastSyncedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastSyncResult = table.Column<string>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Playlists", x => x.Id);
                });

            // One playlist per subscription, sharing its id. CoverSourceUrl records what the existing cover was uploaded
            // from, so it isn't uploaded again.
            migrationBuilder.Sql("""
                INSERT INTO "Playlists" ("Id", "Name", "Enabled", "YotoCardId", "MaxTracks", "Order", "PollInterval",
                    "CoverImageUrl", "CoverSourceUrl", "CoverMediaUrl", "LastPublishedHash", "LastSyncedAt", "LastSyncResult",
                    "LastError", "CreatedAt")
                SELECT "Id", COALESCE("TitleOverride", "Name"), "Enabled", "YotoCardId", "MaxTracks", "Order", "PollInterval",
                    NULL, CASE WHEN "CoverMediaUrl" IS NULL THEN NULL ELSE "CoverSourceUrl" END, "CoverMediaUrl",
                    "LastPublishedHash", "LastSyncedAt", "LastSyncResult", "LastError", "CreatedAt"
                FROM "Subscriptions";
                """);

            migrationBuilder.AddColumn<int>(
                name: "PlaylistId",
                table: "Subscriptions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Position",
                table: "Subscriptions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LogoIcon",
                table: "Subscriptions",
                type: "TEXT",
                nullable: false,
                defaultValue: "Off");

            migrationBuilder.AddColumn<string>(
                name: "LogoIconMediaId",
                table: "Subscriptions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LogoIconUrl",
                table: "Subscriptions",
                type: "TEXT",
                nullable: true);

            // Enabled now means "included in the playlist"; automatic syncing moved to the playlist. Only feed errors stay
            // on the podcast; sync errors moved to the playlist.
            migrationBuilder.Sql("""
                UPDATE "Subscriptions" SET "PlaylistId" = "Id", "Position" = 0, "Enabled" = 1,
                    "LastError" = CASE WHEN "LastError" LIKE 'Feed:%' THEN "LastError" ELSE NULL END;
                """);

            // Sync history moves to the playlist with the same id.
            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuns_Subscriptions_SubscriptionId",
                table: "SyncRuns");

            migrationBuilder.RenameColumn(
                name: "SubscriptionId",
                table: "SyncRuns",
                newName: "PlaylistId");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRuns_SubscriptionId_StartedAt",
                table: "SyncRuns",
                newName: "IX_SyncRuns_PlaylistId_StartedAt");

            migrationBuilder.DropColumn(name: "YotoCardId", table: "Subscriptions");
            migrationBuilder.DropColumn(name: "TitleOverride", table: "Subscriptions");
            migrationBuilder.DropColumn(name: "PollInterval", table: "Subscriptions");
            migrationBuilder.DropColumn(name: "CoverMediaUrl", table: "Subscriptions");
            migrationBuilder.DropColumn(name: "LastPublishedHash", table: "Subscriptions");
            migrationBuilder.DropColumn(name: "LastSyncedAt", table: "Subscriptions");
            migrationBuilder.DropColumn(name: "LastSyncResult", table: "Subscriptions");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_PlaylistId_Position",
                table: "Subscriptions",
                columns: new[] { "PlaylistId", "Position" });

            migrationBuilder.AddForeignKey(
                name: "FK_Subscriptions_Playlists_PlaylistId",
                table: "Subscriptions",
                column: "PlaylistId",
                principalTable: "Playlists",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuns_Playlists_PlaylistId",
                table: "SyncRuns",
                column: "PlaylistId",
                principalTable: "Playlists",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <summary>
        /// Back to one playlist per subscription. Each podcast takes its playlist's Yoto playlist and sync state; history
        /// goes to the playlist's first podcast. Playlists with several podcasts end up with several subscriptions pointing
        /// at the same Yoto playlist, so detach all but one before syncing.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // EF runs SQLite table rebuilds last, so "Playlists" is dropped while still referenced; with foreign keys on
            // that would cascade-delete every podcast and episode. (EF turns them back on after its rebuilds.)
            migrationBuilder.Sql("PRAGMA foreign_keys = 0;", suppressTransaction: true);

            migrationBuilder.AddColumn<string>(name: "YotoCardId", table: "Subscriptions", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<string>(name: "TitleOverride", table: "Subscriptions", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<TimeSpan>(name: "PollInterval", table: "Subscriptions", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<string>(name: "CoverMediaUrl", table: "Subscriptions", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<string>(name: "LastPublishedHash", table: "Subscriptions", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<long>(name: "LastSyncedAt", table: "Subscriptions", type: "INTEGER", nullable: true);
            migrationBuilder.AddColumn<string>(name: "LastSyncResult", table: "Subscriptions", type: "TEXT", nullable: true);

            migrationBuilder.Sql("""
                UPDATE "Subscriptions" SET
                    "YotoCardId" = (SELECT p."YotoCardId" FROM "Playlists" p WHERE p."Id" = "Subscriptions"."PlaylistId"),
                    "TitleOverride" = (SELECT p."Name" FROM "Playlists" p WHERE p."Id" = "Subscriptions"."PlaylistId"),
                    "PollInterval" = (SELECT p."PollInterval" FROM "Playlists" p WHERE p."Id" = "Subscriptions"."PlaylistId"),
                    "CoverMediaUrl" = (SELECT p."CoverMediaUrl" FROM "Playlists" p WHERE p."Id" = "Subscriptions"."PlaylistId"),
                    "LastPublishedHash" = (SELECT p."LastPublishedHash" FROM "Playlists" p WHERE p."Id" = "Subscriptions"."PlaylistId"),
                    "LastSyncedAt" = (SELECT p."LastSyncedAt" FROM "Playlists" p WHERE p."Id" = "Subscriptions"."PlaylistId"),
                    "LastSyncResult" = (SELECT p."LastSyncResult" FROM "Playlists" p WHERE p."Id" = "Subscriptions"."PlaylistId"),
                    "Enabled" = "Enabled" AND (SELECT p."Enabled" FROM "Playlists" p WHERE p."Id" = "Subscriptions"."PlaylistId");
                UPDATE "SyncRuns" SET "PlaylistId" = COALESCE(
                    (SELECT s."Id" FROM "Subscriptions" s WHERE s."PlaylistId" = "SyncRuns"."PlaylistId" ORDER BY s."Position", s."Id" LIMIT 1),
                    "PlaylistId");
                DELETE FROM "SyncRuns" WHERE "PlaylistId" NOT IN (SELECT "Id" FROM "Subscriptions");
                """);

            migrationBuilder.DropForeignKey(name: "FK_Subscriptions_Playlists_PlaylistId", table: "Subscriptions");
            migrationBuilder.DropForeignKey(name: "FK_SyncRuns_Playlists_PlaylistId", table: "SyncRuns");
            migrationBuilder.DropIndex(name: "IX_Subscriptions_PlaylistId_Position", table: "Subscriptions");
            migrationBuilder.DropColumn(name: "PlaylistId", table: "Subscriptions");
            migrationBuilder.DropColumn(name: "Position", table: "Subscriptions");
            migrationBuilder.DropColumn(name: "LogoIcon", table: "Subscriptions");
            migrationBuilder.DropColumn(name: "LogoIconMediaId", table: "Subscriptions");
            migrationBuilder.DropColumn(name: "LogoIconUrl", table: "Subscriptions");
            migrationBuilder.RenameColumn(name: "PlaylistId", table: "SyncRuns", newName: "SubscriptionId");
            migrationBuilder.RenameIndex(name: "IX_SyncRuns_PlaylistId_StartedAt", table: "SyncRuns", newName: "IX_SyncRuns_SubscriptionId_StartedAt");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuns_Subscriptions_SubscriptionId",
                table: "SyncRuns",
                column: "SubscriptionId",
                principalTable: "Subscriptions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.DropTable(name: "Playlists");
        }
    }
}
