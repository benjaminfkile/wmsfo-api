using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class A74StatusMessageRef : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterTable(
                name: "event_message",
                comment: "Messages shown on the site, written by the message form, a status change, or an announcement. The snapshot carries the one with the greatest created_at (ties: greatest id).",
                oldComment: "Messages shown on the site. The snapshot carries the one with the greatest created_at (ties: greatest id).");

            migrationBuilder.AddColumn<long>(
                name: "message_id",
                table: "event_status_history",
                type: "bigint",
                nullable: true,
                comment: "The event_message posted with the change or announcement; its body replaces the alert's stock paragraph. Null when none was given or the message was deleted.");

            migrationBuilder.AddForeignKey(
                name: "event_status_history_message_id_fkey",
                table: "event_status_history",
                column: "message_id",
                principalTable: "event_message",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            // Every history row with text gets an event_message row carrying
            // that text, written by the same admin at the same time.
            migrationBuilder.Sql(@"
do $$
declare
  r record;
  new_id bigint;
begin
  for r in
    select id, event_id, message, changed_by, changed_at
    from event_status_history
    where message is not null
    order by id
  loop
    insert into event_message (event_id, body, event_time, created_by, created_at, updated_at)
    values (r.event_id, r.message, null, r.changed_by, r.changed_at, r.changed_at)
    returning id into new_id;
    update event_status_history set message_id = new_id where id = r.id;
  end loop;
end $$;");

            // The status alert payloads carry messageId in place of the text.
            migrationBuilder.Sql(@"
update outbox o
set payload = (o.payload - 'message') || jsonb_build_object('messageId', h.message_id)
from event_status_history h
where h.outbox_id = o.id
  and o.topic in ('event.status_changed', 'event.status_notified');
update outbox
set payload = (payload - 'message') || jsonb_build_object('messageId', null)
where topic in ('event.status_changed', 'event.status_notified')
  and not payload ? 'messageId';");

            migrationBuilder.DropColumn(
                name: "message",
                table: "event_status_history");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "message",
                table: "event_status_history",
                type: "text",
                nullable: true,
                comment: "The custom alert text; null means the template's stock paragraph.");

            migrationBuilder.Sql(@"
update event_status_history h
set message = m.body
from event_message m
where m.id = h.message_id;
update outbox
set payload = (payload - 'messageId') || jsonb_build_object('message',
  (select m.body from event_message m where m.id = (payload->>'messageId')::bigint))
where topic in ('event.status_changed', 'event.status_notified');");

            migrationBuilder.DropForeignKey(
                name: "event_status_history_message_id_fkey",
                table: "event_status_history");

            migrationBuilder.DropColumn(
                name: "message_id",
                table: "event_status_history");

            migrationBuilder.AlterTable(
                name: "event_message",
                comment: "Messages shown on the site. The snapshot carries the one with the greatest created_at (ties: greatest id).",
                oldComment: "Messages shown on the site, written by the message form, a status change, or an announcement. The snapshot carries the one with the greatest created_at (ties: greatest id).");
        }
    }
}
