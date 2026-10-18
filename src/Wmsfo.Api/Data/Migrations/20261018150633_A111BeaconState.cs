using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class A111BeaconState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<JsonDocument>(
                name: "state",
                table: "beacon",
                type: "jsonb",
                nullable: true,
                comment: "The beacon's own state document (contracts 4.2): any JSON object, stored as the beacon wrote it and never interpreted by the API; null until the first write.");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "state_updated_at",
                table: "beacon",
                type: "timestamptz",
                nullable: true,
                comment: "When state was last written; null until the first write.");

            migrationBuilder.AddColumn<long>(
                name: "state_version",
                table: "beacon",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                comment: "Incremented by every write of state; a write names the version it read and changes nothing when the row holds another.");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "state",
                table: "beacon");

            migrationBuilder.DropColumn(
                name: "state_updated_at",
                table: "beacon");

            migrationBuilder.DropColumn(
                name: "state_version",
                table: "beacon");
        }
    }
}
