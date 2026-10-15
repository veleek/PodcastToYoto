using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PodcastToYoto.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class YotoSourcePickNewTracks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // "Every track" (IncludeAll) becomes "pick new tracks automatically": pick the tracks it covered so the plan
            // doesn't change. Yoto sources also gain "first N tracks", which was left at the podcast default: none.
            migrationBuilder.Sql("""
                UPDATE Episodes SET IsFavourite = 1
                WHERE MissingFromFeedSince IS NULL
                  AND SubscriptionId IN (SELECT Id FROM Subscriptions WHERE Kind = 'YotoPlaylist' AND IncludeAll = 1);
                UPDATE Subscriptions SET RecentCount = 0, UseOwnOrder = 1 WHERE Kind = 'YotoPlaylist';
                """);

            migrationBuilder.RenameColumn(
                name: "IncludeAll",
                table: "Subscriptions",
                newName: "PickNewTracks");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "PickNewTracks",
                table: "Subscriptions",
                newName: "IncludeAll");
        }
    }
}
