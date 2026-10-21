using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class A128PersonSeenUpTo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "seen_up_to_id",
                table: "person",
                type: "bigint",
                nullable: true,
                comment: "The outbox id of the newest alert this person has seen, the same id space as snapshot.event.alerts[].id; null when never recorded.");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "seen_up_to_id",
                table: "person");
        }
    }
}
