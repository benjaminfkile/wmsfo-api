using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wmsfo.Api.Data;

namespace Wmsfo.Api.IntegrationTests;

// A4 acceptance criterion 699: the initial migration applied against an empty
// database produces exactly the tables, indexes, and constraints of sql.md 4.1,
// and the three partial unique indexes raise 23505.
public sealed class MigrationTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public MigrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private DbContextOptions<WmsfoDbContext> Options() =>
        new DbContextOptionsBuilder<WmsfoDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

    [Fact]
    public async Task Initial_migration_creates_every_object_of_sql_md_section_4_1()
    {
        await using (var db = new WmsfoDbContext(Options()))
        {
            await db.Database.MigrateAsync();
        }

        var relations = await ListRelationsAsync();
        foreach (var expected in ExpectedTables)
        {
            Assert.Contains(expected, relations.Tables);
        }

        foreach (var expected in ExpectedIndexes)
        {
            Assert.Contains(expected, relations.Indexes);
        }

        foreach (var expected in ExpectedConstraints)
        {
            Assert.Contains(expected, relations.Constraints);
        }
    }

    [Fact]
    public async Task Partial_unique_indexes_raise_23505()
    {
        await using (var db = new WmsfoDbContext(Options()))
        {
            await db.Database.MigrateAsync();
        }

        await AssertPartialUniqueIndexRaisesAsync(
            constraint: "event_one_live",
            firstInsert: "insert into event (year, name, status_id, created_by) values (2021, 'a', 3, 'test')",
            secondInsert: "insert into event (year, name, status_id, created_by) values (2022, 'b', 3, 'test')");

        await AssertPartialUniqueIndexRaisesAsync(
            constraint: "event_one_current",
            firstInsert: "insert into event (year, name, status_id, is_current, created_by) values (2101, 'a', 1, true, 'test')",
            secondInsert: "insert into event (year, name, status_id, is_current, created_by) values (2102, 'b', 1, true, 'test')");

        await AssertPartialUniqueIndexRaisesAsync(
            constraint: "beacon_one_active",
            firstInsert: "insert into beacon (name, role, key_hash, key_prefix, is_active, created_by) values ('b1', 'beacon', E'\\\\x0102', 'p1', true, 'test')",
            secondInsert: "insert into beacon (name, role, key_hash, key_prefix, is_active, created_by) values ('b2', 'beacon', E'\\\\x0304', 'p2', true, 'test')");
    }

    private async Task AssertPartialUniqueIndexRaisesAsync(string constraint, string firstInsert, string secondInsert)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        await Exec(conn, firstInsert);
        var ex = await Assert.ThrowsAsync<PostgresException>(async () => await Exec(conn, secondInsert));
        Assert.Equal("23505", ex.SqlState);
        Assert.Equal(constraint, ex.ConstraintName);

        // clean up for the next assertion in this test
        if (constraint == "event_one_live")
        {
            await Exec(conn, "delete from event where year in (2021)");
        }
        else if (constraint == "event_one_current")
        {
            await Exec(conn, "delete from event where year in (2101)");
        }
        else if (constraint == "beacon_one_active")
        {
            await Exec(conn, "delete from beacon where name = 'b1'");
        }
    }

    private static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<Relations> ListRelationsAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        var tables = new HashSet<string>();
        await using (var cmd = new NpgsqlCommand(
            "select table_name from information_schema.tables where table_schema = 'public'", conn))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        }

        var indexes = new HashSet<string>();
        await using (var cmd = new NpgsqlCommand(
            "select indexname from pg_indexes where schemaname = 'public'", conn))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) indexes.Add(reader.GetString(0));
        }

        var constraints = new HashSet<string>();
        await using (var cmd = new NpgsqlCommand(
            "select conname from pg_constraint where connamespace = 'public'::regnamespace", conn))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) constraints.Add(reader.GetString(0));
        }

        return new Relations(tables, indexes, constraints);
    }

    private sealed record Relations(HashSet<string> Tables, HashSet<string> Indexes, HashSet<string> Constraints);

    private static readonly string[] ExpectedTables =
    {
        "event_status", "event", "event_status_history", "event_message",
        "route", "beacon", "beacon_enrollment_token", "beacon_log", "location",
        "sponsor", "sponsor_year", "person", "subscriber",
        "cookie_type", "cookie", "contact_message", "app_setting",
        "media_asset", "page", "section", "section_item",
        "site_setting_draft", "content_version", "preview_token",
        "icon_library_state", "snapshot", "live_state", "outbox", "alert_delivery",
    };

    // sql.md 4.1 index inventory.
    private static readonly string[] ExpectedIndexes =
    {
        "event_status_pkey", "event_status_name_key",
        "route_pkey", "route_s3_key_key",
        "event_pkey", "event_year_key", "event_one_live", "event_one_current",
        "event_status_history_pkey", "event_status_history_event",
        "event_message_pkey", "event_message_event_created",
        "beacon_pkey", "beacon_key_hash_key", "beacon_one_active",
        "beacon_enrollment_token_pkey", "beacon_enrollment_token_token_hash_key",
        "beacon_log_pkey", "beacon_log_beacon",
        "location_pkey", "location_event_id_seq_key", "location_event_published_seq",
        "sponsor_pkey", "sponsor_logo_media",
        "sponsor_year_pkey", "sponsor_year_sponsor_id_event_year_key",
        "person_pkey", "person_cognito_sub_key",
        "subscriber_pkey", "subscriber_channel_address_key",
        "subscriber_unsubscribe_token_key", "subscriber_person", "subscriber_verify_token_hash",
        "cookie_type_pkey",
        "cookie_pkey", "cookie_event_person", "cookie_event_type_visible", "cookie_person",
        "contact_message_pkey", "app_setting_pkey",
        "media_asset_pkey", "media_asset_s3_key_key", "media_asset_state_created",
        "page_pkey", "page_slug_key", "page_one_per_role",
        "section_pkey", "section_page_position",
        "section_item_pkey", "section_item_section_position",
        "site_setting_draft_pkey", "icon_library_state_pkey",
        "content_version_pkey",
        "preview_token_pkey", "preview_token_token_hash_key",
        "snapshot_pkey", "live_state_pkey",
        "outbox_pkey", "outbox_unpublished",
        "alert_delivery_pkey", "alert_delivery_subscriber_id_outbox_id_key",
        "alert_delivery_unsent", "alert_delivery_outbox",
    };

    private static readonly string[] ExpectedConstraints =
    {
        // Primary keys and unique constraints named per sql.md 1.
        "event_status_pkey", "event_status_name_key",
        "route_pkey", "route_s3_key_key",
        "event_pkey", "event_year_key",
        "event_status_history_pkey",
        "event_message_pkey",
        "beacon_pkey", "beacon_key_hash_key",
        "beacon_enrollment_token_pkey", "beacon_enrollment_token_token_hash_key",
        "beacon_log_pkey",
        "location_pkey", "location_event_id_seq_key",
        "sponsor_pkey",
        "sponsor_year_pkey", "sponsor_year_sponsor_id_event_year_key",
        "person_pkey", "person_cognito_sub_key",
        "subscriber_pkey", "subscriber_channel_address_key", "subscriber_unsubscribe_token_key",
        "cookie_type_pkey",
        "cookie_pkey",
        "contact_message_pkey",
        "app_setting_pkey",
        "media_asset_pkey", "media_asset_s3_key_key",
        "page_pkey", "page_slug_key",
        "section_pkey",
        "section_item_pkey",
        "site_setting_draft_pkey",
        "content_version_pkey",
        "preview_token_pkey", "preview_token_token_hash_key",
        "snapshot_pkey",
        "live_state_pkey",
        "outbox_pkey",
        "alert_delivery_pkey", "alert_delivery_subscriber_id_outbox_id_key",
        "icon_library_state_pkey",
        // Foreign keys.
        "event_status_id_fkey", "event_route_id_fkey",
        "event_status_history_event_id_fkey",
        "event_status_history_from_status_id_fkey", "event_status_history_to_status_id_fkey",
        "event_message_event_id_fkey",
        "beacon_enrollment_token_beacon_id_fkey",
        "beacon_log_beacon_id_fkey",
        "location_event_id_fkey", "location_beacon_id_fkey",
        "sponsor_logo_media_id_fkey",
        "sponsor_year_sponsor_id_fkey",
        "subscriber_person_id_fkey",
        "cookie_event_id_fkey", "cookie_person_id_fkey", "cookie_cookie_type_id_fkey",
        "section_page_id_fkey",
        "section_item_section_id_fkey",
        "alert_delivery_outbox_id_fkey", "alert_delivery_subscriber_id_fkey",
        // Check constraints.
        "event_funds_percent_check",
        "beacon_role_check",
        "sponsor_year_amount_donated_check",
        "subscriber_channel_check",
        "media_asset_kind_check", "media_asset_state_check",
        "page_role_check",
        "snapshot_id_check", "live_state_id_check",
        "site_setting_draft_id_check", "icon_library_state_id_check",
    };
}
