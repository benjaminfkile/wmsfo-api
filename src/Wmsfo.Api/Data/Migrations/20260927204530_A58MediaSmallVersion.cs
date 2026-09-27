using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class A58MediaSmallVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "small_media_id",
                table: "media_asset",
                type: "uuid",
                nullable: true,
                comment: "The small screen version of this asset (contracts 1.3b): another ready media_asset the site draws in its place under its 760 px cut; null when none. A deleted small version unlinks.");

            migrationBuilder.AddForeignKey(
                name: "media_asset_small_media_id_fkey",
                table: "media_asset",
                column: "small_media_id",
                principalTable: "media_asset",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "media_asset_small_media_id_fkey",
                table: "media_asset");

            migrationBuilder.DropColumn(
                name: "small_media_id",
                table: "media_asset");
        }
    }
}
