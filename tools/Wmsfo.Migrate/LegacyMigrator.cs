using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Media;
using Wmsfo.Api.Objects;
using Wmsfo.Api.Security;

namespace Wmsfo.Migrate;

// sql.md 15: the one-off migration tool. Reads the legacy database and legacy
// bucket, writes the new database and the target bucket. Idempotent by natural
// key: rerunning after a failure or on new legacy rows inserts only the
// missing ones. The unit boundary per step is documented in sql.md 15.
//
// Wire-up: hand it two open connections (legacy read-only, target migrate
// role), an IObjectStore for the target bucket, and a legacy-logo source. The
// integration test drives it directly; Program.cs is the CLI shell.
public sealed class LegacyMigrator
{
    public const string MigrationActor = "migration";
    public const string LegacyBeaconName = "legacy";
    public const string LegacyBeaconNotes = "Synthetic sender for migrated flight_history rows.";
    public const string LegacyMediaTitlePrefix = "legacy:";
    public const string RouteName2025 = "2025 flight";
    public const string SettingSponsorLingerPerDollar = "sponsor_linger_ms_per_dollar";
    public const string ImmutableCacheControl = "public, max-age=31536000, immutable";
    public const string RouteJsonContentType = "application/json; charset=utf-8";

    private readonly MigrateOptions _options;
    private readonly IObjectStore _target;
    private readonly ILegacyLogoSource _legacyLogos;
    private readonly TextWriter _output;
    private readonly TimeZoneInfo _denverTz;

    public LegacyMigrator(
        MigrateOptions options,
        IObjectStore target,
        ILegacyLogoSource legacyLogos,
        TextWriter output)
    {
        _options = options;
        _target = target;
        _legacyLogos = legacyLogos;
        _output = output;
        _denverTz = ResolveDenver(options.DenverTimeZoneId);
    }

    private static TimeZoneInfo ResolveDenver(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return TimeZoneInfo.Utc; }
    }

    public async Task<MigrationSummary> RunAsync(CancellationToken ct)
    {
        var summary = new MigrationSummary { DryRun = _options.DryRun };

        _output.WriteLine($"wmsfo migrate: bucket={_options.Bucket} cdn={_options.CdnBaseUrl.TrimEnd('/')} dryRun={_options.DryRun}");

        await using var legacy = new NpgsqlConnection(_options.LegacyConnection);
        await legacy.OpenAsync(ct);
        await using var target = new NpgsqlConnection(_options.TargetConnection);
        await target.OpenAsync(ct);

        var legacyBeaconId = await Step1LegacyBeaconAsync(target, summary, ct);
        await Step2SponsorsAsync(legacy, target, summary, ct);
        await Step3SponsorYearsAsync(legacy, target, summary, ct);
        await Step4SponsorSettingAsync(legacy, target, summary, ct);
        var yearEventIds = await Step5FlightHistoryAsync(legacy, target, legacyBeaconId, summary, ct);
        await Step6Route2025Async(legacy, target, yearEventIds, summary, ct);
        await Step7EventMessagesAsync(legacy, target, summary, ct);
        await Step8FundsAsync(legacy, target, summary, ct);
        await Step9ContactMessagesAsync(legacy, target, summary, ct);

        _output.WriteLine();
        _output.WriteLine("=== summary ===");
        foreach (var s in summary.Steps) _output.WriteLine("  " + s);
        _output.WriteLine($"  total: considered={summary.TotalConsidered} inserted={summary.TotalInserted} skipped={summary.TotalSkipped}");

        await PrintVerificationQueriesAsync(target, ct);
        return summary;
    }

    // ----- Step 1: legacy beacon -----

    // Natural key: (name = 'legacy', created_by = 'migration'). Insert only when
    // absent; the returned id feeds the location.beacon_id column in step 5.
    private async Task<long> Step1LegacyBeaconAsync(NpgsqlConnection target, MigrationSummary summary, CancellationToken ct)
    {
        var step = summary.AddStep("legacy_beacon");
        step.Considered = 1;

        var existing = await ScalarLongAsync(target,
            "select id from beacon where name = $1 and created_by = $2 limit 1;",
            (NpgsqlDbType.Text, LegacyBeaconName), (NpgsqlDbType.Text, MigrationActor));
        if (existing is long id)
        {
            step.Skipped = 1;
            step.Notes.Add($"already present id={id}");
            return id;
        }
        if (_options.DryRun)
        {
            step.Notes.Add("dry run: skipped insert");
            return 0;
        }

        var minted = Keys.MintKey();
        var newId = await ScalarLongAsync(target,
            @"insert into beacon (name, notes, role, key_hash, key_prefix, is_active, revoked_at, created_by, updated_at)
              values ($1, $2, 'beacon', $3, $4, false, now(), $5, now())
              returning id;",
            (NpgsqlDbType.Text, LegacyBeaconName),
            (NpgsqlDbType.Text, LegacyBeaconNotes),
            (NpgsqlDbType.Bytea, minted.Hash),
            (NpgsqlDbType.Text, minted.Prefix),
            (NpgsqlDbType.Text, MigrationActor))
            ?? throw new InvalidOperationException("legacy beacon insert returned no id");
        step.Inserted = 1;
        return newId;
    }

    // ----- Step 2: sponsors (id preserved) + logos through the confirm slice -----

    // Two step results: sponsor rows and legacy-logo imports. Each is
    // idempotent on its own natural key.
    private async Task Step2SponsorsAsync(NpgsqlConnection legacy, NpgsqlConnection target, MigrationSummary summary, CancellationToken ct)
    {
        var step = summary.AddStep("sponsors");
        var legacyRows = await ReadLegacySponsorsAsync(legacy, ct);
        step.Considered = legacyRows.Count;

        // Insert (or skip) sponsor rows.
        foreach (var row in legacyRows)
        {
            var exists = await ScalarLongAsync(target,
                "select id from sponsor where id = $1;", (NpgsqlDbType.Bigint, (long)row.Id));
            if (exists is not null)
            {
                step.Skipped++;
                continue;
            }
            if (_options.DryRun)
            {
                step.Notes.Add($"dry run: would insert sponsor id={row.Id}");
                continue;
            }
            await ExecAsync(target, @"
insert into sponsor (id, name, contact_person, email, phone, address,
                     website_url, fb_url, ig_url, created_at, updated_at)
overriding system value
values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
on conflict on constraint sponsor_pkey do nothing;",
                (NpgsqlDbType.Bigint, (long)row.Id),
                (NpgsqlDbType.Text, row.Name),
                Nullable(NpgsqlDbType.Text, row.ContactPerson),
                Nullable(NpgsqlDbType.Text, row.Email),
                Nullable(NpgsqlDbType.Text, row.Phone),
                Nullable(NpgsqlDbType.Text, row.Address),
                Nullable(NpgsqlDbType.Text, row.WebsiteUrl),
                Nullable(NpgsqlDbType.Text, row.FbUrl),
                Nullable(NpgsqlDbType.Text, row.IgUrl),
                (NpgsqlDbType.TimestampTz, (object?)row.CreatedAtUtc ?? DateTime.UtcNow),
                (NpgsqlDbType.TimestampTz, (object?)row.UpdatedAtUtc ?? DateTime.UtcNow));
            step.Inserted++;
        }
        if (!_options.DryRun) await SetvalAsync(target, "sponsor", "id");

        // Import each legacy logo through the confirm slice. Idempotent on
        // media_asset.title = 'legacy:<legacy_key>'.
        var logoStep = summary.AddStep("legacy_logos");
        var toImport = legacyRows.Where(r => !string.IsNullOrWhiteSpace(r.LogoS3Key)).ToList();
        logoStep.Considered = toImport.Count;
        var importer = new LogoImporter(_target, _legacyLogos, _options, _output);
        foreach (var row in toImport)
        {
            var result = await importer.ImportAsync(row, target, ct);
            switch (result.Status)
            {
                case LogoImportStatus.AlreadyLinked: logoStep.Skipped++; break;
                case LogoImportStatus.ExistingRowRelinked: logoStep.Inserted++; break;
                case LogoImportStatus.Inserted: logoStep.Inserted++; break;
                case LogoImportStatus.SourceMissing:
                    logoStep.Failed++;
                    logoStep.Notes.Add($"sponsor id={row.Id}: legacy object missing ({row.LogoS3Key})");
                    break;
                case LogoImportStatus.ValidationFailed:
                    logoStep.Failed++;
                    logoStep.Notes.Add($"sponsor id={row.Id}: {result.Reason} ({row.LogoS3Key})");
                    break;
                case LogoImportStatus.DryRun:
                    logoStep.Notes.Add($"dry run: would import legacy logo for sponsor id={row.Id}");
                    break;
            }
        }
    }

    // ----- Step 3: sponsor_year (id preserved) -----

    private async Task Step3SponsorYearsAsync(NpgsqlConnection legacy, NpgsqlConnection target, MigrationSummary summary, CancellationToken ct)
    {
        var step = summary.AddStep("sponsor_years");
        var legacyRows = await ReadLegacySponsorYearsAsync(legacy, ct);
        step.Considered = legacyRows.Count;
        foreach (var row in legacyRows)
        {
            if (row.SponsorId is null)
            {
                step.Failed++;
                step.Notes.Add($"sponsor_year id={row.Id}: sponsor_id is null, skipped");
                continue;
            }
            var exists = await ScalarLongAsync(target,
                "select 1 from sponsor_year where sponsor_id = $1 and event_year = $2;",
                (NpgsqlDbType.Bigint, (long)row.SponsorId.Value), (NpgsqlDbType.Integer, row.EventYear));
            if (exists is not null)
            {
                step.Skipped++;
                continue;
            }
            if (_options.DryRun)
            {
                step.Notes.Add($"dry run: would insert sponsor_year id={row.Id}");
                continue;
            }
            var amount = row.AmountDonated;
            if (amount is decimal a)
            {
                if (a < 0m)
                {
                    step.Notes.Add($"sponsor_year id={row.Id}: negative amount stored as null");
                    amount = null;
                }
                else amount = decimal.Round(a, 2);
            }
            await ExecAsync(target, @"
insert into sponsor_year (id, sponsor_id, event_year, amount_donated, active, can_advertise, anonymous, registered_at)
overriding system value
values ($1, $2, $3, $4, $5, $6, $7, $8)
on conflict on constraint sponsor_year_sponsor_id_event_year_key do nothing;",
                (NpgsqlDbType.Bigint, (long)row.Id),
                (NpgsqlDbType.Bigint, (long)row.SponsorId.Value),
                (NpgsqlDbType.Integer, row.EventYear),
                amount is null
                    ? (NpgsqlDbType.Numeric, (object)DBNull.Value)
                    : (NpgsqlDbType.Numeric, (object)amount.Value),
                (NpgsqlDbType.Boolean, (object)(row.Active ?? true)),
                (NpgsqlDbType.Boolean, (object)(row.CanAdvertise ?? true)),
                (NpgsqlDbType.Boolean, (object)(row.Anonymous ?? false)),
                (NpgsqlDbType.TimestampTz, (object?)row.RegisteredAtUtc ?? DateTime.UtcNow));
            step.Inserted++;
        }
        if (!_options.DryRun) await SetvalAsync(target, "sponsor_year", "id");
    }

    // ----- Step 4: sponsor_settings → app_setting -----

    private async Task Step4SponsorSettingAsync(NpgsqlConnection legacy, NpgsqlConnection target, MigrationSummary summary, CancellationToken ct)
    {
        var step = summary.AddStep("sponsor_setting");
        int? linger = null;
        DateTime? updatedAt = null;
        await using (var cmd = new NpgsqlCommand("select linger_rate_ms_per_dollar, updated_at from sponsor_settings limit 1;", legacy))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                linger = reader.GetInt32(0);
                if (!reader.IsDBNull(1)) updatedAt = reader.GetFieldValue<DateTime>(1);
            }
        }
        step.Considered = linger is null ? 0 : 1;
        if (linger is null) return;

        var existing = await ScalarStringAsync(target,
            "select value::text from app_setting where key = $1;",
            (NpgsqlDbType.Text, SettingSponsorLingerPerDollar));
        var desiredValue = linger.Value.ToString(CultureInfo.InvariantCulture);
        if (existing is string s && string.Equals(s.Trim(), desiredValue, StringComparison.Ordinal) &&
            await IsMigrationOwnedAsync(target, SettingSponsorLingerPerDollar, ct))
        {
            step.Skipped = 1;
            return;
        }
        if (_options.DryRun)
        {
            step.Notes.Add("dry run: would upsert sponsor_linger_ms_per_dollar");
            return;
        }
        await ExecAsync(target, @"
insert into app_setting (key, value, updated_by, updated_at)
values ($1, to_jsonb($2::integer), $3, coalesce($4, now()))
on conflict (key) do update set value = excluded.value, updated_by = excluded.updated_by, updated_at = excluded.updated_at;",
            (NpgsqlDbType.Text, SettingSponsorLingerPerDollar),
            (NpgsqlDbType.Integer, linger.Value),
            (NpgsqlDbType.Text, MigrationActor),
            (NpgsqlDbType.TimestampTz, updatedAt is null ? (object)DBNull.Value : updatedAt.Value));
        step.Inserted = 1;
    }

    private static async Task<bool> IsMigrationOwnedAsync(NpgsqlConnection target, string key, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("select updated_by from app_setting where key = $1;", target);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = key });
        var r = await cmd.ExecuteScalarAsync(ct);
        return r is string s && string.Equals(s, MigrationActor, StringComparison.Ordinal);
    }

    // ----- Step 5: flight_history → event + location + next_seq -----

    // Returns year → event id, populated for years both freshly inserted and
    // ones that already existed. Later steps use it (route link, funds update,
    // message routing).
    private async Task<Dictionary<int, long>> Step5FlightHistoryAsync(
        NpgsqlConnection legacy, NpgsqlConnection target, long legacyBeaconId,
        MigrationSummary summary, CancellationToken ct)
    {
        var step = summary.AddStep("flight_history");
        var years = new List<int>();
        await using (var cmd = new NpgsqlCommand("select distinct year from flight_history order by year;", legacy))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) years.Add(reader.GetInt32(0));
        }
        step.Considered = years.Count;
        var yearToEventId = new Dictionary<int, long>();
        foreach (var year in years)
        {
            var existing = await ScalarLongAsync(target,
                "select id from event where year = $1;", (NpgsqlDbType.Integer, year));
            if (existing is long id)
            {
                yearToEventId[year] = id;
                step.Skipped++;
                continue;
            }
            if (_options.DryRun)
            {
                step.Notes.Add($"dry run: would insert event year={year}");
                continue;
            }

            var rows = await ReadFlightHistoryYearAsync(legacy, year, ct);
            if (rows.Count == 0)
            {
                step.Notes.Add($"year {year}: no rows, skipped");
                continue;
            }
            var minMs = rows.Min(r => r.TimeMs);
            var maxMs = rows.Max(r => r.TimeMs);
            var wentLiveAt = DateTimeOffset.FromUnixTimeMilliseconds(minMs).UtcDateTime;
            var endedAt = DateTimeOffset.FromUnixTimeMilliseconds(maxMs).UtcDateTime;
            var count = rows.Count;
            var name = _options.FormatEventName(year);

            await using var tx = await target.BeginTransactionAsync(ct);
            long eventId;
            await using (var ins = new NpgsqlCommand(@"
insert into event (year, name, status_id, is_current, went_live_at, ended_at, next_seq, created_by, created_at, updated_at)
values ($1, $2, 4, false, $3, $4, $5, $6, now(), now())
returning id;", target, tx))
            {
                ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = year });
                ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = name });
                ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = wentLiveAt });
                ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = endedAt });
                ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (long)(count + 1) });
                ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = MigrationActor });
                var scalar = await ins.ExecuteScalarAsync(ct)
                    ?? throw new InvalidOperationException($"event insert for year {year} returned no id");
                eventId = (long)scalar;
            }

            // Bulk-load the year's locations in one insert with unnest arrays.
            // The array element order is (seq asc, id asc) so row_number() over
            // (order by seq, id) is consistent with the source order.
            var sortedTimes = rows.Select(r => (long)r.TimeMs).ToArray();
            var lats = rows.Select(r => r.Lat).ToArray();
            var lngs = rows.Select(r => r.Lng).ToArray();
            await using (var bulk = new NpgsqlCommand(@"
insert into location (event_id, beacon_id, seq, recorded_at, received_at, lat, lng, published)
select $1, $2, row_number() over (), to_timestamp(t / 1000.0), to_timestamp(t / 1000.0), lat, lng, true
from unnest($3::bigint[], $4::float8[], $5::float8[]) with ordinality as p (t, lat, lng, ord)
order by ord;", target, tx))
            {
                bulk.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
                bulk.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = legacyBeaconId });
                bulk.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint, Value = sortedTimes });
                bulk.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Double, Value = lats });
                bulk.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Double, Value = lngs });
                await bulk.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            yearToEventId[year] = eventId;
            step.Inserted++;
            step.Notes.Add($"year {year}: inserted {count} locations");
        }
        return yearToEventId;
    }

    // ----- Step 6: 2025 route object + route row + event.route_id -----

    private async Task Step6Route2025Async(NpgsqlConnection legacy, NpgsqlConnection target,
        IReadOnlyDictionary<int, long> yearToEventId, MigrationSummary summary, CancellationToken ct)
    {
        var step = summary.AddStep("route_2025");
        if (!yearToEventId.TryGetValue(2025, out var eventId2025))
        {
            // Fall back to reading the event row directly (existing rows path).
            var maybe = await ScalarLongAsync(target, "select id from event where year = 2025;");
            if (maybe is not long id)
            {
                step.Considered = 0;
                step.Notes.Add("no 2025 event; skipped");
                return;
            }
            eventId2025 = id;
        }
        step.Considered = 1;

        var points = await Read2025FlightPointsAsync(legacy, ct);
        if (points.Count == 0)
        {
            step.Notes.Add("no 2025 flight_history rows; skipped");
            return;
        }
        var routeObj = new RouteObject
        {
            SchemaVersion = 1,
            Name = RouteName2025,
            Points = points,
        };
        var bytes = CanonicalJson.SerializeToUtf8Bytes(routeObj);
        var sha = CanonicalJson.Sha256Hex(bytes);
        var key = $"routes/{sha}.json";

        var existingRouteId = await ScalarLongAsync(target,
            "select id from route where s3_key = $1;", (NpgsqlDbType.Text, key));
        long routeId;
        if (existingRouteId is long r)
        {
            routeId = r;
            step.Skipped++;
        }
        else
        {
            if (_options.DryRun)
            {
                step.Notes.Add($"dry run: would PUT {key} and insert route row");
                return;
            }
            await _target.PutObjectAsync(key, bytes, RouteJsonContentType, ImmutableCacheControl, tag: null, ct);
            var url = _options.CdnBaseUrl.TrimEnd('/') + "/" + key;
            routeId = await ScalarLongAsync(target, @"
insert into route (name, s3_key, url, sha256, point_count, uploaded_by)
values ($1, $2, $3, $4, $5, $6)
on conflict (s3_key) do nothing
returning id;",
                (NpgsqlDbType.Text, RouteName2025),
                (NpgsqlDbType.Text, key),
                (NpgsqlDbType.Text, url),
                (NpgsqlDbType.Char, sha),
                (NpgsqlDbType.Integer, points.Count),
                (NpgsqlDbType.Text, MigrationActor))
                ?? await ScalarLongAsync(target, "select id from route where s3_key = $1;", (NpgsqlDbType.Text, key))
                ?? throw new InvalidOperationException("route lookup failed after insert");
            step.Inserted++;
        }

        // Only touch the event row if it does not already reference the route.
        var currentRouteId = await ScalarLongAsync(target,
            "select route_id from event where id = $1;", (NpgsqlDbType.Bigint, eventId2025));
        if (currentRouteId is long current && current == routeId)
        {
            step.Notes.Add("event.route_id already set");
            return;
        }
        if (_options.DryRun)
        {
            step.Notes.Add("dry run: would link event.route_id");
            return;
        }
        await ExecAsync(target,
            "update event set route_id = $1, updated_at = now() where id = $2;",
            (NpgsqlDbType.Bigint, routeId), (NpgsqlDbType.Bigint, eventId2025));
    }

    // ----- Step 7: event_updates → event_message (id preserved) -----

    private async Task Step7EventMessagesAsync(NpgsqlConnection legacy, NpgsqlConnection target, MigrationSummary summary, CancellationToken ct)
    {
        var step = summary.AddStep("event_messages");
        var legacyRows = await ReadLegacyEventUpdatesAsync(legacy, ct);
        step.Considered = legacyRows.Count;
        foreach (var row in legacyRows)
        {
            var routeTs = row.EventTimeUtc ?? row.CreatedAtUtc;
            var denverYear = TimeZoneInfo.ConvertTimeFromUtc(routeTs, _denverTz).Year;
            var eventId = await ScalarLongAsync(target,
                "select id from event where year = $1;", (NpgsqlDbType.Integer, denverYear));
            if (eventId is null)
            {
                step.Failed++;
                step.Notes.Add($"event_update id={row.Id}: no event for year {denverYear}, skipped");
                continue;
            }
            var exists = await ScalarLongAsync(target,
                "select id from event_message where id = $1;", (NpgsqlDbType.Bigint, (long)row.Id));
            if (exists is not null)
            {
                step.Skipped++;
                continue;
            }
            if (_options.DryRun)
            {
                step.Notes.Add($"dry run: would insert event_message id={row.Id}");
                continue;
            }
            await ExecAsync(target, @"
insert into event_message (id, event_id, body, event_time, created_by, created_at, updated_at)
overriding system value
values ($1, $2, $3, $4, $5, $6, $6)
on conflict on constraint event_message_pkey do nothing;",
                (NpgsqlDbType.Bigint, (long)row.Id),
                (NpgsqlDbType.Bigint, eventId.Value),
                (NpgsqlDbType.Text, row.Message),
                row.EventTimeUtc is null
                    ? (NpgsqlDbType.TimestampTz, (object)DBNull.Value)
                    : (NpgsqlDbType.TimestampTz, (object)row.EventTimeUtc.Value),
                (NpgsqlDbType.Text, MigrationActor),
                (NpgsqlDbType.TimestampTz, (object)row.CreatedAtUtc));
            step.Inserted++;
        }
        if (!_options.DryRun) await SetvalAsync(target, "event_message", "id");
    }

    // ----- Step 8: funds → event.funds_percent -----

    private async Task Step8FundsAsync(NpgsqlConnection legacy, NpgsqlConnection target, MigrationSummary summary, CancellationToken ct)
    {
        var step = summary.AddStep("funds_percent");
        var rows = await ReadLegacyFundsAsync(legacy, ct);
        step.Considered = rows.Count;

        var perYear = new Dictionary<int, (long CreatedMs, int Percent)>();
        foreach (var f in rows)
        {
            if (!long.TryParse(f.Created?.Trim() ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
            {
                step.Failed++;
                step.Notes.Add($"funds row: unparseable created '{f.Created}', skipped");
                continue;
            }
            var pctText = f.Percent?.Trim() ?? "";
            if (!System.Text.RegularExpressions.Regex.IsMatch(pctText, "^-?\\d+$")
                || !int.TryParse(pctText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pct))
            {
                step.Failed++;
                step.Notes.Add($"funds row: unparseable percent '{f.Percent}', skipped");
                continue;
            }
            pct = Math.Clamp(pct, 0, 100);
            var year = TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime, _denverTz).Year;
            if (!perYear.TryGetValue(year, out var cur) || ms > cur.CreatedMs)
                perYear[year] = (ms, pct);
        }

        foreach (var kv in perYear)
        {
            var year = kv.Key;
            var pct = kv.Value.Percent;
            var eventId = await ScalarLongAsync(target, "select id, funds_percent from event where year = $1;",
                (NpgsqlDbType.Integer, year));
            if (eventId is null)
            {
                step.Failed++;
                step.Notes.Add($"funds year={year}: no event, skipped");
                continue;
            }
            var current = await ScalarIntAsync(target, "select funds_percent from event where id = $1;",
                (NpgsqlDbType.Bigint, eventId.Value));
            if (current is int c && c == pct)
            {
                step.Skipped++;
                continue;
            }
            if (_options.DryRun)
            {
                step.Notes.Add($"dry run: would set year={year} funds_percent={pct}");
                continue;
            }
            await ExecAsync(target,
                "update event set funds_percent = $1, updated_at = now() where id = $2;",
                (NpgsqlDbType.Integer, pct), (NpgsqlDbType.Bigint, eventId.Value));
            step.Inserted++;
        }
    }

    // ----- Step 9: messages → contact_message (natural-key idempotent) -----

    private async Task Step9ContactMessagesAsync(NpgsqlConnection legacy, NpgsqlConnection target, MigrationSummary summary, CancellationToken ct)
    {
        var step = summary.AddStep("contact_messages");
        var legacyRows = await ReadLegacyMessagesAsync(legacy, ct);
        step.Considered = legacyRows.Count;
        var parsed = new List<(string Name, string Email, string Body, DateTime CreatedAtUtc)>();
        foreach (var row in legacyRows)
        {
            if (!long.TryParse(row.Created?.Trim() ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
            {
                step.Failed++;
                step.Notes.Add($"contact message: unparseable created '{row.Created}', skipped");
                continue;
            }
            var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
            parsed.Add((row.Name, row.Email, row.Message, createdAt));
        }
        parsed.Sort((a, b) => a.CreatedAtUtc.CompareTo(b.CreatedAtUtc));

        foreach (var row in parsed)
        {
            var exists = await ScalarLongAsync(target, @"
select id from contact_message where name = $1 and email = $2 and body = $3 and created_at = $4;",
                (NpgsqlDbType.Text, row.Name),
                (NpgsqlDbType.Text, row.Email),
                (NpgsqlDbType.Text, row.Body),
                (NpgsqlDbType.TimestampTz, (object)row.CreatedAtUtc));
            if (exists is not null)
            {
                step.Skipped++;
                continue;
            }
            if (_options.DryRun)
            {
                step.Notes.Add($"dry run: would insert contact_message name='{row.Name}'");
                continue;
            }
            await ExecAsync(target, @"
insert into contact_message (name, email, body, client_ip, created_at)
values ($1, $2, $3, '', $4);",
                (NpgsqlDbType.Text, row.Name),
                (NpgsqlDbType.Text, row.Email),
                (NpgsqlDbType.Text, row.Body),
                (NpgsqlDbType.TimestampTz, (object)row.CreatedAtUtc));
            step.Inserted++;
        }
    }

    // ----- Verification queries -----

    private async Task PrintVerificationQueriesAsync(NpgsqlConnection target, CancellationToken ct)
    {
        _output.WriteLine();
        _output.WriteLine("=== verification queries ===");
        var queries = new (string Label, string Sql)[]
        {
            ("row counts", @"
select 'event' as t, count(*) c from event
union all select 'location',        count(*) from location
union all select 'sponsor',         count(*) from sponsor
union all select 'sponsor_year',    count(*) from sponsor_year
union all select 'event_message',   count(*) from event_message
union all select 'contact_message', count(*) from contact_message
union all select 'route',           count(*) from route
union all select 'media_asset',     count(*) from media_asset
union all select 'beacon',          count(*) from beacon
order by t;"),
            ("event details", @"
select year, name, status_id, funds_percent, route_id,
       (select count(*) from location l where l.event_id = e.id) as loc_count
from event e order by year;"),
            ("sponsor logos", @"
select id, name, logo_media_id from sponsor order by id;"),
            ("app_setting sponsor_linger_ms_per_dollar", @"
select key, value, updated_by, updated_at from app_setting where key = 'sponsor_linger_ms_per_dollar';"),
        };
        foreach (var (label, sql) in queries)
        {
            _output.WriteLine();
            _output.WriteLine($"-- {label}");
            _output.WriteLine(sql.Trim());
            await using var cmd = new NpgsqlCommand(sql, target);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var cols = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
            _output.WriteLine("  " + string.Join(" | ", cols));
            while (await reader.ReadAsync(ct))
            {
                var values = new string[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                    values[i] = reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "";
                _output.WriteLine("  " + string.Join(" | ", values));
            }
        }
    }

    // ----- Legacy readers -----

    private static async Task<List<LegacySponsor>> ReadLegacySponsorsAsync(NpgsqlConnection legacy, CancellationToken ct)
    {
        var list = new List<LegacySponsor>();
        await using var cmd = new NpgsqlCommand(@"
select id, name, contact_person, email, phone, address,
       website_url, fb_url, ig_url, logo_s3_key, logo_small_s3_key,
       created_at, updated_at
from sponsors order by id;", legacy);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new LegacySponsor(
                Id: reader.GetInt32(0),
                Name: reader.GetString(1),
                ContactPerson: reader.IsDBNull(2) ? null : reader.GetString(2),
                Email: reader.IsDBNull(3) ? null : reader.GetString(3),
                Phone: reader.IsDBNull(4) ? null : reader.GetString(4),
                Address: reader.IsDBNull(5) ? null : reader.GetString(5),
                WebsiteUrl: reader.IsDBNull(6) ? null : reader.GetString(6),
                FbUrl: reader.IsDBNull(7) ? null : reader.GetString(7),
                IgUrl: reader.IsDBNull(8) ? null : reader.GetString(8),
                LogoS3Key: reader.IsDBNull(9) ? null : reader.GetString(9),
                LogoSmallS3Key: reader.IsDBNull(10) ? null : reader.GetString(10),
                CreatedAtUtc: reader.IsDBNull(11) ? null : ReadNaiveAsUtc(reader, 11),
                UpdatedAtUtc: reader.IsDBNull(12) ? null : ReadNaiveAsUtc(reader, 12)));
        }
        return list;
    }

    private static async Task<List<LegacySponsorYear>> ReadLegacySponsorYearsAsync(NpgsqlConnection legacy, CancellationToken ct)
    {
        var list = new List<LegacySponsorYear>();
        await using var cmd = new NpgsqlCommand(@"
select id, sponsor_id, event_year, amount_donated, active, can_advertise, anonymous, registered_at
from sponsor_years order by id;", legacy);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new LegacySponsorYear(
                Id: reader.GetInt32(0),
                SponsorId: reader.IsDBNull(1) ? null : reader.GetInt32(1),
                EventYear: reader.GetInt32(2),
                AmountDonated: reader.IsDBNull(3) ? null : reader.GetDecimal(3),
                Active: reader.IsDBNull(4) ? null : reader.GetBoolean(4),
                CanAdvertise: reader.IsDBNull(5) ? null : reader.GetBoolean(5),
                Anonymous: reader.IsDBNull(6) ? null : reader.GetBoolean(6),
                RegisteredAtUtc: reader.IsDBNull(7) ? null : ReadNaiveAsUtc(reader, 7)));
        }
        return list;
    }

    private static async Task<List<LegacyFlightPoint>> ReadFlightHistoryYearAsync(NpgsqlConnection legacy, int year, CancellationToken ct)
    {
        var list = new List<LegacyFlightPoint>();
        await using var cmd = new NpgsqlCommand(
            "select id, seq, time, lat, lng from flight_history where year = $1 order by seq, id;", legacy);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = year });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new LegacyFlightPoint(
                Id: reader.GetInt32(0),
                Seq: reader.GetInt32(1),
                TimeMs: reader.GetInt64(2),
                Lat: (double)reader.GetDecimal(3),
                Lng: (double)reader.GetDecimal(4)));
        }
        return list;
    }

    private static async Task<List<RoutePoint>> Read2025FlightPointsAsync(NpgsqlConnection legacy, CancellationToken ct)
    {
        var list = new List<RoutePoint>();
        await using var cmd = new NpgsqlCommand(
            "select time, lat, lng from flight_history where year = 2025 order by seq, id;", legacy);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new RoutePoint
            {
                Lat = (double)reader.GetDecimal(1),
                Lng = (double)reader.GetDecimal(2),
                RecordedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
            });
        }
        return list;
    }

    private static async Task<List<LegacyEventUpdate>> ReadLegacyEventUpdatesAsync(NpgsqlConnection legacy, CancellationToken ct)
    {
        var list = new List<LegacyEventUpdate>();
        await using var cmd = new NpgsqlCommand(
            "select id, message, time, created_at from event_updates order by id;", legacy);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new LegacyEventUpdate(
                Id: reader.GetInt32(0),
                Message: reader.GetString(1),
                EventTimeUtc: reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTime>(2).ToUniversalTime(),
                CreatedAtUtc: reader.GetFieldValue<DateTime>(3).ToUniversalTime()));
        }
        return list;
    }

    private static async Task<List<LegacyFunds>> ReadLegacyFundsAsync(NpgsqlConnection legacy, CancellationToken ct)
    {
        var list = new List<LegacyFunds>();
        await using var cmd = new NpgsqlCommand("select percent::text, created from funds;", legacy);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new LegacyFunds(
                Percent: reader.IsDBNull(0) ? null : reader.GetString(0),
                Created: reader.IsDBNull(1) ? null : reader.GetString(1)));
        }
        return list;
    }

    private static async Task<List<LegacyMessage>> ReadLegacyMessagesAsync(NpgsqlConnection legacy, CancellationToken ct)
    {
        var list = new List<LegacyMessage>();
        await using var cmd = new NpgsqlCommand("select name, email, message, created from messages;", legacy);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new LegacyMessage(
                Name: reader.GetString(0),
                Email: reader.GetString(1),
                Message: reader.GetString(2),
                Created: reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        return list;
    }

    // ----- Small helpers -----

    private static DateTime ReadNaiveAsUtc(NpgsqlDataReader reader, int ordinal)
    {
        var raw = reader.GetFieldValue<DateTime>(ordinal);
        return DateTime.SpecifyKind(raw, DateTimeKind.Utc);
    }

    private static async Task<long?> ScalarLongAsync(NpgsqlConnection conn, string sql, params (NpgsqlDbType Type, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var p in parameters)
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = p.Type, Value = p.Value ?? DBNull.Value });
        var r = await cmd.ExecuteScalarAsync();
        return r switch
        {
            null or DBNull => null,
            long l => l,
            int i => i,
            short s => s,
            _ => Convert.ToInt64(r, CultureInfo.InvariantCulture),
        };
    }

    private static async Task<int?> ScalarIntAsync(NpgsqlConnection conn, string sql, params (NpgsqlDbType Type, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var p in parameters)
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = p.Type, Value = p.Value ?? DBNull.Value });
        var r = await cmd.ExecuteScalarAsync();
        return r switch
        {
            null or DBNull => null,
            int i => i,
            long l => (int)l,
            short s => s,
            _ => Convert.ToInt32(r, CultureInfo.InvariantCulture),
        };
    }

    private static async Task<string?> ScalarStringAsync(NpgsqlConnection conn, string sql, params (NpgsqlDbType Type, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var p in parameters)
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = p.Type, Value = p.Value ?? DBNull.Value });
        var r = await cmd.ExecuteScalarAsync();
        if (r is null || r is DBNull) return null;
        return r as string ?? r.ToString();
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, params (NpgsqlDbType Type, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var p in parameters)
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = p.Type, Value = p.Value ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task SetvalAsync(NpgsqlConnection target, string table, string column)
    {
        await using var cmd = new NpgsqlCommand(
            $"select setval(pg_get_serial_sequence('{table}', '{column}'), coalesce((select max({column}) from {table}), 1));",
            target);
        await cmd.ExecuteScalarAsync();
    }

    private static (NpgsqlDbType Type, object Value) Nullable(NpgsqlDbType type, string? value)
        => (type, (object?)value ?? DBNull.Value);
}

// ----- Legacy row DTOs -----

internal sealed record LegacySponsor(
    int Id, string Name,
    string? ContactPerson, string? Email, string? Phone, string? Address,
    string? WebsiteUrl, string? FbUrl, string? IgUrl,
    string? LogoS3Key, string? LogoSmallS3Key,
    DateTime? CreatedAtUtc, DateTime? UpdatedAtUtc);

internal sealed record LegacySponsorYear(
    int Id, int? SponsorId, int EventYear,
    decimal? AmountDonated, bool? Active, bool? CanAdvertise, bool? Anonymous,
    DateTime? RegisteredAtUtc);

internal sealed record LegacyFlightPoint(int Id, int Seq, long TimeMs, double Lat, double Lng);

internal sealed record LegacyEventUpdate(
    int Id, string Message, DateTime? EventTimeUtc, DateTime CreatedAtUtc);

internal sealed record LegacyFunds(string? Percent, string? Created);

internal sealed record LegacyMessage(string Name, string Email, string Message, string? Created);
