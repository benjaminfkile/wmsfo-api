using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class A39RemoveLocationMaxGap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A39: A38's unique (event_id, lat, lng) turns a repeat into a
            // carry unconditionally, so the max-gap rule and its setting no
            // longer decide anything. Remove the app_setting row and refresh
            // the beacon.fixes_carried comment to match the new rules.
            migrationBuilder.Sql("delete from app_setting where key = 'location_max_gap_s';");

            migrationBuilder.AlterColumn<long>(
                name: "fixes_carried",
                table: "beacon",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                comment: "Fixes accepted without a location row (within min distance, or a repeat of a position already stored in the event).",
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValue: 0L,
                oldComment: "Fixes accepted without a location row (within min distance and max gap).");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "fixes_carried",
                table: "beacon",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                comment: "Fixes accepted without a location row (within min distance and max gap).",
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValue: 0L,
                oldComment: "Fixes accepted without a location row (within min distance, or a repeat of a position already stored in the event).");

            migrationBuilder.Sql(@"
insert into app_setting (key, value, updated_by) values
  ('location_max_gap_s', '30', 'seed')
on conflict (key) do nothing;");
        }
    }
}
