using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class A142EventTrackerStartView : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<JsonDocument>(
                name: "tracker_start_view",
                table: "event",
                type: "jsonb",
                nullable: true,
                comment: "Where the tracker map opens: { center: { lat, lng }, zoom }, zoom an integer 3 to 18, no other key. Rule: center lies inside tracker_bbox (west <= lng <= east, south <= lat <= north); a write that names a view outside the box is 400 validation_failed at trackerStartView, and a write that moves the box away from a stored view clears it in the same update. Null means the tracker fits the whole box.");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "tracker_start_view",
                table: "event");
        }
    }
}
