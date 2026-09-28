using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class A63EventPosterLayout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<JsonDocument>(
                name: "poster_layout",
                table: "event",
                type: "jsonb",
                nullable: true,
                comment: "The admin panel's poster composer layout, stored opaquely (contracts 4.5). A JSON object of at most 32 KB canonical; never in the snapshot or on the site.");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "poster_layout",
                table: "event");
        }
    }
}
