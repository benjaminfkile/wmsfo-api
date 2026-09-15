using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class A40HubFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
insert into app_setting (key, value, updated_by) values ('hub_enabled', 'true', 'seed')
on conflict (key) do nothing;");

            migrationBuilder.AddColumn<bool>(
                name: "hub_allowed",
                table: "beacon",
                type: "boolean",
                nullable: false,
                defaultValue: true,
                comment: "Whether this beacon may join the hub (contracts 2.4); false denies the join and the beacon sends over HTTP.");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("delete from app_setting where key = 'hub_enabled';");
            migrationBuilder.DropColumn(
                name: "hub_allowed",
                table: "beacon");
        }
    }
}
