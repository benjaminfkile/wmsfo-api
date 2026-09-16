using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class A41CarriedStampsLastLocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "fixes_carried",
                table: "beacon",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                comment: "Fixes accepted without a location row (within min distance, or a repeat of a position already stored in the event); these stamp last_location_at like a stored fix.",
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValue: 0L,
                oldComment: "Fixes accepted without a location row (within min distance, or a repeat of a position already stored in the event).");
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
                comment: "Fixes accepted without a location row (within min distance, or a repeat of a position already stored in the event).",
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValue: 0L,
                oldComment: "Fixes accepted without a location row (within min distance, or a repeat of a position already stored in the event); these stamp last_location_at like a stored fix.");
        }
    }
}
