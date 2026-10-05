using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class A88GlobalPlaces : Migration
    {
        // Builds the site settings draft's `places` when the draft is an object
        // without one: `tracker` from the first map section (by page, position,
        // id) whose `poiFilter` is true, its `poiKinds` or [] as `kinds`;
        // `routeMap` from the current event's `pois`, else the newest event's,
        // when `pois.kinds` is an array. The draft is left alone when neither
        // part is found.
        public const string FoldPlacesSql = @"
with tracker as (
  select jsonb_build_object('kinds',
           case when jsonb_typeof(s.data->'poiKinds') = 'array' then s.data->'poiKinds' else '[]'::jsonb end) as part
  from section s
  where s.kind = 'map'
    and jsonb_typeof(s.data) = 'object'
    and s.data->>'poiFilter' = 'true'
  order by s.page_id, s.position, s.id
  limit 1
),
route_map as (
  select jsonb_build_object('kinds', e.route_map_config->'pois'->'kinds') as part
  from event e
  where jsonb_typeof(e.route_map_config) = 'object'
    and jsonb_typeof(e.route_map_config->'pois') = 'object'
    and jsonb_typeof(e.route_map_config->'pois'->'kinds') = 'array'
  order by e.is_current desc, e.year desc, e.id desc
  limit 1
),
places as (
  select jsonb_strip_nulls(jsonb_build_object(
           'tracker', (select part from tracker),
           'routeMap', (select part from route_map))) as value
)
update site_setting_draft d
set data = d.data || jsonb_build_object('places', p.value),
    updated_at = now()
from places p
where d.id = 1
  and jsonb_typeof(d.data) = 'object'
  and not (d.data ? 'places')
  and p.value <> '{}'::jsonb;";

        // Removes `poiFilter` and `poiKinds` from the data of every map section
        // that carries either.
        public const string StripMapPoiKeysSql = @"
update section
set data = data - 'poiFilter' - 'poiKinds'
where kind = 'map'
  and jsonb_typeof(data) = 'object'
  and (data ? 'poiFilter' or data ? 'poiKinds');";

        // Removes the `pois` key from every event's route_map_config; a
        // configuration left empty becomes null.
        public const string StripEventPoisSql = @"
update event
set route_map_config = nullif(route_map_config - 'pois', '{}'::jsonb)
where jsonb_typeof(route_map_config) = 'object'
  and route_map_config ? 'pois';";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The places on both maps are one site setting. The published
            // content versions are immutable and the site ignores the old keys
            // in them; the setting reaches the site when the editor publishes
            // after the deploy.
            migrationBuilder.Sql(FoldPlacesSql);
            migrationBuilder.Sql(StripMapPoiKeysSql);
            migrationBuilder.Sql(StripEventPoisSql);

            migrationBuilder.AlterColumn<JsonDocument>(
                name: "route_map_config",
                table: "event",
                type: "jsonb",
                nullable: true,
                comment: "The event's route map configuration { display?, controls? } in canonical form, validated on write against $defs/RouteMapConfig (contracts 1.3, 4.5). Null means every built-in default; the snapshot carries it for the current event.",
                oldClrType: typeof(JsonDocument),
                oldType: "jsonb",
                oldNullable: true,
                oldComment: "The event's route map configuration { display?, controls?, pois? } in canonical form, validated on write against $defs/RouteMapConfig (contracts 1.3, 4.5). Null means every built-in default; the snapshot carries it for the current event.");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The places stay in the site settings draft; the sections and the
            // events keep their data without the old keys.
            migrationBuilder.AlterColumn<JsonDocument>(
                name: "route_map_config",
                table: "event",
                type: "jsonb",
                nullable: true,
                comment: "The event's route map configuration { display?, controls?, pois? } in canonical form, validated on write against $defs/RouteMapConfig (contracts 1.3, 4.5). Null means every built-in default; the snapshot carries it for the current event.",
                oldClrType: typeof(JsonDocument),
                oldType: "jsonb",
                oldNullable: true,
                oldComment: "The event's route map configuration { display?, controls? } in canonical form, validated on write against $defs/RouteMapConfig (contracts 1.3, 4.5). Null means every built-in default; the snapshot carries it for the current event.");
        }
    }
}
