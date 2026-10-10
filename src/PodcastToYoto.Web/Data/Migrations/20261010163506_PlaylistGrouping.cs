using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PodcastToYoto.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class PlaylistGrouping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "UseOwnOrder",
                table: "Subscriptions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "GroupByPodcast",
                table: "Playlists",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // The old three-way track order: "GroupedByPodcast" grouped each podcast in its own order.
            migrationBuilder.Sql("""
                UPDATE "Subscriptions" SET "UseOwnOrder" = 1
                WHERE "PlaylistId" IN (SELECT "Id" FROM "Playlists" WHERE "Order" = 'GroupedByPodcast');
                """);
            migrationBuilder.Sql("""
                UPDATE "Playlists" SET "GroupByPodcast" = 1, "Order" = 'NewestFirst' WHERE "Order" = 'GroupedByPodcast';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""UPDATE "Playlists" SET "Order" = 'GroupedByPodcast' WHERE "GroupByPodcast" = 1;""");

            migrationBuilder.DropColumn(
                name: "UseOwnOrder",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "GroupByPodcast",
                table: "Playlists");
        }
    }
}
