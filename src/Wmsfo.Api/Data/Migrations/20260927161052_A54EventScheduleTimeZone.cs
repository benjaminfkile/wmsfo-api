using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class A54EventScheduleTimeZone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "schedule_time_zone",
                table: "event",
                type: "text",
                nullable: true,
                comment: "IANA zone id the scheduled time was entered in; alert emails render scheduled_at in it (America/Denver when null).");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "schedule_time_zone",
                table: "event");
        }
    }
}
