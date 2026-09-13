using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStatusNotify : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterTable(
                name: "event_status_history",
                comment: "One row per status change, written in the status change transaction, and one per later announcement (from_status_id = to_status_id) written by POST .../notify.",
                oldComment: "One row per status change, written in the status change transaction.");

            migrationBuilder.AlterColumn<short>(
                name: "from_status_id",
                table: "event_status_history",
                type: "smallint",
                nullable: true,
                comment: "Null when there was no previous status; equal to to_status_id for an announcement without a change.",
                oldClrType: typeof(short),
                oldType: "smallint",
                oldNullable: true,
                oldComment: "Null when there was no previous status.");

            migrationBuilder.AddColumn<string>(
                name: "message",
                table: "event_status_history",
                type: "text",
                nullable: true,
                comment: "The custom alert text; null means the template's stock paragraph.");

            migrationBuilder.AddColumn<bool>(
                name: "notify",
                table: "event_status_history",
                type: "boolean",
                nullable: false,
                defaultValue: false,
                comment: "The admin asked for subscribers to be emailed.");

            migrationBuilder.AddColumn<long>(
                name: "outbox_id",
                table: "event_status_history",
                type: "bigint",
                nullable: true,
                comment: "The alert's outbox row while it exists (set null when the row is cleaned up).");

            migrationBuilder.AddColumn<int>(
                name: "sent_count",
                table: "event_status_history",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                comment: "Alert emails sent for this row, incremented by the alert-send chore per successful send; survives the outbox row.");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "status_notified_at",
                table: "event",
                type: "timestamptz",
                nullable: true,
                comment: "When the current status was last announced to subscribers (a status change with notify, or POST .../notify); null since the last change otherwise.");

            migrationBuilder.AddForeignKey(
                name: "event_status_history_outbox_id_fkey",
                table: "event_status_history",
                column: "outbox_id",
                principalTable: "outbox",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "event_status_history_outbox_id_fkey",
                table: "event_status_history");

            migrationBuilder.DropColumn(
                name: "message",
                table: "event_status_history");

            migrationBuilder.DropColumn(
                name: "notify",
                table: "event_status_history");

            migrationBuilder.DropColumn(
                name: "outbox_id",
                table: "event_status_history");

            migrationBuilder.DropColumn(
                name: "sent_count",
                table: "event_status_history");

            migrationBuilder.DropColumn(
                name: "status_notified_at",
                table: "event");

            migrationBuilder.AlterTable(
                name: "event_status_history",
                comment: "One row per status change, written in the status change transaction.",
                oldComment: "One row per status change, written in the status change transaction, and one per later announcement (from_status_id = to_status_id) written by POST .../notify.");

            migrationBuilder.AlterColumn<short>(
                name: "from_status_id",
                table: "event_status_history",
                type: "smallint",
                nullable: true,
                comment: "Null when there was no previous status.",
                oldClrType: typeof(short),
                oldType: "smallint",
                oldNullable: true,
                oldComment: "Null when there was no previous status; equal to to_status_id for an announcement without a change.");
        }
    }
}
