using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class A26ApiKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "api_key",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    key_prefix = table.Column<string>(type: "text", nullable: false, comment: "First 12 characters of the key, for display."),
                    key_hash = table.Column<byte[]>(type: "bytea", nullable: false, comment: "sha256 of the plaintext key, 32 bytes. The plaintext is never stored."),
                    all_capabilities = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    capabilities = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'"),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true, comment: "Null (never) or rfc3339. Past it the key answers 401 unauthenticated."),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true, comment: "Set once by revoke. A revoked key never authenticates again.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("api_key_pkey", x => x.id);
                    table.UniqueConstraint("api_key_key_hash_key", x => x.key_hash);
                },
                comment: "Bearer keys with capability sets and an optional expiry. Lookup is by sha256(key).");

            migrationBuilder.CreateIndex(
                name: "api_key_name_ux",
                table: "api_key",
                column: "name",
                unique: true,
                filter: "revoked_at is null");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_key");
        }
    }
}
