using Npgsql;
using NpgsqlTypes;

namespace Wmsfo.Api.IntegrationTests;

// Legacy-shaped schema per docs/reference/legacy-schema.md. Created inside the
// same Postgres database as the target so a single connection string reaches
// both. Only the tables sql.md 15 reads are recreated here (retired tables are
// not touched).
internal static class A18LegacySchema
{
    // Wipes every target table the migrator writes to and resets the identity
    // sequences. The migrator drops tests are otherwise contaminated by state
    // from earlier tests sharing the same class fixture.
    public static async Task ClearTargetAsync(NpgsqlConnection conn)
    {
        foreach (var sql in new[]
        {
            "delete from location;",
            "delete from event_message;",
            "delete from event;",
            "delete from sponsor_year;",
            "delete from sponsor;",
            "delete from contact_message;",
            "delete from media_asset;",
            "delete from route;",
            "delete from beacon;",
            "update app_setting set value = to_jsonb(40::integer), updated_by = 'seed', updated_at = now() where key = 'sponsor_linger_ms_per_dollar';",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        foreach (var (table, column) in new[] { ("sponsor", "id"), ("sponsor_year", "id"), ("event_message", "id"),
                                                 ("event", "id"), ("beacon", "id"), ("route", "id"),
                                                 ("contact_message", "id"), ("location", "id") })
        {
            await using var cmd = new NpgsqlCommand(
                $"select setval(pg_get_serial_sequence('{table}', '{column}'), 1, false);", conn);
            await cmd.ExecuteScalarAsync();
        }
    }

    public static async Task CreateAsync(NpgsqlConnection conn)
    {
        // Drop first so a rerun in the same fixture works cleanly.
        foreach (var sql in new[]
        {
            "drop table if exists event_updates;",
            "drop table if exists flight_history;",
            "drop table if exists funds;",
            "drop table if exists messages;",
            "drop table if exists sponsor_settings;",
            "drop table if exists sponsor_years;",
            "drop table if exists sponsors;",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }

        foreach (var ddl in new[]
        {
            @"create table event_updates (
                id         integer primary key,
                message    text not null,
                time       timestamptz,
                created_at timestamptz not null default now()
              );",
            @"create table flight_history (
                id   integer primary key,
                year integer not null,
                lat  numeric not null,
                lng  numeric not null,
                seq  integer not null,
                time bigint  not null
              );",
            @"create table funds (
                percent bigint not null,
                id      text   not null,
                created text   not null
              );",
            @"create table messages (
                email   text not null,
                name    text not null,
                message text not null,
                created text not null,
                id      text not null primary key
              );",
            @"create table sponsor_settings (
                id                        integer primary key,
                linger_rate_ms_per_dollar integer not null default 40,
                updated_at                timestamptz default current_timestamp
              );",
            @"create table sponsor_years (
                id              integer primary key,
                sponsor_id      integer,
                event_year      integer not null,
                amount_donated  numeric default 0,
                active          boolean default true,
                can_advertise   boolean default true,
                anonymous       boolean default false,
                registered_at   timestamp without time zone default now()
              );",
            @"create table sponsors (
                id                integer primary key,
                name              varchar not null,
                contact_person    varchar,
                email             varchar,
                phone             varchar,
                address           varchar,
                website_url       varchar,
                fb_url            varchar,
                ig_url            varchar,
                logo_s3_key       varchar,
                logo_small_s3_key varchar,
                created_at        timestamp without time zone default now(),
                updated_at        timestamp without time zone default now()
              );",
        })
        {
            await using var cmd = new NpgsqlCommand(ddl, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    // Seeds a handful of rows per table with values that stress the migration
    // rules of sql.md 15: null legacy timestamps, both years populated in
    // flight_history, two funds rows for 2024 (the later one wins), and one
    // sponsor with a legacy logo.
    public static async Task SeedAsync(NpgsqlConnection conn)
    {
        // Sponsors — one with a legacy logo, one without.
        await using (var cmd = new NpgsqlCommand(@"
insert into sponsors (id, name, email, website_url, logo_s3_key, created_at, updated_at)
values (1, 'Sponsor One', 'one@example.com', 'https://one.example', 'legacy/sponsors/1/logo.png',
        timestamp '2024-01-15 10:00', timestamp '2024-01-15 10:00'),
       (2, 'Sponsor Two', null, null, null, null, null);", conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        // Sponsor_years — one for each year, plus a null sponsor_id that should be skipped.
        await using (var cmd = new NpgsqlCommand(@"
insert into sponsor_years (id, sponsor_id, event_year, amount_donated, active, can_advertise, anonymous, registered_at)
values (10, 1, 2024, 150.00, true, true, false, timestamp '2024-02-01 00:00'),
       (11, 1, 2025, 200.00, true, true, false, timestamp '2025-02-01 00:00'),
       (12, 2, 2025, 50.00,  true, false, true,  timestamp '2025-03-01 00:00'),
       (13, null, 2024, 0,    true, true, false, null);", conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        // Sponsor settings (linger rate override).
        await using (var cmd = new NpgsqlCommand(@"
insert into sponsor_settings (id, linger_rate_ms_per_dollar, updated_at)
values (1, 60, timestamp with time zone '2024-01-01 00:00Z');", conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        // Flight_history — three rows in 2024, three in 2025. Times in December at Denver noon.
        await using (var cmd = new NpgsqlCommand(@"
insert into flight_history (id, year, lat, lng, seq, time) values
  (1, 2024, 39.7392, -104.9903, 1, 1735048800000),
  (2, 2024, 39.7401, -104.9910, 2, 1735048801000),
  (3, 2024, 39.7410, -104.9917, 3, 1735048802000),
  (4, 2025, 39.7392, -104.9903, 1, 1766584800000),
  (5, 2025, 39.7401, -104.9910, 2, 1766584801000),
  (6, 2025, 39.7410, -104.9917, 3, 1766584802000);", conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        // Event_updates — one per year plus one whose year has no event (skipped).
        await using (var cmd = new NpgsqlCommand(@"
insert into event_updates (id, message, time, created_at) values
  (100, 'Kickoff 2024', timestamp with time zone '2024-12-24 19:00Z', timestamp with time zone '2024-12-24 19:05Z'),
  (101, 'Kickoff 2025', timestamp with time zone '2025-12-24 19:00Z', timestamp with time zone '2025-12-24 19:05Z'),
  (102, 'Stray 2019',   null,                                          timestamp with time zone '2019-12-24 19:05Z');", conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        // Funds — two 2024 rows (later one wins with 75), one 2025 row.
        await using (var cmd = new NpgsqlCommand(@"
insert into funds (percent, id, created) values
  (30,  'a', '1735048800000'),
  (75,  'b', '1735048900000'),
  (50,  'c', '1766584800000');", conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        // Messages — one legitimate, one unparseable created (skipped).
        await using (var cmd = new NpgsqlCommand(@"
insert into messages (name, email, message, created, id) values
  ('Alice', 'a@example.com', 'Hi there',    '1735048800000', 'msg-a'),
  ('Bob',   'b@example.com', 'Broken time', 'not-a-number',  'msg-b');", conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
