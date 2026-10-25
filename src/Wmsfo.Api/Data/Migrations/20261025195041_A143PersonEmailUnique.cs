using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    // One address is one person (contracts 3.1): a unique index on lower(email).
    // The guard runs first and raises naming every address (lowercased) that more
    // than one row holds, so the migration never half-applies; sql.md 14.4 has
    // the same query for the check against prod before the main merge.
    public partial class A143PersonEmailUnique : Migration
    {
        public const string GuardSql = @"
do $$
declare
  duplicated text;
begin
  select string_agg(address, ', ' order by address) into duplicated
  from (
    select lower(email) as address
    from person
    group by lower(email)
    having count(*) > 1
  ) d;
  if duplicated is not null then
    raise exception 'person rows share an address: %', duplicated;
  end if;
end
$$;";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(GuardSql);
            migrationBuilder.Sql("create unique index person_email_lower_key on person (lower(email));");
            migrationBuilder.AlterTable(
                name: "person",
                comment: "A registered person, one per email address (person_email_lower_key on lower(email)). Every person request updates it by the ID token's sub; a sub with no row is checked against the people pool and gets a fresh row after any stale row holding the address is deleted with its cascade.",
                oldComment: "A registered person. Upserted on every authenticated request from the ID token (sub, email).");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("drop index person_email_lower_key;");
            migrationBuilder.AlterTable(
                name: "person",
                comment: "A registered person. Upserted on every authenticated request from the ID token (sub, email).",
                oldComment: "A registered person, one per email address (person_email_lower_key on lower(email)). Every person request updates it by the ID token's sub; a sub with no row is checked against the people pool and gets a fresh row after any stale row holding the address is deleted with its cascade.");
        }
    }
}
