using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PodcastToYoto.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class YotoPlaylistSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IncludeAll",
                table: "Subscriptions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "Subscriptions",
                type: "TEXT",
                nullable: false,
                defaultValue: "Podcast"); // existing rows are podcasts

            migrationBuilder.AddColumn<string>(
                name: "SourceCardId",
                table: "Subscriptions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceIconRef",
                table: "Episodes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceIndex",
                table: "Episodes",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IncludeAll",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "SourceCardId",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "SourceIconRef",
                table: "Episodes");

            migrationBuilder.DropColumn(
                name: "SourceIndex",
                table: "Episodes");
        }
    }
}
