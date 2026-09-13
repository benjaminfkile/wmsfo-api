using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaDziKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "dzi_key",
                table: "media_asset",
                type: "text",
                nullable: true,
                comment: "media/<id>/dzi/poster.dzi when a Deep Zoom tile pyramid exists (contracts 1.3b): a raster whose longest side is 2048 px or more.");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "dzi_key",
                table: "media_asset");
        }
    }
}
