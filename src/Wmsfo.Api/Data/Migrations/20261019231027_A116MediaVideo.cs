using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class A116MediaVideo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "media_asset_kind_check",
                table: "media_asset");

            migrationBuilder.AlterColumn<string>(
                name: "kind",
                table: "media_asset",
                type: "text",
                nullable: false,
                comment: "raster: png, jpeg, webp (variants derived). gif: stored as is. svg: validated. video: mp4, read by ranges at confirm, no variants.",
                oldClrType: typeof(string),
                oldType: "text",
                oldComment: "raster: png, jpeg, webp (variants derived). gif: stored as is. svg: validated.");

            migrationBuilder.AddColumn<int>(
                name: "duration_ms",
                table: "media_asset",
                type: "integer",
                nullable: true,
                comment: "A video's length in milliseconds, read from its mvhd box at confirm (api.md 11.3); null for every other kind.");

            migrationBuilder.AddColumn<Guid>(
                name: "poster_media_id",
                table: "media_asset",
                type: "uuid",
                nullable: true,
                comment: "A video's poster (contracts 1.3b): another ready raster or gif media_asset the site draws before play; null when none. A deleted poster unlinks.");

            migrationBuilder.AddCheckConstraint(
                name: "media_asset_kind_check",
                table: "media_asset",
                sql: "kind in ('raster', 'svg', 'gif', 'video')");

            migrationBuilder.AddForeignKey(
                name: "media_asset_poster_media_id_fkey",
                table: "media_asset",
                column: "poster_media_id",
                principalTable: "media_asset",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "media_asset_poster_media_id_fkey",
                table: "media_asset");

            migrationBuilder.DropCheckConstraint(
                name: "media_asset_kind_check",
                table: "media_asset");

            migrationBuilder.DropColumn(
                name: "duration_ms",
                table: "media_asset");

            migrationBuilder.DropColumn(
                name: "poster_media_id",
                table: "media_asset");

            migrationBuilder.AlterColumn<string>(
                name: "kind",
                table: "media_asset",
                type: "text",
                nullable: false,
                comment: "raster: png, jpeg, webp (variants derived). gif: stored as is. svg: validated.",
                oldClrType: typeof(string),
                oldType: "text",
                oldComment: "raster: png, jpeg, webp (variants derived). gif: stored as is. svg: validated. video: mp4, read by ranges at confirm, no variants.");

            migrationBuilder.AddCheckConstraint(
                name: "media_asset_kind_check",
                table: "media_asset",
                sql: "kind in ('raster', 'svg', 'gif')");
        }
    }
}
