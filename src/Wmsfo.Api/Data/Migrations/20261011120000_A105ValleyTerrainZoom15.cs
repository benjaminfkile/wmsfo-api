using Microsoft.EntityFrameworkCore.Migrations;
using Wmsfo.Api.Themes;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class A105ValleyTerrainZoom15 : Migration
    {
        // Moves the seeded valley map from the terrain zoom and package key
        // A92TrackerMapsThemes inserts to ValleyTerrainMaxZoom and the key of
        // the identity at that zoom. A row at any other terrain zoom is left
        // as it is.
        public static readonly string UpSql = $@"
update tracker_map
set terrain_max_zoom = {TrackerThemeSeed.ValleyTerrainMaxZoom}, package_key = '{TrackerThemeSeed.ValleyPackageKey}'
where prefix = '{TrackerThemeSeed.ValleyPrefix}' and terrain_max_zoom = {TrackerThemeSeed.ValleyInsertTerrainMaxZoom};";

        // Moves the seeded valley map back to the terrain zoom and package key
        // A92TrackerMapsThemes inserts.
        public static readonly string DownSql = $@"
update tracker_map
set terrain_max_zoom = {TrackerThemeSeed.ValleyInsertTerrainMaxZoom}, package_key = '{TrackerThemeSeed.ValleyInsertPackageKey}'
where prefix = '{TrackerThemeSeed.ValleyPrefix}' and terrain_max_zoom = {TrackerThemeSeed.ValleyTerrainMaxZoom};";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(UpSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DownSql);
        }
    }
}
