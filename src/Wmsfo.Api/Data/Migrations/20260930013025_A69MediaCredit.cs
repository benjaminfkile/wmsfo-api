using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class A69MediaCredit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "credit",
                table: "media_asset",
                type: "text",
                nullable: true,
                comment: "The author or source the site names under the asset (contracts 1.3b), 1 to 200 characters; null when none.");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "credit",
                table: "media_asset");
        }
    }
}
