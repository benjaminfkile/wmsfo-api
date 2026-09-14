using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class DeleteCascades : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // location.event_id: NoAction -> Cascade (sql.md 14)
            migrationBuilder.DropForeignKey(
                name: "location_event_id_fkey",
                table: "location");
            migrationBuilder.AddForeignKey(
                name: "location_event_id_fkey",
                table: "location",
                column: "event_id",
                principalTable: "event",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            // cookie.cookie_type_id: NoAction -> Cascade
            migrationBuilder.DropForeignKey(
                name: "cookie_cookie_type_id_fkey",
                table: "cookie");
            migrationBuilder.AddForeignKey(
                name: "cookie_cookie_type_id_fkey",
                table: "cookie",
                column: "cookie_type_id",
                principalTable: "cookie_type",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            // event.route_id: NoAction -> SetNull
            migrationBuilder.DropForeignKey(
                name: "event_route_id_fkey",
                table: "event");
            migrationBuilder.AddForeignKey(
                name: "event_route_id_fkey",
                table: "event",
                column: "route_id",
                principalTable: "route",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            // event.route_image_media_id: NoAction -> SetNull
            migrationBuilder.DropForeignKey(
                name: "event_route_image_media_id_fkey",
                table: "event");
            migrationBuilder.AddForeignKey(
                name: "event_route_image_media_id_fkey",
                table: "event",
                column: "route_image_media_id",
                principalTable: "media_asset",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            // sponsor.logo_media_id: NoAction -> SetNull
            migrationBuilder.DropForeignKey(
                name: "sponsor_logo_media_id_fkey",
                table: "sponsor");
            migrationBuilder.AddForeignKey(
                name: "sponsor_logo_media_id_fkey",
                table: "sponsor",
                column: "logo_media_id",
                principalTable: "media_asset",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "location_event_id_fkey",
                table: "location");
            migrationBuilder.AddForeignKey(
                name: "location_event_id_fkey",
                table: "location",
                column: "event_id",
                principalTable: "event",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.DropForeignKey(
                name: "cookie_cookie_type_id_fkey",
                table: "cookie");
            migrationBuilder.AddForeignKey(
                name: "cookie_cookie_type_id_fkey",
                table: "cookie",
                column: "cookie_type_id",
                principalTable: "cookie_type",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.DropForeignKey(
                name: "event_route_id_fkey",
                table: "event");
            migrationBuilder.AddForeignKey(
                name: "event_route_id_fkey",
                table: "event",
                column: "route_id",
                principalTable: "route",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.DropForeignKey(
                name: "event_route_image_media_id_fkey",
                table: "event");
            migrationBuilder.AddForeignKey(
                name: "event_route_image_media_id_fkey",
                table: "event",
                column: "route_image_media_id",
                principalTable: "media_asset",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.DropForeignKey(
                name: "sponsor_logo_media_id_fkey",
                table: "sponsor");
            migrationBuilder.AddForeignKey(
                name: "sponsor_logo_media_id_fkey",
                table: "sponsor",
                column: "logo_media_id",
                principalTable: "media_asset",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
