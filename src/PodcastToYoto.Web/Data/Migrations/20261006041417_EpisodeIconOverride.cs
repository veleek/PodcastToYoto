using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PodcastToYoto.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class EpisodeIconOverride : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IconMediaId",
                table: "Episodes",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IconMediaId",
                table: "Episodes");
        }
    }
}
