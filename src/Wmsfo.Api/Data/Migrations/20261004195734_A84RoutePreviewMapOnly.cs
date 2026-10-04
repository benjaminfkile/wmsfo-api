using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class A84RoutePreviewMapOnly : Migration
    {
        // Removes the `style` key from every route_preview section's data, so
        // the working set validates against the route_preview schema. Sections
        // without the key are left alone.
        public const string StripRoutePreviewStyleSql = @"
update section
set data = data - 'style'
where kind = 'route_preview'
  and jsonb_typeof(data) = 'object'
  and data ? 'style';";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The published content versions are immutable; the site ignores
            // `style` in the documents that still carry it.
            migrationBuilder.Sql(StripRoutePreviewStyleSql);

            migrationBuilder.DropForeignKey(
                name: "event_route_image_media_id_fkey",
                table: "event");

            migrationBuilder.DropColumn(
                name: "route_image_media_id",
                table: "event");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The column comes back empty; the sections keep their data
            // without `style`.
            migrationBuilder.AddColumn<Guid>(
                name: "route_image_media_id",
                table: "event",
                type: "uuid",
                nullable: true,
                comment: "The route poster the site shows (contracts 1.3). A ready raster media_asset; svg and gif are refused at PATCH.");

            migrationBuilder.AddForeignKey(
                name: "event_route_image_media_id_fkey",
                table: "event",
                column: "route_image_media_id",
                principalTable: "media_asset",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
