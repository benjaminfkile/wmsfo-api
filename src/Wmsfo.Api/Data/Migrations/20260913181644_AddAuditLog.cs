using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    actor = table.Column<string>(type: "text", nullable: false, comment: "person:<email> or key:<name>."),
                    action = table.Column<string>(type: "text", nullable: false, comment: "create, update, delete, or the endpoint's verb (4.5 Audit)."),
                    entity = table.Column<string>(type: "text", nullable: false, comment: "The entity kind (4.5 Audit)."),
                    entity_id = table.Column<string>(type: "text", nullable: false, comment: "The row's id as text (uuid for media, a setting's key, the year for a sponsor order)."),
                    before = table.Column<JsonDocument>(type: "jsonb", nullable: true, comment: "The resource before the write, null on create."),
                    after = table.Column<JsonDocument>(type: "jsonb", nullable: true, comment: "The resource after the write, null on delete."),
                    request_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("audit_log_pkey", x => x.id);
                },
                comment: "One row per admin write. Actor is person:<email> or key:<name>; action is create/update/delete or the endpoint verb; entity is the kind and entity_id is the row id as text; before and after are the endpoint response shapes (4.5 Audit). Read by GET /admin/audit and by the audit stamp on every DTO that carries one.");

            migrationBuilder.CreateIndex(
                name: "audit_log_action",
                table: "audit_log",
                columns: new[] { "action", "id" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "audit_log_entity",
                table: "audit_log",
                columns: new[] { "entity", "entity_id", "id" },
                descending: new[] { false, false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_log");
        }
    }
}
