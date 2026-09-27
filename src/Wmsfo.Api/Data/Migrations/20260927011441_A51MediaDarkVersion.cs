using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class A51MediaDarkVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "dark_media_id",
                table: "media_asset",
                type: "uuid",
                nullable: true,
                comment: "The dark mode version of this asset (contracts 1.3b): another ready media_asset the site draws in its place in dark mode; null when none. A deleted dark version unlinks.");

            migrationBuilder.AddColumn<bool>(
                name: "invert_in_dark",
                table: "media_asset",
                type: "boolean",
                nullable: false,
                defaultValue: false,
                comment: "When true the site inverts this asset's colors in dark mode (contracts 1.3b).");

            migrationBuilder.AddForeignKey(
                name: "media_asset_dark_media_id_fkey",
                table: "media_asset",
                column: "dark_media_id",
                principalTable: "media_asset",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "media_asset_dark_media_id_fkey",
                table: "media_asset");

            migrationBuilder.DropColumn(
                name: "dark_media_id",
                table: "media_asset");

            migrationBuilder.DropColumn(
                name: "invert_in_dark",
                table: "media_asset");
        }
    }
}
