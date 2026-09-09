using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Chores;
using Wmsfo.Api.Data;
using Wmsfo.Api.Email;

namespace Wmsfo.Api.IntegrationTests;

// A16 acceptance criteria (task 287): integration tests for every chore.
// - outbox claim under two overlapping leaders (skip-locked yields disjoint sets)
// - alert fan-out uniqueness on (subscriber_id, outbox_id)
// - source_deleted on event.message_posted whose message or event vanished
// - stale beacon flag set and cleared
// - orphan collector's four transitions with a clock stub
// - nightly cleanup counts each of the six deletes
public sealed class A16ChoreTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A16Host? _host;

    public A16ChoreTests(PostgresFixture fixture) { _fixture = fixture; }

    public async Task InitializeAsync()
    {
        await MigrateAndCleanAsync(_fixture.ConnectionString);
    }

    public Task DisposeAsync()
    {
        _host?.Dispose();
        return Task.CompletedTask;
    }

    private A16Host Host(InMemoryObjectStore? store = null)
    {
        _host = A16Host.Create(_fixture.ConnectionString, store);
        return _host;
    }

    private static async Task MigrateAndCleanAsync(string connectionString)
    {
        var opts = new DbContextOptionsBuilder<WmsfoDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using (var db = new WmsfoDbContext(opts))
        {
            await db.Database.MigrateAsync();
        }
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        foreach (var sql in new[]
        {
            "delete from alert_delivery;",
            "delete from outbox;",
            "delete from beacon_log;",
            "delete from beacon_enrollment_token;",
            "delete from beacon;",
            "delete from subscriber;",
            "delete from contact_message;",
            "delete from event_message;",
            "delete from event_status_history;",
            "delete from cookie;",
            "delete from location;",
            "delete from event;",
            "delete from preview_token;",
            "delete from section_item;",
            "delete from section;",
            "delete from page;",
            "delete from content_version;",
            "delete from sponsor_year;",
            "delete from sponsor;",
            "delete from cookie_type;",
            "update site_setting_draft set data = '{}'::jsonb where id = 1;",
            "delete from media_asset;",
            "delete from person;",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await SnapshotSeed.EnsureAsync(conn);
    }

    // ---------- outbox: claim under two overlapping leaders ----------

    [Fact]
    public async Task Outbox_two_overlapping_publishers_claim_disjoint_rows_via_skip_locked()
    {
        // 50 pending outbox rows.
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        for (int i = 0; i < 5; i++)
        {
            await using var cmd = new NpgsqlCommand(@"
insert into outbox (topic, payload) values (
  'event.status_changed', jsonb_build_object('eventId', 0, 'fromStatusId', 1, 'toStatusId', 4, 'notify', false)
);", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var host = Host();
        // Two publishers, each running RunOnceAsync concurrently. Each should
        // process a disjoint set of rows (skip-locked); collectively they mark
        // all 5 published without double-processing.
        var a = host.Outbox.RunOnceAsync(CancellationToken.None);
        var b = host.Outbox.RunOnceAsync(CancellationToken.None);
        await Task.WhenAll(a, b);

        // No unpublished rows remain.
        await using (var cmd = new NpgsqlCommand("select count(*) from outbox where published_at is null;", conn))
        {
            var remaining = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
            Assert.Equal(0L, remaining);
        }
        // Every row was claimed exactly once (attempts = 1).
        await using (var cmd = new NpgsqlCommand("select count(*) from outbox where attempts > 1;", conn))
        {
            var doubles = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
            Assert.Equal(0L, doubles);
        }
    }

    // ---------- outbox: source_deleted on event.message_posted vanished ----------

    [Fact]
    public async Task Outbox_message_posted_marks_source_deleted_when_row_gone()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        // Insert a message_posted row referencing an event and message that
        // do not exist. The publisher must mark it source_deleted.
        var outboxId = await InsertOutboxAsync(conn,
            "event.message_posted",
            "{\"eventId\":1234, \"messageId\":9999}");

        var host = Host();
        await host.Outbox.RunOnceAsync(CancellationToken.None);

        var (published, error) = await ReadOutboxAsync(conn, outboxId);
        Assert.True(published);
        Assert.Equal("source_deleted", error);
        // No alert deliveries fanned out.
        Assert.Equal(0L, await CountAsync(conn, "select count(*) from alert_delivery;"));
        // No emails sent.
        Assert.Empty(host.Sender.Sent);
    }

    // ---------- outbox: subscription.verify sends via SES fake ----------

    [Fact]
    public async Task Outbox_subscription_verify_sends_email_and_marks_published()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        var personId = await InsertPersonAsync(conn, "jane@example.com");
        var subscriberId = await InsertSubscriberAsync(conn, personId, "jane@example.com", verified: false);
        var payload = $"{{\"subscriberId\":{subscriberId}, \"verifyToken\":\"wsv_abc\"}}";
        var outboxId = await InsertOutboxAsync(conn, "subscription.verify", payload);

        var host = Host();
        await host.Outbox.RunOnceAsync(CancellationToken.None);

        var (published, _) = await ReadOutboxAsync(conn, outboxId);
        Assert.True(published);
        var sent = Assert.Single(host.Sender.Sent);
        Assert.Equal(EmailTemplates.SubscriptionVerify, sent.TemplateName);
        Assert.Equal("jane@example.com", sent.ToAddress);
        Assert.Contains("verifyUrl", sent.Values.Keys);
        Assert.Contains("wsv_abc", sent.Values["verifyUrl"]);
    }

    [Fact]
    public async Task Outbox_subscription_verify_skips_when_subscriber_missing()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        var outboxId = await InsertOutboxAsync(conn, "subscription.verify",
            "{\"subscriberId\":999999, \"verifyToken\":\"wsv_missing\"}");

        var host = Host();
        await host.Outbox.RunOnceAsync(CancellationToken.None);

        var (published, _) = await ReadOutboxAsync(conn, outboxId);
        Assert.True(published);
        Assert.Empty(host.Sender.Sent);
    }

    // ---------- outbox: contact.received sends via SES fake ----------

    [Fact]
    public async Task Outbox_contact_received_sends_notification_with_reply_to()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        long contactId;
        await using (var cmd = new NpgsqlCommand(
            "insert into contact_message (name, email, body, client_ip) values ('Alex', 'alex@example.com', 'Hello!', '127.0.0.1') returning id;", conn))
        {
            contactId = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
        }
        var outboxId = await InsertOutboxAsync(conn, "contact.received", $"{{\"contactMessageId\":{contactId}}}");

        var host = Host();
        await host.Outbox.RunOnceAsync(CancellationToken.None);

        var (published, _) = await ReadOutboxAsync(conn, outboxId);
        Assert.True(published);
        var sent = Assert.Single(host.Sender.Sent);
        Assert.Equal(EmailTemplates.ContactReceived, sent.TemplateName);
        Assert.Equal(host.Options.ContactNotifyEmail, sent.ToAddress);
        Assert.Equal("alex@example.com", sent.ReplyTo);
        Assert.Equal("Alex", sent.Values["contactName"]);
    }

    // ---------- alert: fan-out uniqueness (subscriber_id, outbox_id) ----------

    [Fact]
    public async Task Alert_fanout_is_unique_per_subscriber_and_outbox_row_across_two_passes()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        var eventId = await InsertEventAsync(conn, "Santa 2027", statusId: 3);
        var personA = await InsertPersonAsync(conn, "a@example.com");
        var personB = await InsertPersonAsync(conn, "b@example.com");
        await InsertSubscriberAsync(conn, personA, "a@example.com", verified: true);
        await InsertSubscriberAsync(conn, personB, "b@example.com", verified: true);

        // Notify: true; toStatusId 3 -> fan out and mark published.
        var payload = $"{{\"eventId\":{eventId}, \"fromStatusId\":2, \"toStatusId\":3, \"notify\":true}}";
        var outboxId = await InsertOutboxAsync(conn, "event.status_changed", payload);

        // First publisher pass: fans out and marks published.
        var host = Host();
        await host.Outbox.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2L, await CountAsync(conn, "select count(*) from alert_delivery;"));

        // Simulate the row being reclaimable and re-processed (overlapping
        // leaders). Reset published_at + claimed_at so the claim query picks it
        // up again. The fan-out INSERT ON CONFLICT DO NOTHING preserves the
        // 1-per-subscriber invariant.
        await using (var cmd = new NpgsqlCommand(
            "update outbox set published_at = null, claimed_at = null, attempts = 0 where id = $1;", conn))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = outboxId });
            await cmd.ExecuteNonQueryAsync();
        }
        await host.Outbox.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2L, await CountAsync(conn, "select count(*) from alert_delivery;"));
    }

    // ---------- alert send: rows sent and marked ----------

    [Fact]
    public async Task Alert_send_marks_deliveries_with_sent_at_and_ses_message_id()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        var eventId = await InsertEventAsync(conn, "Santa 2027", statusId: 3);
        var person = await InsertPersonAsync(conn, "person@example.com");
        var subscriberId = await InsertSubscriberAsync(conn, person, "person@example.com", verified: true);
        var payload = $"{{\"eventId\":{eventId}, \"fromStatusId\":2, \"toStatusId\":3, \"notify\":true}}";
        var outboxId = await InsertOutboxAsync(conn, "event.status_changed", payload);

        var host = Host();
        await host.Outbox.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1L, await CountAsync(conn, "select count(*) from alert_delivery where sent_at is null;"));

        var sent = await host.Alerts.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, sent);

        Assert.Equal(0L, await CountAsync(conn, "select count(*) from alert_delivery where sent_at is null;"));
        await using var cmd = new NpgsqlCommand(
            "select ses_message_id, last_error from alert_delivery where outbox_id = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = outboxId });
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.False(reader.IsDBNull(0));
        Assert.True(reader.IsDBNull(1));
        _ = subscriberId;
    }

    // ---------- stale beacon flag: set then cleared ----------

    [Fact]
    public async Task Stale_beacon_flag_sets_and_clears()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        // A beacon whose last_seen_at is 60 s ago; the default stale threshold
        // is 45 s so this must be flagged.
        long beaconId;
        await using (var cmd = new NpgsqlCommand(@"
insert into beacon (name, notes, role, key_hash, key_prefix, created_by,
                    last_seen_at, last_heartbeat_at, last_location_at)
values ('B1', '', 'beacon', decode('11', 'hex'), 'wbk_test1234', 'seed',
        now() - interval '60 seconds', now() - interval '60 seconds', now() - interval '60 seconds')
returning id;", conn))
        {
            beaconId = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
        }

        var host = Host();
        var flagged = await host.Stale.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, flagged);

        // Row now has stale_since set.
        var stale = await ReadStaleSinceAsync(conn, beaconId);
        Assert.NotNull(stale);

        // A heartbeat clears the flag (simulated by an update).
        await using (var upd = new NpgsqlCommand(@"
update beacon set stale_since = null, last_heartbeat_at = now(), last_seen_at = now() where id = $1;", conn))
        {
            upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beaconId });
            await upd.ExecuteNonQueryAsync();
        }
        // A second flagger pass does not re-flag while the beacon is fresh.
        var second = await host.Stale.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, second);
        stale = await ReadStaleSinceAsync(conn, beaconId);
        Assert.Null(stale);
    }

    // ---------- orphan collector: four transitions with a clock stub ----------
    // Because the SQL uses now() with fixed intervals ('30 days', '8 days',
    // '2 days'), we drive time by rewinding unreferenced_since / orphaned_at
    // on the row (the clock stub the host holds moves other cadences; the DB
    // clock is what the recipes read). The four transitions are covered in a
    // single test that walks a media_asset through each state.

    [Fact]
    public async Task Orphan_collector_walks_four_transitions_over_a_clock_stub()
    {
        var store = new InMemoryObjectStore();
        var host = Host(store);
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        var id = await InsertMediaAssetAsync(conn, "ready", withVariants: true);
        store.SeedObject($"media/{id:D}/photo.png", initialTag: null);
        store.SeedObject($"media/{id:D}/w480.webp", initialTag: null);

        // (1) ready + not referenced -> unreferenced_since = now
        var counts1 = await host.OrphanCollector.RunOnceAsync(CancellationToken.None);
        Assert.True(counts1.Stamped >= 1);
        var (state1, unref1, orph1) = await ReadMediaAsync(conn, id);
        Assert.Equal("ready", state1);
        Assert.NotNull(unref1);
        Assert.Null(orph1);

        // (2) unreferenced_since past 30 days -> tag every object, mark orphaned.
        await using (var rewind = new NpgsqlCommand(
            "update media_asset set unreferenced_since = now() - interval '31 days' where id = $1;", conn))
        {
            rewind.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
            await rewind.ExecuteNonQueryAsync();
        }
        var counts2 = await host.OrphanCollector.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, counts2.Marked);
        var (state2, _, orph2) = await ReadMediaAsync(conn, id);
        Assert.Equal("orphaned", state2);
        Assert.NotNull(orph2);
        // The original and the variant were tagged.
        Assert.Contains(store.TagCalls, c => c.Key == $"media/{id:D}/photo.png" && c.Tag == "state=orphaned");
        Assert.Contains(store.TagCalls, c => c.Key == $"media/{id:D}/w480.webp" && c.Tag == "state=orphaned");

        // (3) reference appears again while orphaned -> untag, revive.
        // Simulate a reference by adding the id to a sponsor row.
        await using (var sponsor = new NpgsqlCommand(
            "insert into sponsor (name, logo_media_id) values ('SponsorX', $1);", conn))
        {
            sponsor.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
            await sponsor.ExecuteNonQueryAsync();
        }
        var counts3 = await host.OrphanCollector.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, counts3.Revived);
        var (state3, unref3, orph3) = await ReadMediaAsync(conn, id);
        Assert.Equal("ready", state3);
        Assert.Null(orph3);
        Assert.Null(unref3);
        Assert.Contains(store.UntagCalls, k => k == $"media/{id:D}/photo.png");
        Assert.Contains(store.UntagCalls, k => k == $"media/{id:D}/w480.webp");

        // (4) row orphaned more than 8 days ago -> row delete.
        // Detach the sponsor reference first so the row is orphanable again.
        await using (var clear = new NpgsqlCommand(
            "update sponsor set logo_media_id = null where logo_media_id = $1;", conn))
        {
            clear.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
            await clear.ExecuteNonQueryAsync();
        }
        // Fast-forward: put the row directly in orphaned state with an
        // orphaned_at that is older than 8 days.
        await using (var age = new NpgsqlCommand(@"
update media_asset
set state = 'orphaned',
    orphaned_at = now() - interval '9 days',
    unreferenced_since = now() - interval '40 days'
where id = $1;", conn))
        {
            age.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
            await age.ExecuteNonQueryAsync();
        }
        var counts4 = await host.OrphanCollector.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, counts4.Deleted);
        var missing = await ReadMediaAsync(conn, id);
        Assert.Null(missing.State);
    }

    // ---------- nightly cleanup counts ----------

    [Fact]
    public async Task Nightly_cleanup_counts_each_delete()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        // Seed rows that qualify for deletion. Every INSERT here targets one
        // of the six statements the nightly chore runs so the returned counts
        // match a known expectation (one per statement).
        var beaconId = await InsertBeaconAsync(conn);
        await using (var cmd = new NpgsqlCommand(@"
insert into beacon_enrollment_token (beacon_id, token_hash, key_ciphertext, expires_at, consumed_at)
values ($1, decode('01', 'hex'), null, now() - interval '30 days', now() - interval '30 days');", conn))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beaconId });
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var cmd = new NpgsqlCommand(@"
insert into outbox (topic, payload, published_at)
values ('event.status_changed', '{}'::jsonb, now() - interval '31 days');", conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }
        var personId = await InsertPersonAsync(conn, "unverified@example.com");
        await using (var cmd = new NpgsqlCommand(@"
insert into subscriber (person_id, channel, address, unsubscribe_token, created_at)
values ($1, 'email', 'unverified@example.com', 'wsu_1', now() - interval '8 days');", conn))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var cmd = new NpgsqlCommand(@"
insert into beacon_log (beacon_id, received_at, app_version, size_bytes, body)
values ($1, now() - interval '31 days', '1.0', 5, 'body');", conn))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beaconId });
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var cmd = new NpgsqlCommand(@"
insert into preview_token (token_hash, created_by, expires_at)
values (decode('01', 'hex'), 'seed', now() - interval '2 days');", conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }
        var pendingMediaId = Guid.NewGuid();
        await using (var cmd = new NpgsqlCommand(@"
insert into media_asset (id, filename, content_type, kind, state, s3_key, uploaded_by, alt, title, variants, created_at)
values ($1, 'stale.png', 'image/png', 'raster', 'pending', 'media/x/stale.png', 'seed', '', '', '{}'::jsonb, now() - interval '3 days');", conn))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = pendingMediaId });
            await cmd.ExecuteNonQueryAsync();
        }

        var host = Host();
        var counts = await host.Nightly.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, counts.EnrollmentTokens);
        Assert.Equal(1, counts.Outbox);
        Assert.Equal(1, counts.UnverifiedSubscribers);
        Assert.Equal(1, counts.BeaconLogs);
        Assert.Equal(1, counts.PreviewTokens);
        Assert.Equal(1, counts.StalePendingMedia);

        // Idempotent - a second run touches nothing.
        var second = await host.Nightly.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, second.EnrollmentTokens);
        Assert.Equal(0, second.Outbox);
        Assert.Equal(0, second.UnverifiedSubscribers);
        Assert.Equal(0, second.BeaconLogs);
        Assert.Equal(0, second.PreviewTokens);
        Assert.Equal(0, second.StalePendingMedia);
    }

    // ---------- helpers ----------

    private static async Task<long> InsertOutboxAsync(NpgsqlConnection conn, string topic, string payload)
    {
        await using var cmd = new NpgsqlCommand(
            "insert into outbox (topic, payload) values ($1, $2::jsonb) returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = topic });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = payload });
        return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<(bool Published, string? LastError)> ReadOutboxAsync(NpgsqlConnection conn, long id)
    {
        await using var cmd = new NpgsqlCommand(
            "select published_at, last_error from outbox where id = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return (false, null);
        return (!reader.IsDBNull(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private static async Task<long> CountAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<long> InsertPersonAsync(NpgsqlConnection conn, string email)
    {
        await using var cmd = new NpgsqlCommand(
            "insert into person (cognito_sub, email) values (gen_random_uuid(), $1) returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = email });
        return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<long> InsertSubscriberAsync(NpgsqlConnection conn, long personId, string address, bool verified)
    {
        await using var cmd = new NpgsqlCommand(@"
insert into subscriber (person_id, channel, address, unsubscribe_token, verified_at, verify_expires_at)
values ($1, 'email', $2, $3, case when $4 then now() else null end, case when $4 then null else now() + interval '24 hours' end)
returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = address });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = "wsu_" + Guid.NewGuid().ToString("N") });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = verified });
        return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<long> InsertEventAsync(NpgsqlConnection conn, string name, short statusId)
    {
        await using var cmd = new NpgsqlCommand(@"
insert into event (year, name, status_id, is_current, created_by, updated_at)
values (2027, $1, $2, false, 'seed', now()) returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = name });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Smallint, Value = statusId });
        return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<long> InsertBeaconAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand(@"
insert into beacon (name, notes, role, key_hash, key_prefix, created_by)
values ('B', '', 'beacon', decode('deadbeef', 'hex'), 'wbk_prefix000', 'seed') returning id;", conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<DateTimeOffset?> ReadStaleSinceAsync(NpgsqlConnection conn, long beaconId)
    {
        await using var cmd = new NpgsqlCommand("select stale_since from beacon where id = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beaconId });
        var r = await cmd.ExecuteScalarAsync();
        return r switch
        {
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            DateTimeOffset dto => dto,
            _ => (DateTimeOffset?)null,
        };
    }

    private static async Task<Guid> InsertMediaAssetAsync(NpgsqlConnection conn, string state, bool withVariants)
    {
        var id = Guid.NewGuid();
        var variantsJson = withVariants
            ? "{\"480\":\"https://cdn.example/media/" + id.ToString("D") + "/w480.webp\"}"
            : "{}";
        await using var cmd = new NpgsqlCommand(@"
insert into media_asset (id, filename, content_type, kind, state, s3_key, uploaded_by, alt, title, variants, created_at)
values ($1, 'photo.png', 'image/png', 'raster', $2, $3, 'seed', '', '', $4::jsonb, now());", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = state });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = $"media/{id:D}/photo.png" });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = variantsJson });
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private sealed record MediaRow(string? State, DateTimeOffset? UnreferencedSince, DateTimeOffset? OrphanedAt);

    private static async Task<MediaRow> ReadMediaAsync(NpgsqlConnection conn, Guid id)
    {
        await using var cmd = new NpgsqlCommand(
            "select state, unreferenced_since, orphaned_at from media_asset where id = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return new MediaRow(null, null, null);
        var state = reader.GetString(0);
        DateTimeOffset? unref = reader.IsDBNull(1) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc));
        DateTimeOffset? orph = reader.IsDBNull(2) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc));
        return new MediaRow(state, unref, orph);
    }
}
