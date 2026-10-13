using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class A109ScheduleTimeZoneComment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "schedule_time_zone",
                table: "event",
                type: "text",
                nullable: true,
                comment: "IANA zone id the admin panel enters scheduled_at in; null means unset. No email renders the scheduled time.",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true,
                oldComment: "IANA zone id the scheduled time was entered in; alert emails render scheduled_at in it (America/Denver when null).");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "schedule_time_zone",
                table: "event",
                type: "text",
                nullable: true,
                comment: "IANA zone id the scheduled time was entered in; alert emails render scheduled_at in it (America/Denver when null).",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true,
                oldComment: "IANA zone id the admin panel enters scheduled_at in; null means unset. No email renders the scheduled time.");
        }
    }
}
