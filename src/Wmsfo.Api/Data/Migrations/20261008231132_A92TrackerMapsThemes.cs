using System;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Wmsfo.Api.Themes;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class A92TrackerMapsThemes : Migration
    {
        // Removes `themes` and `defaultTheme` from the data of every map
        // section that carries either, the working set only.
        public const string StripMapThemeKeysSql = @"
update section
set data = data - 'themes' - 'defaultTheme'
where kind = 'map'
  and jsonb_typeof(data) = 'object'
  and (data ? 'themes' or data ? 'defaultTheme');";

        // Writes the six Google keys in the baseline enum order and the
        // starter default back into every map section, so the baseline map
        // schema validates the working set.
        public const string RestoreMapThemeKeysSql = @"
update section
set data = data || '{""themes"":[""standard"",""expedition"",""blizzard"",""charcoal"",""night"",""nebula""],""defaultTheme"":""night""}'::jsonb
where kind = 'map'
  and jsonb_typeof(data) = 'object';";

        // Removes the `tracker` key from the settings draft, so the baseline
        // site settings schema validates it.
        public const string RemoveDraftTrackerSql = @"
update site_setting_draft
set data = data - 'tracker'
where jsonb_typeof(data) = 'object'
  and data ? 'tracker';";

        // The seeds of sql.md 6, each guarded so a second run changes nothing:
        // the Missoula valley map unless a row has its prefix, every event
        // without a map pointed at it, each seeded theme unless a row has its
        // (renderer, key), every theme enabled on every event, the state row,
        // and the draft's tracker.defaultBbox unless the draft has `tracker`.
        // A seeded default flag is set only while its renderer has no holder.
        public static string SeedSql()
        {
            var sql = new StringBuilder();
            var bbox = Literal(TrackerThemeSeed.ValleyBboxJson);
            var prefix = Literal(TrackerThemeSeed.ValleyPrefix);
            sql.Append($@"
insert into tracker_map (name, package_key, prefix, bbox, min_zoom, max_zoom, terrain_max_zoom, state, created_by, updated_by)
select {Literal(TrackerThemeSeed.ValleyName)}, {Literal(TrackerThemeSeed.ValleyPackageKey)}, {prefix}, {bbox}::jsonb,
       {TrackerThemeSeed.ValleyMinZoom}, {TrackerThemeSeed.ValleyMaxZoom}, {TrackerThemeSeed.ValleyTerrainMaxZoom}, 'ready', 'seed', 'seed'
where not exists (select 1 from tracker_map where prefix = {prefix});

update event
set tracker_map_id = (select id from tracker_map where prefix = {prefix})
where tracker_map_id is null;
");
            foreach (var theme in TrackerThemeSeed.Themes)
            {
                var renderer = Literal(theme.Renderer);
                var key = Literal(theme.Key);
                sql.Append($@"
insert into tracker_theme (renderer, key, name, sort_order, style_sha256, style_bytes, chrome, overlay, default_light_mode, default_dark_mode, created_by, updated_by)
select {renderer}, {key}, {Literal(theme.Name)}, {theme.SortOrder}, {Literal(theme.StyleSha256)}, {theme.StyleBytes},
       {Literal(theme.Chrome)}::jsonb, {Literal(theme.Overlay)}::jsonb,
       {Bool(theme.DefaultLightMode)} and not exists (select 1 from tracker_theme where renderer = {renderer} and default_light_mode),
       {Bool(theme.DefaultDarkMode)} and not exists (select 1 from tracker_theme where renderer = {renderer} and default_dark_mode),
       'seed', 'seed'
where not exists (select 1 from tracker_theme where renderer = {renderer} and key = {key});
");
            }
            sql.Append($@"
insert into event_tracker_theme (event_id, theme_id)
select e.id, t.id from event e cross join tracker_theme t
on conflict do nothing;

insert into tracker_theme_state (id) values (1)
on conflict do nothing;

update site_setting_draft
set data = data || jsonb_build_object('tracker', jsonb_build_object('defaultBbox', {bbox}::jsonb)),
    updated_at = now()
where id = 1
  and jsonb_typeof(data) = 'object'
  and not data ? 'tracker';
");
            return sql.ToString();
        }

        private static string Literal(string value) => "'" + value.Replace("'", "''") + "'";

        private static string Bool(bool value) => value ? "true" : "false";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tracker_map",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    package_key = table.Column<string>(type: "text", nullable: false, comment: "Lowercase hex SHA-256 of the canonical JSON { bbox, minZoom, maxZoom, terrainMaxZoom }. Two uploads of the same box and zooms collide here on purpose."),
                    prefix = table.Column<string>(type: "text", nullable: false, comment: "The CDN prefix holding tiles.pmtiles and terrain.pmtiles: maps/{package_key} for uploaded packages; basemap for the seeded Missoula valley row, whose objects the operator placed by hand before this table existed (those two objects are cached one day and hand-invalidated, platform.md 1.8, unlike the immutable maps/ objects)."),
                    bbox = table.Column<JsonDocument>(type: "jsonb", nullable: false, comment: "{ west, south, east, north } in degrees, west < east, south < north, at most 20 degrees on a side."),
                    min_zoom = table.Column<short>(type: "smallint", nullable: false),
                    max_zoom = table.Column<short>(type: "smallint", nullable: false),
                    terrain_max_zoom = table.Column<short>(type: "smallint", nullable: true, comment: "Null when the package has no terrain file."),
                    tiles_bytes = table.Column<long>(type: "bigint", nullable: true),
                    terrain_bytes = table.Column<long>(type: "bigint", nullable: true),
                    source_build = table.Column<DateOnly>(type: "date", nullable: true, comment: "The Protomaps daily build date the vector tiles came from."),
                    state = table.Column<string>(type: "text", nullable: false, comment: "pending until POST /admin/maps/{id}/confirm verifies the objects; only ready maps can be picked by an event."),
                    built_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("tracker_map_pkey", x => x.id);
                    table.UniqueConstraint("tracker_map_package_key_key", x => x.package_key);
                    table.UniqueConstraint("tracker_map_prefix_key", x => x.prefix);
                    table.CheckConstraint("tracker_map_max_zoom_check", "max_zoom between 8 and 15");
                    table.CheckConstraint("tracker_map_min_zoom_check", "min_zoom between 0 and 15");
                    table.CheckConstraint("tracker_map_name_check", "char_length(name) between 1 and 200");
                    table.CheckConstraint("tracker_map_state_check", "state in ('pending', 'ready')");
                    table.CheckConstraint("tracker_map_terrain_max_zoom_check", "terrain_max_zoom between 8 and 13");
                },
                comment: "A tile package the operator built for a bounding box and uploaded under maps/{package_key}/. Events reference one; the snapshot carries the current event's.");

            migrationBuilder.CreateTable(
                name: "tracker_theme",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    renderer = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false, comment: "Stable slug. The viewer's localStorage choice names it. The six seeded Google themes keep their current keys."),
                    name = table.Column<string>(type: "text", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    style_sha256 = table.Column<string>(type: "text", nullable: false),
                    style_bytes = table.Column<int>(type: "integer", nullable: false),
                    sprite_sha256 = table.Column<string>(type: "text", nullable: true, comment: "Lowercase hex SHA-256 of the canonical bytes of the confirmed sprite index: the set lives under themes/{id}/sprites/{sprite_sha256}/sprite{,@2x}.{png,json}, immutable; a re-upload is a new prefix and the old one stays. MapLibre only; null without a sprite set."),
                    chrome = table.Column<JsonDocument>(type: "jsonb", nullable: false, comment: "{ bg, fg, text, tile, tileFg, panel, accent } as #rrggbb or #rrggbbaa. Write check: text on bg and tileFg on tile at 4.5:1 or better."),
                    overlay = table.Column<JsonDocument>(type: "jsonb", nullable: false, comment: "{ routeColor, routeOpacity, arrowColor, timeLabelBg, timeLabelFg, timeLabelOpacity, userColor }."),
                    thumbnail_media_id = table.Column<Guid>(type: "uuid", nullable: true, comment: "A media asset (uuid, like sponsor.logo_media_id). The media orphan chore, media usage, and media impact all count this reference (5.4)."),
                    default_light_mode = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    default_dark_mode = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("tracker_theme_pkey", x => x.id);
                    table.UniqueConstraint("tracker_theme_renderer_key_key", x => new { x.renderer, x.key });
                    table.CheckConstraint("tracker_theme_key_check", "key ~ '^[a-z][a-z0-9-]{1,39}$'");
                    table.CheckConstraint("tracker_theme_name_check", "char_length(name) between 1 and 60");
                    table.CheckConstraint("tracker_theme_renderer_check", "renderer in ('google', 'maplibre')");
                    table.ForeignKey(
                        name: "tracker_theme_thumbnail_media_id_fkey",
                        column: x => x.thumbnail_media_id,
                        principalTable: "media_asset",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                },
                comment: "One tracker look for one renderer. The style body lives on the CDN at themes/{style_sha256}.json; the row carries what the snapshot needs inline.");

            migrationBuilder.CreateTable(
                name: "event_tracker_theme",
                columns: table => new
                {
                    event_id = table.Column<long>(type: "bigint", nullable: false),
                    theme_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("event_tracker_theme_pkey", x => new { x.event_id, x.theme_id });
                    table.ForeignKey(
                        name: "event_tracker_theme_event_id_fkey",
                        column: x => x.event_id,
                        principalTable: "event",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "event_tracker_theme_theme_id_fkey",
                        column: x => x.theme_id,
                        principalTable: "tracker_theme",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "The themes this event offers. Write rule: at least one google theme (400 validation_failed at trackerThemeIds). Clone copies the rows under the \"Tracker map and themes\" flag; create follows the one rule in 5.3.");

            migrationBuilder.CreateTable(
                name: "tracker_theme_state",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false),
                    written_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("tracker_theme_state_pkey", x => x.id);
                    table.CheckConstraint("tracker_theme_state_id_check", "id = 1");
                },
                comment: "Single row (id = 1). When the first-boot step last wrote the seeded themes' style objects to the bucket (section 8.16 step 2b). The step writes every theme whose object is missing, so it is idempotent and heals a lost object.");

            migrationBuilder.CreateIndex(
                name: "tracker_theme_one_default_light",
                table: "tracker_theme",
                column: "renderer",
                unique: true,
                filter: "default_light_mode");

            migrationBuilder.CreateIndex(
                name: "tracker_theme_one_default_dark",
                table: "tracker_theme",
                column: "renderer",
                unique: true,
                filter: "default_dark_mode");

            migrationBuilder.CreateIndex(
                name: "tracker_theme_thumbnail_media",
                table: "tracker_theme",
                column: "thumbnail_media_id",
                filter: "thumbnail_media_id is not null");

            migrationBuilder.CreateIndex(
                name: "event_tracker_theme_theme",
                table: "event_tracker_theme",
                column: "theme_id");

            migrationBuilder.AddColumn<JsonDocument>(
                name: "tracker_bbox",
                table: "event",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{\"west\":-114.75,\"south\":46.35,\"east\":-113.30,\"north\":47.25}'::jsonb",
                comment: "{ west, south, east, north } in degrees, west < east, south < north, at least 0.05 and at most 20 degrees on a side. Required on create (the API always writes it; the column default exists only so a rolled-back API that does not know the column can still insert events, which works because the event inserts name their columns). Both tracker renderers lock to it; the snapshot carries it; the tile CLI builds from it. The migration fills existing events with the Missoula valley box.");

            migrationBuilder.AddColumn<long>(
                name: "tracker_map_id",
                table: "event",
                type: "bigint",
                nullable: true,
                comment: "The tile package the tracker renders with MapLibre. Null means Google Maps for every viewer. Write rule: the package bbox contains tracker_bbox (400 validation_failed at trackerMapId); shrinking or moving the box later must keep it inside the package or the write is refused the same way at trackerBbox.");

            migrationBuilder.AddForeignKey(
                name: "event_tracker_map_id_fkey",
                table: "event",
                column: "tracker_map_id",
                principalTable: "tracker_map",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.CreateIndex(
                name: "event_tracker_map",
                table: "event",
                column: "tracker_map_id",
                filter: "tracker_map_id is not null");

            migrationBuilder.Sql(SeedSql());
            migrationBuilder.Sql(StripMapThemeKeysSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "event_tracker_map_id_fkey",
                table: "event");

            migrationBuilder.DropIndex(
                name: "event_tracker_map",
                table: "event");

            migrationBuilder.DropColumn(
                name: "tracker_map_id",
                table: "event");

            migrationBuilder.DropColumn(
                name: "tracker_bbox",
                table: "event");

            migrationBuilder.DropTable(
                name: "event_tracker_theme");

            migrationBuilder.DropTable(
                name: "tracker_theme_state");

            migrationBuilder.DropTable(
                name: "tracker_theme");

            migrationBuilder.DropTable(
                name: "tracker_map");

            migrationBuilder.Sql(RestoreMapThemeKeysSql);
            migrationBuilder.Sql(RemoveDraftTrackerSql);
        }
    }
}
