using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class A77Postponed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("insert into event_status (id, name) values (6, 'postponed');");

            migrationBuilder.DropCheckConstraint(
                name: "page_role_check",
                table: "page");

            migrationBuilder.AddCheckConstraint(
                name: "page_role_check",
                table: "page",
                sql: "role in ('none', 'no_event', 'planned', 'scheduled', 'live', 'ended', 'cancelled', 'postponed')");

            migrationBuilder.AlterTable(
                name: "page",
                comment: "Working set. The seven role pages are seeded, undeletable, and role-immutable; none pages render at /<slug>. Hidden pages are omitted at publish.",
                oldComment: "Working set. The six role pages are seeded, undeletable, and role-immutable; none pages render at /<slug>. Hidden pages are omitted at publish.");

            // A working set that has pages but none with the postponed role gets
            // the starter postponed page and its sections. A database with no
            // pages gets all seven role pages from the first boot seed instead.
            // The slug is `postponed`, or `postponed-2`, `postponed-3`, and so
            // on when a page already holds it.
            migrationBuilder.Sql(@"
do $$
declare
  new_page_id bigint;
  new_slug text := 'postponed';
  n int := 1;
begin
  if exists (select 1 from page) and not exists (select 1 from page where role = 'postponed') then
    while exists (select 1 from page where slug = new_slug) loop
      n := n + 1;
      new_slug := 'postponed-' || n;
    end loop;
    insert into page (slug, title, nav_label, nav_position, is_hidden, role, created_by, updated_by)
    values (new_slug, 'Postponed', null, 0, false, 'postponed', 'seed', 'seed')
    returning id into new_page_id;

    insert into section (page_id, kind, position, data, presentation, updated_by) values
      (new_page_id, 'hero', 0,
       '{""title"":""Santa''s flight is postponed"",""tagline"":""A new time will be announced here and by email."",""icon"":{""source"":""library"",""id"":""helicopter""},""links"":[],""height"":""tall""}',
       '{""width"":""wide"",""align"":""center"",""background"":{""kind"":""none""},""spacing"":""normal"",""iconBefore"":null,""iconAfter"":null,""anchor"":null}',
       'seed'),
      (new_page_id, 'latest_message', 1,
       '{""heading"":""Latest update"",""style"":""card""}',
       '{""width"":""wide"",""align"":""start"",""background"":{""kind"":""none""},""spacing"":""normal"",""iconBefore"":null,""iconAfter"":null,""anchor"":null}',
       'seed'),
      (new_page_id, 'funds_ring', 2,
       '{""heading"":""Cheer meter"",""caption"":""How the fundraiser is doing."",""size"":""large"",""showYear"":true}',
       '{""width"":""wide"",""align"":""start"",""background"":{""kind"":""none""},""spacing"":""normal"",""iconBefore"":null,""iconAfter"":null,""anchor"":null}',
       'seed'),
      (new_page_id, 'route_preview', 3,
       '{""heading"":""This year''s route"",""style"":""image"",""disclaimer"":null,""emptyText"":""The route will appear here once it is set.""}',
       '{""width"":""wide"",""align"":""start"",""background"":{""kind"":""none""},""spacing"":""normal"",""iconBefore"":null,""iconAfter"":null,""anchor"":null}',
       'seed'),
      (new_page_id, 'sponsor_carousel', 4,
       '{""heading"":""Thanks to our sponsors"",""logoWidth"":480}',
       '{""width"":""wide"",""align"":""start"",""background"":{""kind"":""none""},""spacing"":""normal"",""iconBefore"":null,""iconAfter"":null,""anchor"":null}',
       'seed'),
      (new_page_id, 'alerts_signup', 5,
       '{""heading"":""Get alerts"",""copy"":""We will email you when Santa is scheduled and when he lifts off."",""signedOutCopy"":""Sign in to sign up for alerts.""}',
       '{""width"":""wide"",""align"":""start"",""background"":{""kind"":""none""},""spacing"":""normal"",""iconBefore"":null,""iconAfter"":null,""anchor"":null}',
       'seed');
  end if;
end $$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Postponed events and history rows read as planned; the postponed
            // pages and their sections go.
            migrationBuilder.Sql(@"
update event set status_id = 1 where status_id = 6;
update event_status_history set from_status_id = 1 where from_status_id = 6;
update event_status_history set to_status_id = 1 where to_status_id = 6;
delete from page where role = 'postponed';
delete from event_status where id = 6;");

            migrationBuilder.DropCheckConstraint(
                name: "page_role_check",
                table: "page");

            migrationBuilder.AddCheckConstraint(
                name: "page_role_check",
                table: "page",
                sql: "role in ('none', 'no_event', 'planned', 'scheduled', 'live', 'ended', 'cancelled')");

            migrationBuilder.AlterTable(
                name: "page",
                comment: "Working set. The six role pages are seeded, undeletable, and role-immutable; none pages render at /<slug>. Hidden pages are omitted at publish.",
                oldComment: "Working set. The seven role pages are seeded, undeletable, and role-immutable; none pages render at /<slug>. Hidden pages are omitted at publish.");
        }
    }
}
