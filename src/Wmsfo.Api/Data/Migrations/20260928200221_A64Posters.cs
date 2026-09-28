using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class A64Posters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "poster_layout",
                table: "event");

            migrationBuilder.CreateTable(
                name: "poster",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    route_id = table.Column<long>(type: "bigint", nullable: true, comment: "The flight recording the poster's map is built from; null when unlinked. Set null when the route is deleted."),
                    layout = table.Column<JsonDocument>(type: "jsonb", nullable: true, comment: "The admin panel's poster composer layout, stored opaquely (contracts 4.5 Posters). A JSON object of at most 32 KB canonical; null when unset."),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("poster_pkey", x => x.id);
                    table.CheckConstraint("poster_name_check", "char_length(name) between 1 and 200");
                    table.ForeignKey(
                        name: "poster_route_id_fkey",
                        column: x => x.route_id,
                        principalTable: "route",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                },
                comment: "A poster the admin panel composes: a name, an optional flight recording for its map, and the panel's layout document. Admin only; never in the snapshot or on the site.");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "poster");

            migrationBuilder.AddColumn<JsonDocument>(
                name: "poster_layout",
                table: "event",
                type: "jsonb",
                nullable: true,
                comment: "The admin panel's poster composer layout, stored opaquely (contracts 4.5). A JSON object of at most 32 KB canonical; never in the snapshot or on the site.");
        }
    }
}
