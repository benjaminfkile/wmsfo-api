using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddQrCodesAndPlaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "place",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    parent_id = table.Column<long>(type: "bigint", nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    opens_page_id = table.Column<long>(type: "bigint", nullable: true),
                    forward_url = table.Column<string>(type: "text", nullable: true),
                    lat = table.Column<double>(type: "double precision", nullable: true),
                    lng = table.Column<double>(type: "double precision", nullable: true),
                    accuracy_m = table.Column<double>(type: "double precision", nullable: true),
                    pin_source = table.Column<string>(type: "text", nullable: true),
                    pinned_by = table.Column<string>(type: "text", nullable: true),
                    pinned_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("place_pkey", x => x.id);
                    table.CheckConstraint("place_opens_check", "(opens_page_id is null) or (forward_url is null)");
                    table.CheckConstraint("place_pin_check", "((lat is null) = (lng is null)) and ((lat is null) = (pin_source is null))");
                    table.CheckConstraint("place_pin_source_check", "pin_source is null or pin_source in ('phone', 'search', 'drag')");
                    table.ForeignKey(
                        name: "place_opens_page_id_fkey",
                        column: x => x.opens_page_id,
                        principalTable: "page",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "place_parent_id_fkey",
                        column: x => x.parent_id,
                        principalTable: "place",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "A place a printed QR code hangs in. Tree of friendly names with an optional pin and opens override; the snapshot resolves each active code through the tree.");

            migrationBuilder.CreateTable(
                name: "qr_code",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tag = table.Column<string>(type: "text", nullable: false),
                    batch_no = table.Column<int>(type: "integer", nullable: false),
                    printed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    note = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    opens_page_id = table.Column<long>(type: "bigint", nullable: true),
                    forward_url = table.Column<string>(type: "text", nullable: true),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("qr_code_pkey", x => x.id);
                    table.UniqueConstraint("qr_code_tag_key", x => x.tag);
                    table.CheckConstraint("qr_code_opens_check", "(opens_page_id is null) or (forward_url is null)");
                    table.ForeignKey(
                        name: "qr_code_opens_page_id_fkey",
                        column: x => x.opens_page_id,
                        principalTable: "page",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                },
                comment: "A printed sticker's permanent tag (qr-001, sequential). Its opens setting overrides the place it hangs in; the snapshot resolves it.");

            migrationBuilder.CreateTable(
                name: "qr_attachment",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    qr_code_id = table.Column<long>(type: "bigint", nullable: false),
                    place_id = table.Column<long>(type: "bigint", nullable: false),
                    from_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    to_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    attached_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("qr_attachment_pkey", x => x.id);
                    table.ForeignKey(
                        name: "qr_attachment_place_id_fkey",
                        column: x => x.place_id,
                        principalTable: "place",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "qr_attachment_qr_code_id_fkey",
                        column: x => x.qr_code_id,
                        principalTable: "qr_code",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "One row per stay of a code in a place. Open row (to_at is null) is unique per code; attach closes the open row and opens a new one.");

            migrationBuilder.CreateTable(
                name: "qr_scan",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    qr_code_id = table.Column<long>(type: "bigint", nullable: false),
                    attachment_id = table.Column<long>(type: "bigint", nullable: true),
                    event_id = table.Column<long>(type: "bigint", nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    user_agent = table.Column<string>(type: "text", nullable: true),
                    referrer = table.Column<string>(type: "text", nullable: true),
                    ip_hash = table.Column<string>(type: "char(64)", nullable: false),
                    is_bot = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    is_repeat = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("qr_scan_pkey", x => x.id);
                    table.ForeignKey(
                        name: "qr_scan_attachment_id_fkey",
                        column: x => x.attachment_id,
                        principalTable: "qr_attachment",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "qr_scan_event_id_fkey",
                        column: x => x.event_id,
                        principalTable: "event",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "qr_scan_qr_code_id_fkey",
                        column: x => x.qr_code_id,
                        principalTable: "qr_code",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "A public visit to a printed address. Salted IP hash and bot/repeat markers make the counts safe; unflagged rows are 'people'.");

            migrationBuilder.CreateIndex(
                name: "qr_attachment_open",
                table: "qr_attachment",
                column: "qr_code_id",
                unique: true,
                filter: "to_at is null");

            migrationBuilder.CreateIndex(
                name: "qr_attachment_place",
                table: "qr_attachment",
                columns: new[] { "place_id", "from_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "qr_scan_attachment",
                table: "qr_scan",
                columns: new[] { "attachment_id", "at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "qr_scan_code_at",
                table: "qr_scan",
                columns: new[] { "qr_code_id", "at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "qr_scan_event",
                table: "qr_scan",
                columns: new[] { "event_id", "at" },
                descending: new[] { false, true });

            // place_sibling_name (sql.md 3): unique among siblings, case-insensitive.
            // A functional expression index cannot be described through the EF Core
            // model, so install it as raw SQL. `coalesce(parent_id, 0)` groups
            // root-level rows into a single logical parent for uniqueness.
            migrationBuilder.Sql(
                "create unique index place_sibling_name on place (coalesce(parent_id, 0), lower(name));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("drop index if exists place_sibling_name;");

            migrationBuilder.DropTable(
                name: "qr_scan");

            migrationBuilder.DropTable(
                name: "qr_attachment");

            migrationBuilder.DropTable(
                name: "place");

            migrationBuilder.DropTable(
                name: "qr_code");
        }
    }
}
