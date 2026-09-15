using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class A37LocationFilters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "speed_source",
                table: "location",
                type: "text",
                nullable: true,
                comment: "'beacon' when the body carried speedMps, 'derived' when the API computed it from the previous stored fix, null otherwise (contracts 7.2).");

            migrationBuilder.AddColumn<long>(
                name: "fixes_carried",
                table: "beacon",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                comment: "Fixes accepted without a location row (within min distance and max gap).");

            migrationBuilder.AddColumn<long>(
                name: "fixes_rate_limited",
                table: "beacon",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                comment: "Fixes dropped before the transaction by the per-beacon min interval; flushed at most every 5 s per beacon.");

            migrationBuilder.AddColumn<long>(
                name: "fixes_stored",
                table: "beacon",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                comment: "Fixes stored as location rows for this beacon.");

            migrationBuilder.AddColumn<int>(
                name: "min_interval_ms",
                table: "beacon",
                type: "integer",
                nullable: true,
                comment: "Per-beacon override of location_min_interval_ms; null means the setting (contracts 4.2 / 6).");

            migrationBuilder.CreateIndex(
                name: "location_event_beacon_seq",
                table: "location",
                columns: new[] { "event_id", "beacon_id", "seq" },
                descending: new[] { false, false, true });

            // Seed the three new settings (contracts 6). Only when the row is
            // absent; PUT /admin/settings/{key} owns updates.
            migrationBuilder.Sql(@"
insert into app_setting (key, value, updated_by) values
  ('location_min_interval_ms', '250', 'seed'),
  ('location_min_distance_m',  '0',   'seed'),
  ('location_max_gap_s',       '30',  'seed')
on conflict (key) do nothing;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
delete from app_setting where key in (
  'location_min_interval_ms', 'location_min_distance_m', 'location_max_gap_s');");

            migrationBuilder.DropIndex(
                name: "location_event_beacon_seq",
                table: "location");

            migrationBuilder.DropColumn(
                name: "speed_source",
                table: "location");

            migrationBuilder.DropColumn(
                name: "fixes_carried",
                table: "beacon");

            migrationBuilder.DropColumn(
                name: "fixes_rate_limited",
                table: "beacon");

            migrationBuilder.DropColumn(
                name: "fixes_stored",
                table: "beacon");

            migrationBuilder.DropColumn(
                name: "min_interval_ms",
                table: "beacon");
        }
    }
}
