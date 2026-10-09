using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class A99TerrainMaxZoom15 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "tracker_map_terrain_max_zoom_check",
                table: "tracker_map");

            migrationBuilder.AddCheckConstraint(
                name: "tracker_map_terrain_max_zoom_check",
                table: "tracker_map",
                sql: "terrain_max_zoom between 8 and 15");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "tracker_map_terrain_max_zoom_check",
                table: "tracker_map");

            migrationBuilder.AddCheckConstraint(
                name: "tracker_map_terrain_max_zoom_check",
                table: "tracker_map",
                sql: "terrain_max_zoom between 8 and 13");
        }
    }
}
