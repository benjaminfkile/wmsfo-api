using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class A24Design : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "linger_ms_override",
                table: "sponsor_year",
                type: "integer",
                nullable: true,
                comment: "0..600000, overrides the computed lingerMs for this year. Snapshot exposes only lingerMs; whether it was overridden is not revealed.");

            migrationBuilder.AddColumn<int>(
                name: "pinned_position",
                table: "sponsor_year",
                type: "integer",
                nullable: true,
                comment: "1..1000, pins the sponsor's slot for this year. Snapshot orders pinned rows first, pinned_position asc.");

            migrationBuilder.AddColumn<Guid>(
                name: "route_image_media_id",
                table: "event",
                type: "uuid",
                nullable: true,
                comment: "The route poster the site shows (contracts 1.3). A ready raster media_asset; svg and gif are refused at PATCH.");

            migrationBuilder.CreateIndex(
                name: "sponsor_year_pinned_ux",
                table: "sponsor_year",
                columns: new[] { "event_year", "pinned_position" },
                unique: true,
                filter: "pinned_position is not null");

            migrationBuilder.AddCheckConstraint(
                name: "sponsor_year_linger_ms_override_check",
                table: "sponsor_year",
                sql: "linger_ms_override is null or (linger_ms_override between 0 and 600000)");

            migrationBuilder.AddCheckConstraint(
                name: "sponsor_year_pinned_position_check",
                table: "sponsor_year",
                sql: "pinned_position is null or (pinned_position between 1 and 1000)");

            migrationBuilder.AddForeignKey(
                name: "event_route_image_media_id_fkey",
                table: "event",
                column: "route_image_media_id",
                principalTable: "media_asset",
                principalColumn: "id");

            // Seed the new app_setting row (contracts 6, section 6). Idempotent
            // so a fleet applying the migration on an already-live database does
            // not overwrite an admin-set value.
            migrationBuilder.Sql(@"
insert into app_setting (key, value, updated_by) values
  ('flight_history_max_points', '2000', 'seed')
on conflict (key) do nothing;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "event_route_image_media_id_fkey",
                table: "event");

            migrationBuilder.DropIndex(
                name: "sponsor_year_pinned_ux",
                table: "sponsor_year");

            migrationBuilder.DropCheckConstraint(
                name: "sponsor_year_linger_ms_override_check",
                table: "sponsor_year");

            migrationBuilder.DropCheckConstraint(
                name: "sponsor_year_pinned_position_check",
                table: "sponsor_year");

            migrationBuilder.DropColumn(
                name: "linger_ms_override",
                table: "sponsor_year");

            migrationBuilder.DropColumn(
                name: "pinned_position",
                table: "sponsor_year");

            migrationBuilder.DropColumn(
                name: "route_image_media_id",
                table: "event");
        }
    }
}
