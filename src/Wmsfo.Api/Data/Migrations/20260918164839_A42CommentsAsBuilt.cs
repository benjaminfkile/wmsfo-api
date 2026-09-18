using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class A42CommentsAsBuilt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterTable(
                name: "cookie_type",
                comment: "Admin-managed. Locked (409 event_live) while any event has status 3. A delete takes the type's cookies with it; active = false removes a type from the snapshot without deleting it.",
                oldComment: "Admin-managed. Locked (409 event_live) while any event has status 3. No delete; active = false removes a type from the snapshot.");

            migrationBuilder.AlterTable(
                name: "beacon",
                comment: "A trusted sender: a key and nothing more. Rows are never deleted; revoke is permanent.",
                oldComment: "A trusted sender. Rows are never deleted; revoke is permanent.");

            migrationBuilder.AlterColumn<Guid>(
                name: "logo_media_id",
                table: "sponsor",
                type: "uuid",
                nullable: true,
                comment: "A ready media_asset chosen from the library; null when no logo.",
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true,
                oldComment: "A ready media_asset chosen from the library; null when no logo. The API refuses to delete a referenced asset (409 media_in_use).");

            migrationBuilder.AlterColumn<string>(
                name: "note",
                table: "cookie",
                type: "text",
                nullable: true,
                comment: "Never shown anywhere; stored for the record.",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true,
                oldComment: "Never public. Visible to admins only.");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "hidden_at",
                table: "cookie",
                type: "timestamptz",
                nullable: true,
                comment: "Always null. Moderation was removed; the column and the partial index stay so the tally query is unchanged.",
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamptz",
                oldNullable: true,
                oldComment: "Soft delete for moderation. Hidden cookies leave the tally but still count toward the per-person limit.");

            migrationBuilder.AlterColumn<JsonDocument>(
                name: "telemetry",
                table: "beacon",
                type: "jsonb",
                nullable: true,
                comment: "The last heartbeat body, stored as received: sentAt, the optional health core, and the beacon's own debug object (contracts 4.2).",
                oldClrType: typeof(JsonDocument),
                oldType: "jsonb",
                oldNullable: true,
                oldComment: "The last heartbeat body, stored as received.");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "last_location_at",
                table: "beacon",
                type: "timestamptz",
                nullable: true,
                comment: "Last location the beacon delivered, published or not, stored as a row or carried (contracts 7.2). Health reads this: a beacon whose every fix repeats a stored position is still alive.",
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamptz",
                oldNullable: true,
                oldComment: "Last stored location, published or not.");

            migrationBuilder.AlterColumn<string>(
                name: "entity_id",
                table: "audit_log",
                type: "text",
                nullable: false,
                comment: "The row's id as text so uuids, setting keys, sponsor-year pairs (sponsorId:year), and a sponsor order's year share one index.",
                oldClrType: typeof(string),
                oldType: "text",
                oldComment: "The row's id as text (uuid for media, a setting's key, the year for a sponsor order).");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterTable(
                name: "cookie_type",
                comment: "Admin-managed. Locked (409 event_live) while any event has status 3. No delete; active = false removes a type from the snapshot.",
                oldComment: "Admin-managed. Locked (409 event_live) while any event has status 3. A delete takes the type's cookies with it; active = false removes a type from the snapshot without deleting it.");

            migrationBuilder.AlterTable(
                name: "beacon",
                comment: "A trusted sender. Rows are never deleted; revoke is permanent.",
                oldComment: "A trusted sender: a key and nothing more. Rows are never deleted; revoke is permanent.");

            migrationBuilder.AlterColumn<Guid>(
                name: "logo_media_id",
                table: "sponsor",
                type: "uuid",
                nullable: true,
                comment: "A ready media_asset chosen from the library; null when no logo. The API refuses to delete a referenced asset (409 media_in_use).",
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true,
                oldComment: "A ready media_asset chosen from the library; null when no logo.");

            migrationBuilder.AlterColumn<string>(
                name: "note",
                table: "cookie",
                type: "text",
                nullable: true,
                comment: "Never public. Visible to admins only.",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true,
                oldComment: "Never shown anywhere; stored for the record.");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "hidden_at",
                table: "cookie",
                type: "timestamptz",
                nullable: true,
                comment: "Soft delete for moderation. Hidden cookies leave the tally but still count toward the per-person limit.",
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamptz",
                oldNullable: true,
                oldComment: "Always null. Moderation was removed; the column and the partial index stay so the tally query is unchanged.");

            migrationBuilder.AlterColumn<JsonDocument>(
                name: "telemetry",
                table: "beacon",
                type: "jsonb",
                nullable: true,
                comment: "The last heartbeat body, stored as received.",
                oldClrType: typeof(JsonDocument),
                oldType: "jsonb",
                oldNullable: true,
                oldComment: "The last heartbeat body, stored as received: sentAt, the optional health core, and the beacon's own debug object (contracts 4.2).");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "last_location_at",
                table: "beacon",
                type: "timestamptz",
                nullable: true,
                comment: "Last stored location, published or not.",
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamptz",
                oldNullable: true,
                oldComment: "Last location the beacon delivered, published or not, stored as a row or carried (contracts 7.2). Health reads this: a beacon whose every fix repeats a stored position is still alive.");

            migrationBuilder.AlterColumn<string>(
                name: "entity_id",
                table: "audit_log",
                type: "text",
                nullable: false,
                comment: "The row's id as text (uuid for media, a setting's key, the year for a sponsor order).",
                oldClrType: typeof(string),
                oldType: "text",
                oldComment: "The row's id as text so uuids, setting keys, sponsor-year pairs (sponsorId:year), and a sponsor order's year share one index.");
        }
    }
}
