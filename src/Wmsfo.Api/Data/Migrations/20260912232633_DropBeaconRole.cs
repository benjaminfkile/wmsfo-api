using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropBeaconRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "beacon_role_check",
                table: "beacon");

            migrationBuilder.DropColumn(
                name: "role",
                table: "beacon");

            migrationBuilder.AlterTable(
                name: "beacon_log",
                comment: "Beacon debug log uploads. Up to 2 MB each. 30-day retention.",
                oldComment: "Red-Nose debug log uploads (admin-role beacons). Up to 2 MB each. 30-day retention.");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterTable(
                name: "beacon_log",
                comment: "Red-Nose debug log uploads (admin-role beacons). Up to 2 MB each. 30-day retention.",
                oldComment: "Beacon debug log uploads. Up to 2 MB each. 30-day retention.");

            migrationBuilder.AddColumn<string>(
                name: "role",
                table: "beacon",
                type: "text",
                nullable: false,
                defaultValue: "",
                comment: "beacon or admin. Immutable after creation.");

            migrationBuilder.AddCheckConstraint(
                name: "beacon_role_check",
                table: "beacon",
                sql: "role in ('beacon', 'admin')");
        }
    }
}
