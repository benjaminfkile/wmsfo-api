using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PlaceDeleteRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "place_parent_id_fkey",
                table: "place");

            migrationBuilder.DropForeignKey(
                name: "qr_attachment_place_id_fkey",
                table: "qr_attachment");

            migrationBuilder.AlterColumn<long>(
                name: "place_id",
                table: "qr_attachment",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AddForeignKey(
                name: "place_parent_id_fkey",
                table: "place",
                column: "parent_id",
                principalTable: "place",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "qr_attachment_place_id_fkey",
                table: "qr_attachment",
                column: "place_id",
                principalTable: "place",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "place_parent_id_fkey",
                table: "place");

            migrationBuilder.DropForeignKey(
                name: "qr_attachment_place_id_fkey",
                table: "qr_attachment");

            migrationBuilder.AlterColumn<long>(
                name: "place_id",
                table: "qr_attachment",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "place_parent_id_fkey",
                table: "place",
                column: "parent_id",
                principalTable: "place",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "qr_attachment_place_id_fkey",
                table: "qr_attachment",
                column: "place_id",
                principalTable: "place",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
