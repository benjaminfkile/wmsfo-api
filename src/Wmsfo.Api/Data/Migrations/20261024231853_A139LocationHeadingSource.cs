using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class A139LocationHeadingSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "heading_source",
                table: "location",
                type: "text",
                nullable: true,
                comment: "'beacon' when the body carried headingDeg, 'derived' when the API computed it, null otherwise (contracts 7.2).");

            // Seed the heading setting (contracts 6). Only when the row is
            // absent; PUT /admin/settings/{key} owns updates.
            migrationBuilder.Sql(@"
insert into app_setting (key, value, updated_by) values
  ('heading_min_distance_m', '15', 'seed')
on conflict (key) do nothing;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
delete from app_setting where key = 'heading_min_distance_m';");

            migrationBuilder.DropColumn(
                name: "heading_source",
                table: "location");
        }
    }
}
