using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PodcastToYoto.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class TrackRotation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RotationNumber",
                table: "Subscriptions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "RotationStartedAt",
                table: "Subscriptions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Selection",
                table: "Subscriptions",
                type: "TEXT",
                nullable: false,
                defaultValue: "First"); // existing sources keep the first N

            migrationBuilder.AddColumn<int>(
                name: "RotationNumber",
                table: "Episodes",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RotationNumber",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "RotationStartedAt",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "Selection",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "RotationNumber",
                table: "Episodes");
        }
    }
}
