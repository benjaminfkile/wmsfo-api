using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class A82MessageNotify : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "event_time",
                table: "event_message");

            migrationBuilder.AddColumn<bool>(
                name: "notify",
                table: "event_message",
                type: "boolean",
                nullable: false,
                defaultValue: false,
                comment: "The admin asked for subscribers to be emailed when the message was posted from the message form. False for the messages a status change or an announcement writes; their emails are counted on the history row.");

            migrationBuilder.AddColumn<long>(
                name: "outbox_id",
                table: "event_message",
                type: "bigint",
                nullable: true,
                comment: "The event.message_posted outbox row while it exists (set null when the row is cleaned up).");

            migrationBuilder.AddColumn<int>(
                name: "sent_count",
                table: "event_message",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                comment: "Alert emails sent for this message, incremented by the alert-send chore per successful send; survives the outbox row.");

            migrationBuilder.AddForeignKey(
                name: "event_message_outbox_id_fkey",
                table: "event_message",
                column: "outbox_id",
                principalTable: "outbox",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            // A message an event.message_posted outbox row names in its
            // payload's messageId was posted with notify: it takes notify
            // true, that outbox row, and the emails already sent for it.
            migrationBuilder.Sql(@"
update event_message m
set notify = true,
    outbox_id = o.id,
    sent_count = (select count(*) from alert_delivery d where d.outbox_id = o.id and d.sent_at is not null)
from outbox o
where o.topic = 'event.message_posted'
  and jsonb_typeof(o.payload->'messageId') = 'number'
  and (o.payload->>'messageId')::bigint = m.id;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "event_message_outbox_id_fkey",
                table: "event_message");

            migrationBuilder.DropColumn(
                name: "notify",
                table: "event_message");

            migrationBuilder.DropColumn(
                name: "outbox_id",
                table: "event_message");

            migrationBuilder.DropColumn(
                name: "sent_count",
                table: "event_message");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "event_time",
                table: "event_message",
                type: "timestamptz",
                nullable: true,
                comment: "The time the message is about, as entered by the admin. Display only.");
        }
    }
}
