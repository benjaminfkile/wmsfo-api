using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class A113SiteRoute : Migration
    {
        // Builds the site settings draft's `route` when the draft is an object
        // without one: `routeId` is the current event's route_id, else the
        // route_id of the event with the greatest year that has one, else
        // null; `mapConfig` is the current event's route_map_config, else the
        // non-null one of the event with the greatest year, and is omitted
        // when no event has one.
        public const string FoldRouteSql = @"
with picked as (
  select
    (select e.route_id from event e
     where e.route_id is not null
     order by e.is_current desc, e.year desc, e.id desc
     limit 1) as route_id,
    (select e.route_map_config from event e
     where jsonb_typeof(e.route_map_config) = 'object'
     order by e.is_current desc, e.year desc, e.id desc
     limit 1) as map_config
)
update site_setting_draft d
set data = d.data || jsonb_build_object('route',
             jsonb_build_object('routeId', p.route_id)
             || case when p.map_config is null then '{}'::jsonb
                     else jsonb_build_object('mapConfig', p.map_config) end),
    updated_at = now()
from picked p
where d.id = 1
  and jsonb_typeof(d.data) = 'object'
  and not (d.data ? 'route');";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One site route in the site settings. The published content
            // versions are immutable, so the folded route reaches the site
            // when the editor publishes after the deploy.
            migrationBuilder.Sql(FoldRouteSql);

            migrationBuilder.DropForeignKey(
                name: "event_route_id_fkey",
                table: "event");

            migrationBuilder.DropForeignKey(
                name: "poster_route_id_fkey",
                table: "poster");

            migrationBuilder.DropColumn(
                name: "route_id",
                table: "poster");

            migrationBuilder.DropColumn(
                name: "route_id",
                table: "event");

            migrationBuilder.DropColumn(
                name: "route_map_config",
                table: "event");

            migrationBuilder.AlterTable(
                name: "poster",
                comment: "A poster the admin panel composes: a name and the panel's layout document; the panel draws the published site route into it. Admin only; never in the snapshot or on the site.",
                oldComment: "A poster the admin panel composes: a name, an optional flight recording for its map, and the panel's layout document. Admin only; never in the snapshot or on the site.");

            migrationBuilder.AlterColumn<string>(
                name: "url",
                table: "route",
                type: "text",
                nullable: false,
                comment: "Absolute CDN URL of s3_key, carried in the admin Route as url.",
                oldClrType: typeof(string),
                oldType: "text",
                oldComment: "Absolute CDN URL of s3_key, carried into the snapshot as routeUrl.");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The route stays in the site settings draft; the events and the
            // posters come back unlinked.
            migrationBuilder.AlterTable(
                name: "poster",
                comment: "A poster the admin panel composes: a name, an optional flight recording for its map, and the panel's layout document. Admin only; never in the snapshot or on the site.",
                oldComment: "A poster the admin panel composes: a name and the panel's layout document; the panel draws the published site route into it. Admin only; never in the snapshot or on the site.");

            migrationBuilder.AlterColumn<string>(
                name: "url",
                table: "route",
                type: "text",
                nullable: false,
                comment: "Absolute CDN URL of s3_key, carried into the snapshot as routeUrl.",
                oldClrType: typeof(string),
                oldType: "text",
                oldComment: "Absolute CDN URL of s3_key, carried in the admin Route as url.");

            migrationBuilder.AddColumn<long>(
                name: "route_id",
                table: "poster",
                type: "bigint",
                nullable: true,
                comment: "The flight recording the poster's map is built from; null when unlinked. Set null when the route is deleted.");

            migrationBuilder.AddColumn<long>(
                name: "route_id",
                table: "event",
                type: "bigint",
                nullable: true,
                comment: "Route shown for this event; null when unlinked.");

            migrationBuilder.AddColumn<JsonDocument>(
                name: "route_map_config",
                table: "event",
                type: "jsonb",
                nullable: true,
                comment: "The event's route map configuration { display?, controls? } in canonical form, validated on write against $defs/RouteMapConfig (contracts 1.3, 4.5). Null means every built-in default; the snapshot carries it for the current event.");

            migrationBuilder.AddForeignKey(
                name: "event_route_id_fkey",
                table: "event",
                column: "route_id",
                principalTable: "route",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "poster_route_id_fkey",
                table: "poster",
                column: "route_id",
                principalTable: "route",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
