using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Wmsfo.Api.Chores;
using Wmsfo.Api.Contracts;
using Wmsfo.Api.Email;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.IntegrationTests;

// A76 acceptance: an alert email carries the published site logo.
//   - with a published logoMedia, a status email and a message email render
//     email/<sha>.png of the derived tile, the store holds that key, and the
//     site name sits beside the logo
//   - without one, the email renders the bundled logo and the site name
//   - an svg logoMedia keeps the bundled logo and the send succeeds
public sealed class A76EmailLogoTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly string _storeDir = Path.Combine(Path.GetTempPath(), "wmsfo-a76-" + Guid.NewGuid().ToString("N"));
    private A16Host? _host;

    public A76EmailLogoTests(PostgresFixture fixture) { _fixture = fixture; }

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await SnapshotSeed.EnsureAsync(conn);
    }

    public Task DisposeAsync()
    {
        _host?.Dispose();
        try { Directory.Delete(_storeDir, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    private (A16Host Host, AlertSender Alerts, LocalObjectStore Store, EmailTemplates Templates) Build()
    {
        var store = new LocalObjectStore(_storeDir, "http://localhost:5000");
        _host = A16Host.Create(_fixture.ConnectionString, store);
        var templates = EmailTemplates.Load(TestPaths.EmailTemplatesDir, _host.Options.CdnBaseUrl, _host.Options.SiteBaseUrl);
        var resolver = new EmailLogoResolver(_host.Connections, _host.Options, store, templates,
            NullLogger<EmailLogoResolver>.Instance);
        var alerts = new AlertSender(_host.Connections, _host.Options, _host.Sender,
            NullLogger<AlertSender>.Instance, resolver);
        return (_host, alerts, store, templates);
    }

    [Fact]
    public async Task A_published_logo_media_is_the_email_logo_under_email_sha_png()
    {
        var (host, alerts, store, templates) = Build();
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        var png = LogoPng();
        var mediaId = await InsertMediaAsync(conn, store, "logo.png", "image/png", "raster", png);
        await PublishAsync(conn, "North Pole Flight Desk", mediaId);
        var eventId = await InsertEventAsync(conn, "Santa 2027");
        await InsertSubscriberAsync(conn, "logo@example.com");
        var messageId = await InsertMessageAsync(conn, eventId, "Wave at the sleigh!");
        await InsertOutboxAsync(conn, "event.status_changed",
            $"{{\"eventId\":{eventId},\"fromStatusId\":2,\"toStatusId\":3,\"notify\":true}}");
        await InsertOutboxAsync(conn, "event.message_posted",
            $"{{\"eventId\":{eventId},\"messageId\":{messageId}}}");

        await host.Outbox.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, await alerts.RunOnceAsync(CancellationToken.None));

        var key = EmailLogo.KeyFor(EmailLogo.DeriveTile(png, "image/png"));
        Assert.Matches("^email/[0-9a-f]{64}\\.png$", key);
        var url = host.Options.CdnBaseUrl.TrimEnd('/') + "/" + key;

        Assert.Equal(2, host.Sender.Sent.Count);
        foreach (var message in host.Sender.Sent)
        {
            var rendered = templates.Render(message.TemplateName, message.Values);
            Assert.Contains("<img src=\"" + url + "\"", rendered.Html);
            Assert.DoesNotContain(templates.Logo.Url, rendered.Html);
            Assert.Contains("alt=\"North Pole Flight Desk\"", rendered.Html);
            Assert.Contains(">North Pole Flight Desk</td>", rendered.Html);
            Assert.StartsWith("North Pole Flight Desk\n", rendered.Text);
        }

        var head = await store.HeadObjectAsync(key);
        Assert.NotNull(head);
        Assert.Equal("image/png", head!.ContentType);
        Assert.Equal(EmailLogo.ImmutableCacheControl, head.CacheControl);
        var stored = await store.GetObjectAsync(key);
        using var tile = Image.Load<Rgba32>(stored!.Bytes);
        Assert.Equal(192, tile.Width);
        Assert.Equal(192, tile.Height);
    }

    [Fact]
    public async Task Without_logo_media_the_email_carries_the_bundled_logo()
    {
        var (host, alerts, _, templates) = Build();
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        await PublishAsync(conn, "", mediaId: null);
        var eventId = await InsertEventAsync(conn, "Santa 2028");
        await InsertSubscriberAsync(conn, "plain@example.com");
        await InsertOutboxAsync(conn, "event.status_changed",
            $"{{\"eventId\":{eventId},\"fromStatusId\":2,\"toStatusId\":3,\"notify\":true}}");

        await host.Outbox.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, await alerts.RunOnceAsync(CancellationToken.None));

        var message = Assert.Single(host.Sender.Sent);
        var rendered = templates.Render(message.TemplateName, message.Values);
        Assert.Contains("<img src=\"" + templates.Logo.Url + "\"", rendered.Html);
        Assert.Contains("<img src=\"" + templates.Ornaments.Url + "\"", rendered.Html);
        Assert.Contains("<img src=\"" + templates.Lights.Url + "\"", rendered.Html);
        Assert.Contains("alt=\"Santa Tracker\"", rendered.Html);
        Assert.StartsWith("Santa Tracker\n", rendered.Text);
    }

    [Fact]
    public async Task An_svg_logo_media_keeps_the_bundled_logo_and_the_send_succeeds()
    {
        var (host, alerts, store, templates) = Build();
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"><rect width=\"10\" height=\"10\"/></svg>"u8.ToArray();
        var mediaId = await InsertMediaAsync(conn, store, "logo.svg", "image/svg+xml", "svg", svg);
        await PublishAsync(conn, "Svg Site", mediaId);
        var eventId = await InsertEventAsync(conn, "Santa 2029");
        await InsertSubscriberAsync(conn, "svg@example.com");
        await InsertOutboxAsync(conn, "event.status_changed",
            $"{{\"eventId\":{eventId},\"fromStatusId\":2,\"toStatusId\":3,\"notify\":true}}");

        await host.Outbox.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, await alerts.RunOnceAsync(CancellationToken.None));

        var message = Assert.Single(host.Sender.Sent);
        var rendered = templates.Render(message.TemplateName, message.Values);
        Assert.Contains("<img src=\"" + templates.Logo.Url + "\"", rendered.Html);
        Assert.Contains("alt=\"Svg Site\"", rendered.Html);
    }

    // ---------- helpers ----------

    private static byte[] LogoPng()
    {
        using var image = new Image<Rgba32>(300, 120, new Rgba32(10, 120, 200, 255));
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static async Task<Guid> InsertMediaAsync(
        NpgsqlConnection conn, IObjectStore store, string filename, string contentType, string kind, byte[] bytes)
    {
        var id = Guid.NewGuid();
        var s3Key = $"media/{id:D}/{filename}";
        await store.PutObjectAsync(s3Key, bytes, contentType, EmailLogo.ImmutableCacheControl);
        await using var cmd = new NpgsqlCommand(@"
insert into media_asset (id, filename, content_type, kind, state, s3_key, uploaded_by, alt, title, variants, created_at, confirmed_at)
values ($1, $2, $3, $4, 'ready', $5, 'seed', '', '', '{}'::jsonb, now(), now());", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = filename });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = contentType });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = kind });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = s3Key });
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    // Publishes a content version whose settings carry `siteName` and, when
    // given, `logoMedia` naming `mediaId`.
    private static async Task PublishAsync(NpgsqlConnection conn, string siteName, Guid? mediaId)
    {
        var doc = FixtureData.BuildContentDocument();
        doc.Settings.SiteName = siteName;
        doc.Settings.LogoMedia = mediaId is null
            ? null
            : new JsonObject { ["mediaId"] = mediaId.Value.ToString("D"), ["alt"] = "Logo" };
        var bytes = CanonicalJson.SerializeToUtf8Bytes(doc);
        await using var cmd = new NpgsqlCommand(@"
insert into content_version (document, sha256, media_ids, label, published_by)
values ($1::jsonb, $2, '{}'::uuid[], 'a76', 'seed');", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = System.Text.Encoding.UTF8.GetString(bytes) });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Char, Value = CanonicalJson.Sha256Hex(bytes) });
        await cmd.ExecuteNonQueryAsync();
    }

    // `name` is "Santa <year>"; the year is the event's year.
    private static async Task<long> InsertEventAsync(NpgsqlConnection conn, string name)
    {
        await using var cmd = new NpgsqlCommand(@"
insert into event (year, name, status_id, is_current, created_by, updated_at)
values ($2, $1, 1, false, 'seed', now()) returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = name });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = int.Parse(name[^4..], System.Globalization.CultureInfo.InvariantCulture) });
        return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<long> InsertMessageAsync(NpgsqlConnection conn, long eventId, string body)
    {
        await using var cmd = new NpgsqlCommand(
            "insert into event_message (event_id, body, created_by) values ($1, $2, 'seed') returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = body });
        return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    // A verified email subscriber; earlier tests' subscribers are removed so
    // each test's fan-out reaches only its own address.
    private static async Task InsertSubscriberAsync(NpgsqlConnection conn, string address)
    {
        await using (var clear = new NpgsqlCommand("delete from alert_delivery; delete from subscriber;", conn))
            await clear.ExecuteNonQueryAsync();
        long personId;
        await using (var cmd = new NpgsqlCommand(
            "insert into person (cognito_sub, email) values (gen_random_uuid(), $1) returning id;", conn))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = address });
            personId = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
        }
        await using (var cmd = new NpgsqlCommand(@"
insert into subscriber (person_id, channel, address, unsubscribe_token, verified_at)
values ($1, 'email', $2, $3, now());", conn))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = address });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = "wsu_" + Guid.NewGuid().ToString("N") });
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task InsertOutboxAsync(NpgsqlConnection conn, string topic, string payload)
    {
        await using var cmd = new NpgsqlCommand(
            "insert into outbox (topic, payload) values ($1, $2::jsonb);", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = topic });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = payload });
        await cmd.ExecuteNonQueryAsync();
    }
}
