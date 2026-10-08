using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class A89HelpTopics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "help_topic",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false, comment: "The seed key, dotted lower-case segments (page, page.card, page.dialog)."),
                    page = table.Column<string>(type: "text", nullable: false, comment: "The admin panel page the topic belongs to, from the seed."),
                    label = table.Column<string>(type: "text", nullable: false, comment: "What the topic is about, from the seed; the panel lists topics by it."),
                    title = table.Column<string>(type: "text", nullable: false, comment: "The title shown: the default while edited_by is null, else the admin's."),
                    body = table.Column<string>(type: "text", nullable: false, comment: "The body shown: the default while edited_by is null, else the admin's."),
                    links = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb", comment: "The links shown, an array of { label, to }: the default while edited_by is null, else the admin's."),
                    default_title = table.Column<string>(type: "text", nullable: false, comment: "The seed's title, rewritten on every boot."),
                    default_body = table.Column<string>(type: "text", nullable: false, comment: "The seed's body, rewritten on every boot."),
                    default_links = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb", comment: "The seed's links, rewritten on every boot."),
                    default_updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, comment: "When the boot last found a default_title, default_body, or default_links different from the seed."),
                    edited_by = table.Column<string>(type: "text", nullable: true, comment: "The actor of the last edit (the audit text of contracts 3.1); null while the shown text is the seed's."),
                    edited_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true, comment: "When the last edit was made; null while the shown text is the seed's."),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()", comment: "When the row last changed, by an edit, a reset, or the boot.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("help_topic_pkey", x => x.key);
                },
                comment: "One help popover of the admin panel: the text shown, the seed's default text from help/topics.json, and who last edited it. Admin only; never in the snapshot or on the site.");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "help_topic");
        }
    }
}
