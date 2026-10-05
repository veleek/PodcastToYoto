using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PodcastToYoto.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Audio",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceSha256 = table.Column<string>(type: "TEXT", nullable: false),
                    TranscodedSha256 = table.Column<string>(type: "TEXT", nullable: false),
                    DurationSeconds = table.Column<long>(type: "INTEGER", nullable: true),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: true),
                    ChannelsJson = table.Column<string>(type: "TEXT", nullable: true),
                    FormatJson = table.Column<string>(type: "TEXT", nullable: true),
                    UploadedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Audio", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Subscriptions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    FeedUrl = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    YotoCardId = table.Column<string>(type: "TEXT", nullable: true),
                    TitleOverride = table.Column<string>(type: "TEXT", nullable: true),
                    RecentCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxTracks = table.Column<int>(type: "INTEGER", nullable: false),
                    Order = table.Column<string>(type: "TEXT", nullable: false),
                    IncludeTrailers = table.Column<bool>(type: "INTEGER", nullable: false),
                    PollInterval = table.Column<TimeSpan>(type: "TEXT", nullable: true),
                    CoverSourceUrl = table.Column<string>(type: "TEXT", nullable: true),
                    CoverMediaUrl = table.Column<string>(type: "TEXT", nullable: true),
                    FeedETag = table.Column<string>(type: "TEXT", nullable: true),
                    FeedLastModified = table.Column<string>(type: "TEXT", nullable: true),
                    LastPolledAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastPublishedHash = table.Column<string>(type: "TEXT", nullable: true),
                    LastSyncedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastSyncResult = table.Column<string>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subscriptions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "YotoAccounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    ProtectedAccessToken = table.Column<string>(type: "TEXT", nullable: true),
                    ProtectedRefreshToken = table.Column<string>(type: "TEXT", nullable: true),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Scope = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    ConnectedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_YotoAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Episodes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SubscriptionId = table.Column<int>(type: "INTEGER", nullable: false),
                    Guid = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    PublishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    EnclosureUrl = table.Column<string>(type: "TEXT", nullable: false),
                    EnclosureLength = table.Column<long>(type: "INTEGER", nullable: true),
                    DurationSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    EpisodeType = table.Column<string>(type: "TEXT", nullable: true),
                    ImageUrl = table.Column<string>(type: "TEXT", nullable: true),
                    IsFavourite = table.Column<bool>(type: "INTEGER", nullable: false),
                    FirstSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    MissingFromFeedSince = table.Column<long>(type: "INTEGER", nullable: true),
                    AudioId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Episodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Episodes_Audio_AudioId",
                        column: x => x.AudioId,
                        principalTable: "Audio",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Episodes_Subscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalTable: "Subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SubscriptionId = table.Column<int>(type: "INTEGER", nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    FinishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Result = table.Column<string>(type: "TEXT", nullable: false),
                    NewEpisodes = table.Column<int>(type: "INTEGER", nullable: false),
                    Uploaded = table.Column<int>(type: "INTEGER", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncRuns_Subscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalTable: "Subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Audio_SourceSha256",
                table: "Audio",
                column: "SourceSha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Episodes_AudioId",
                table: "Episodes",
                column: "AudioId");

            migrationBuilder.CreateIndex(
                name: "IX_Episodes_SubscriptionId_Guid",
                table: "Episodes",
                columns: new[] { "SubscriptionId", "Guid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Episodes_SubscriptionId_PublishedAt",
                table: "Episodes",
                columns: new[] { "SubscriptionId", "PublishedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuns_SubscriptionId_StartedAt",
                table: "SyncRuns",
                columns: new[] { "SubscriptionId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Episodes");

            migrationBuilder.DropTable(
                name: "SyncRuns");

            migrationBuilder.DropTable(
                name: "YotoAccounts");

            migrationBuilder.DropTable(
                name: "Audio");

            migrationBuilder.DropTable(
                name: "Subscriptions");
        }
    }
}
