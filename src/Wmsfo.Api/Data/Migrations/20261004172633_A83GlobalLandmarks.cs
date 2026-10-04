using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class A83GlobalLandmarks : Migration
    {
        // Folds every event's route_map_config landmarks into the site settings
        // draft's `landmarks`: the draft's own landmarks first, then the current
        // event's, then the other events' by year descending. A landmark is a
        // duplicate of an earlier one when its name matches case-insensitively
        // and its lat and lng round to the same five decimals; at most 50 are
        // kept. The draft is left alone when no event has a landmark.
        public const string FoldLandmarksSql = @"
with candidates as (
  select l.value as landmark, 0 as tier, 0 as current_rank, 0 as year, 0::bigint as event_id, l.ordinality as pos
  from site_setting_draft d
  cross join lateral jsonb_array_elements(
    case when jsonb_typeof(d.data->'landmarks') = 'array' then d.data->'landmarks' else '[]'::jsonb end
  ) with ordinality l
  where d.id = 1
  union all
  select l.value, 1, case when e.is_current then 0 else 1 end, e.year, e.id, l.ordinality
  from event e
  cross join lateral jsonb_array_elements(e.route_map_config->'landmarks') with ordinality l
  where jsonb_typeof(e.route_map_config->'landmarks') = 'array'
),
ranked as (
  select landmark,
         row_number() over (order by tier, current_rank, year desc, event_id, pos) as ord,
         row_number() over (
           partition by lower(landmark->>'name'),
                        round((landmark->>'lat')::numeric, 5),
                        round((landmark->>'lng')::numeric, 5)
           order by tier, current_rank, year desc, event_id, pos) as copy
  from candidates
),
kept as (
  select landmark, ord from ranked where copy = 1 order by ord limit 50
)
update site_setting_draft d
set data = jsonb_set(d.data, '{landmarks}', (select jsonb_agg(landmark order by ord) from kept)),
    updated_at = now()
where d.id = 1
  and jsonb_typeof(d.data) = 'object'
  and exists (
    select 1 from event e
    where jsonb_typeof(e.route_map_config->'landmarks') = 'array'
      and jsonb_array_length(e.route_map_config->'landmarks') > 0);";

        // Removes the `landmarks` key from every event's route_map_config; a
        // configuration left empty becomes null.
        public const string StripEventLandmarksSql = @"
update event
set route_map_config = nullif(route_map_config - 'landmarks', '{}'::jsonb)
where route_map_config->'landmarks' is not null;";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Landmarks are one sitewide list in the site settings. The
            // published content versions are immutable, so the folded landmarks
            // reach the site when the editor publishes after the deploy.
            migrationBuilder.Sql(FoldLandmarksSql);
            migrationBuilder.Sql(StripEventLandmarksSql);

            migrationBuilder.AlterColumn<JsonDocument>(
                name: "route_map_config",
                table: "event",
                type: "jsonb",
                nullable: true,
                comment: "The event's route map configuration { display?, controls?, pois? } in canonical form, validated on write against $defs/RouteMapConfig (contracts 1.3, 4.5). Null means every built-in default; the snapshot carries it for the current event.",
                oldClrType: typeof(JsonDocument),
                oldType: "jsonb",
                oldNullable: true,
                oldComment: "The event's route map configuration { display?, controls?, landmarks?, pois? } in canonical form, validated on write against $defs/RouteMapConfig (contracts 1.3, 4.5). Null means every built-in default; the snapshot carries it for the current event.");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The folded landmarks stay in the site settings draft; the events
            // keep configurations without landmarks.
            migrationBuilder.AlterColumn<JsonDocument>(
                name: "route_map_config",
                table: "event",
                type: "jsonb",
                nullable: true,
                comment: "The event's route map configuration { display?, controls?, landmarks?, pois? } in canonical form, validated on write against $defs/RouteMapConfig (contracts 1.3, 4.5). Null means every built-in default; the snapshot carries it for the current event.",
                oldClrType: typeof(JsonDocument),
                oldType: "jsonb",
                oldNullable: true,
                oldComment: "The event's route map configuration { display?, controls?, pois? } in canonical form, validated on write against $defs/RouteMapConfig (contracts 1.3, 4.5). Null means every built-in default; the snapshot carries it for the current event.");
        }
    }
}
