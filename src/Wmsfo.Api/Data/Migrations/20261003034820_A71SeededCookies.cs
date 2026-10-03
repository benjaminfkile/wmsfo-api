using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class A71SeededCookies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterTable(
                name: "cookie",
                comment: "A cookie on an event: left by a registered person (person_id set) or seeded by an admin on the live event (seeded_by set). Exactly one of the two is set. Both kinds count in the tally; only a person's own cookies count toward that person. No location.",
                oldComment: "A cookie left by a registered person during a live event. No location.");

            migrationBuilder.AlterColumn<long>(
                name: "person_id",
                table: "cookie",
                type: "bigint",
                nullable: true,
                comment: "The person who left the cookie; null on a seeded cookie.",
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AddColumn<string>(
                name: "seeded_by",
                table: "cookie",
                type: "text",
                nullable: true,
                comment: "The admin who seeded the cookie, as the audit log writes the actor (person:<email> or key:<name>); null on a person's cookie.");

            migrationBuilder.AddCheckConstraint(
                name: "cookie_origin_check",
                table: "cookie",
                sql: "(person_id is null) <> (seeded_by is null)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "cookie_origin_check",
                table: "cookie");

            migrationBuilder.DropColumn(
                name: "seeded_by",
                table: "cookie");

            migrationBuilder.AlterTable(
                name: "cookie",
                comment: "A cookie left by a registered person during a live event. No location.",
                oldComment: "A cookie on an event: left by a registered person (person_id set) or seeded by an admin on the live event (seeded_by set). Exactly one of the two is set. Both kinds count in the tally; only a person's own cookies count toward that person. No location.");

            migrationBuilder.AlterColumn<long>(
                name: "person_id",
                table: "cookie",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true,
                oldComment: "The person who left the cookie; null on a seeded cookie.");
        }
    }
}
