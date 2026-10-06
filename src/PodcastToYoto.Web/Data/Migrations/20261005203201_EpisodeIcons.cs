using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PodcastToYoto.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class EpisodeIcons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "UseDefaultIcon",
                table: "Subscriptions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UseDefaultIcon",
                table: "Subscriptions");
        }
    }
}
