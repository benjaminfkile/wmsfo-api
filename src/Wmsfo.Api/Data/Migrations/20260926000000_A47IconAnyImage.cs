using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class A47IconAnyImage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<JsonDocument>(
                name: "icon",
                table: "cookie_type",
                type: "jsonb",
                nullable: true,
                comment: "Icon value (contracts 1.3a): {\"source\":\"library\",\"id\":\"cookie\"} or {\"source\":\"media\",\"id\":\"<uuid of a ready media_asset>\"}; null until chosen.",
                oldClrType: typeof(JsonDocument),
                oldType: "jsonb",
                oldNullable: true,
                oldComment: "Icon value (contracts 1.3a): {\"source\":\"library\",\"id\":\"cookie\"} or {\"source\":\"media\",\"id\":\"<uuid of a ready svg media_asset>\"}; null until chosen.");

            migrationBuilder.AlterColumn<string>(
                name: "kind",
                table: "media_asset",
                type: "text",
                nullable: false,
                comment: "raster: png, jpeg, webp (variants derived). gif: stored as is. svg: validated.",
                oldClrType: typeof(string),
                oldType: "text",
                oldComment: "raster: png, jpeg, webp (variants derived). gif: stored as is. svg: validated, usable as an icon.");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<JsonDocument>(
                name: "icon",
                table: "cookie_type",
                type: "jsonb",
                nullable: true,
                comment: "Icon value (contracts 1.3a): {\"source\":\"library\",\"id\":\"cookie\"} or {\"source\":\"media\",\"id\":\"<uuid of a ready svg media_asset>\"}; null until chosen.",
                oldClrType: typeof(JsonDocument),
                oldType: "jsonb",
                oldNullable: true,
                oldComment: "Icon value (contracts 1.3a): {\"source\":\"library\",\"id\":\"cookie\"} or {\"source\":\"media\",\"id\":\"<uuid of a ready media_asset>\"}; null until chosen.");

            migrationBuilder.AlterColumn<string>(
                name: "kind",
                table: "media_asset",
                type: "text",
                nullable: false,
                comment: "raster: png, jpeg, webp (variants derived). gif: stored as is. svg: validated, usable as an icon.",
                oldClrType: typeof(string),
                oldType: "text",
                oldComment: "raster: png, jpeg, webp (variants derived). gif: stored as is. svg: validated.");
        }
    }
}
