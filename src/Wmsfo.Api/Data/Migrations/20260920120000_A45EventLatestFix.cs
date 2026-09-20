using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class A45EventLatestFix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<JsonDocument>(
                name: "latest_fix",
                table: "event",
                type: "jsonb",
                nullable: true,
                comment: "The last published fix on this event as { seq, beaconId, lat, lng, speedMps, altitudeM, headingDeg, accuracyM, recordedAt, receivedAt }, set in the same update that advances next_seq for the stored and the carried outcome alike (contracts 1.2, 7.2). Null when the event has never had a published fix and cleared by DELETE /admin/events/{id}/locations (contracts 4.5).");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "latest_fix",
                table: "event");
        }
    }
}
