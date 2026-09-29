using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class A67EventRouteMapConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<JsonDocument>(
                name: "route_map_config",
                table: "event",
                type: "jsonb",
                nullable: true,
                comment: "The event's route map configuration { display?, controls?, landmarks?, pois? } in canonical form, validated on write against $defs/RouteMapConfig (contracts 1.3, 4.5). Null means every built-in default; the snapshot carries it for the current event.");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "route_map_config",
                table: "event");
        }
    }
}
