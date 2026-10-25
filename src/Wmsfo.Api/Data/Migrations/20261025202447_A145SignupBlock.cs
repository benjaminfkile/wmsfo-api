using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    // The signup_block table of sql.md 3.12a: blocked addresses and domains
    // (contracts 3.1). Additive; nothing migrated.
    public partial class A145SignupBlock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "signup_block",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    pattern = table.Column<string>(type: "text", nullable: false, comment: "An address (jane@example.com) or a domain (*@example.com), trimmed and lowercased. An address matches by equality; a domain matches the part after the address's last @."),
                    note = table.Column<string>(type: "text", nullable: false, comment: "Why the pattern is blocked, as the admin wrote it."),
                    created_by = table.Column<string>(type: "text", nullable: false, comment: "The audit actor text of the admin who wrote the block (person:<email> or key:<name>)."),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("signup_block_pkey", x => x.id);
                    table.UniqueConstraint("signup_block_pattern_key", x => x.pattern);
                    table.CheckConstraint("signup_block_note_check", "char_length(note) between 1 and 300");
                    table.CheckConstraint("signup_block_pattern_check", "char_length(pattern) between 3 and 254 and pattern = lower(pattern)");
                },
                comment: "Addresses and domains that cannot sign up. A person whose address matches a pattern is removed when the block is written, and a first sight of a matching address is refused.");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "signup_block");
        }
    }
}
