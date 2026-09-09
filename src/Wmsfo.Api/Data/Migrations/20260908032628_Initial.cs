using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Wmsfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_setting",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    updated_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("app_setting_pkey", x => x.key);
                },
                comment: "The admin knobs, one row per key. Only PUT /admin/settings/{key} changes a value. A missing row means the compiled default.");

            migrationBuilder.CreateTable(
                name: "beacon",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    role = table.Column<string>(type: "text", nullable: false, comment: "beacon or admin. Immutable after creation."),
                    key_hash = table.Column<byte[]>(type: "bytea", nullable: false, comment: "sha256 of the plaintext key, 32 bytes. The plaintext is never stored."),
                    key_prefix = table.Column<string>(type: "text", nullable: false, comment: "First 12 characters of the key, for display."),
                    key_version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1, comment: "Incremented by rotate. Carried in the hub identity as \"<id>:<key_version>\"; the message path rejects a stale version."),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false, comment: "The one beacon whose updates fan out. At most one true (beacon_one_active). Zero is allowed."),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true, comment: "Set once by revoke. A revoked beacon never authenticates again."),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true, comment: "Any authenticated contact: REST call, hub authorize, hub message."),
                    last_location_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true, comment: "Last stored location, published or not."),
                    last_heartbeat_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true, comment: "Last stored heartbeat."),
                    stale_since = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true, comment: "Set by the stale-beacon chore; cleared by a heartbeat or a stored location."),
                    telemetry = table.Column<JsonDocument>(type: "jsonb", nullable: true, comment: "The last heartbeat body, stored as received."),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("beacon_pkey", x => x.id);
                    table.UniqueConstraint("beacon_key_hash_key", x => x.key_hash);
                    table.CheckConstraint("beacon_role_check", "role in ('beacon', 'admin')");
                },
                comment: "A trusted sender. Rows are never deleted; revoke is permanent.");

            migrationBuilder.CreateTable(
                name: "contact_message",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    email = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    client_ip = table.Column<string>(type: "text", nullable: false, comment: "Client IP per the trusted-proxy rule; empty string for migrated rows."),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("contact_message_pkey", x => x.id);
                },
                comment: "Contact form submissions. No automatic retention; admins delete.");

            migrationBuilder.CreateTable(
                name: "content_version",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    document = table.Column<JsonDocument>(type: "jsonb", nullable: false, comment: "ContentDocument (contracts 1.3a) exactly as the snapshot embeds it: hidden rows omitted, canonical order."),
                    sha256 = table.Column<string>(type: "char(64)", nullable: false, comment: "SHA-256 of the canonical bytes of document; POST /admin/content/publish answers 409 content_unchanged when the working set hashes to the newest row."),
                    media_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false, defaultValueSql: "'{}'", comment: "Every media_asset id the document references (MediaRef, media icons, background media). The orphan chore treats these as referenced while the row exists."),
                    label = table.Column<string>(type: "text", nullable: true),
                    published_by = table.Column<string>(type: "text", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("content_version_pkey", x => x.id);
                },
                comment: "Published content documents. The row with the greatest id is what the snapshot carries; the newest 50 are kept, older rows are deleted in the publish transaction. Restore copies a row back into the working set and publishes nothing.");

            migrationBuilder.CreateTable(
                name: "cookie_type",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    icon = table.Column<JsonDocument>(type: "jsonb", nullable: true, comment: "Icon value (contracts 1.3a): {\"source\":\"library\",\"id\":\"cookie\"} or {\"source\":\"media\",\"id\":\"<uuid of a ready svg media_asset>\"}; null until chosen."),
                    sort = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("cookie_type_pkey", x => x.id);
                },
                comment: "Admin-managed. Locked (409 event_live) while any event has status 3. No delete; active = false removes a type from the snapshot.");

            migrationBuilder.CreateTable(
                name: "event_status",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("event_status_pkey", x => x.id);
                    table.UniqueConstraint("event_status_name_key", x => x.name);
                },
                comment: "Fixed lookup. Never edited through the API. Ids are the statusId values on every wire.");

            migrationBuilder.CreateTable(
                name: "icon_library_state",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false),
                    library_sha256 = table.Column<string>(type: "char(64)", nullable: true),
                    written_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("icon_library_state_pkey", x => x.id);
                    table.CheckConstraint("icon_library_state_id_check", "id = 1");
                },
                comment: "Single row (id = 1). Hash of the icon library the fleet last wrote to the bucket. The boot migrator compares it with the compiled library and writes the icons when they differ (section 8.16).");

            migrationBuilder.CreateTable(
                name: "live_state",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false),
                    last_write_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    last_write_seq = table.Column<long>(type: "bigint", nullable: true),
                    last_write_version = table.Column<long>(type: "bigint", nullable: true),
                    last_write_error = table.Column<string>(type: "text", nullable: true),
                    last_write_node = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("live_state_pkey", x => x.id);
                    table.CheckConstraint("live_state_id_check", "id = 1");
                },
                comment: "Single row (id = 1). Fleet-wide outcome of the last live-object write. Returned by GET /admin/live.");

            migrationBuilder.CreateTable(
                name: "media_asset",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, comment: "Minted by the API (UUID v4) when the upload ticket is issued; it is the key segment media/{id}/."),
                    filename = table.Column<string>(type: "text", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false, comment: "raster: png, jpeg, webp (variants derived). gif: stored as is. svg: validated, usable as an icon."),
                    state = table.Column<string>(type: "text", nullable: false),
                    s3_key = table.Column<string>(type: "text", nullable: false, comment: "media/{id}/{filename}; the original bytes."),
                    size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    width = table.Column<int>(type: "integer", nullable: true),
                    height = table.Column<int>(type: "integer", nullable: true),
                    sha256 = table.Column<string>(type: "char(64)", nullable: true),
                    variants = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'{}'", comment: "{ \"480\": \"media/{id}/w480.webp\", \"960\": ..., \"1600\": ... }: the WebP width variants that exist (raster only, widths below the source width)."),
                    alt = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    title = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    uploaded_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    unreferenced_since = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    orphaned_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("media_asset_pkey", x => x.id);
                    table.UniqueConstraint("media_asset_s3_key_key", x => x.s3_key);
                    table.CheckConstraint("media_asset_kind_check", "kind in ('raster', 'svg', 'gif')");
                    table.CheckConstraint("media_asset_state_check", "state in ('pending', 'ready', 'orphaned')");
                },
                comment: "The media library. pending: ticket issued, bytes may or may not be in the bucket. ready: confirmed. orphaned: unreferenced for 30 days, objects tagged for lifecycle expiry; the row is deleted 8 days later.");

            migrationBuilder.CreateTable(
                name: "outbox",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    topic = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true, comment: "Set by the claim query; a claim older than 2 minutes is reclaimable."),
                    published_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0, comment: "Rows with 5 attempts stay unpublished and are visible in the logs."),
                    last_error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("outbox_pkey", x => x.id);
                },
                comment: "Work for the leader chores, written in the same transaction as the change it announces. Topics: event.status_changed, event.message_posted, subscription.verify, contact.received.");

            migrationBuilder.CreateTable(
                name: "page",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    slug = table.Column<string>(type: "text", nullable: false, comment: "One lowercase path segment, ^[a-z0-9]+(-[a-z0-9]+)*$, 1 to 60; never auth, preview, api, admin, assets. Validated by the API."),
                    title = table.Column<string>(type: "text", nullable: false),
                    nav_label = table.Column<string>(type: "text", nullable: true, comment: "Nav entry text; null keeps the page out of the nav. Always null on role pages."),
                    nav_position = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    is_hidden = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    role = table.Column<string>(type: "text", nullable: false, defaultValue: "none"),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("page_pkey", x => x.id);
                    table.UniqueConstraint("page_slug_key", x => x.slug);
                    table.CheckConstraint("page_role_check", "role in ('none', 'no_event', 'planned', 'scheduled', 'live', 'ended', 'cancelled')");
                },
                comment: "Working set. The six role pages are seeded, undeletable, and role-immutable; none pages render at /<slug>. Hidden pages are omitted at publish.");

            migrationBuilder.CreateTable(
                name: "person",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    cognito_sub = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("person_pkey", x => x.id);
                    table.UniqueConstraint("person_cognito_sub_key", x => x.cognito_sub);
                },
                comment: "A registered person. Upserted on every authenticated request from the ID token (sub, email).");

            migrationBuilder.CreateTable(
                name: "preview_token",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("preview_token_pkey", x => x.id);
                    table.UniqueConstraint("preview_token_token_hash_key", x => x.token_hash);
                },
                comment: "wpv_ tokens minted by POST /admin/content/preview-token, sha256 at rest, 15 minutes, reusable until expiry, deleted by the nightly cleanup 24 h after expiry. Database-backed so any node can answer GET /preview/document.");

            migrationBuilder.CreateTable(
                name: "route",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    s3_key = table.Column<string>(type: "text", nullable: false, comment: "routes/{sha256}.json. Content-hashed, so an identical upload resolves to the existing row."),
                    url = table.Column<string>(type: "text", nullable: false, comment: "Absolute CDN URL of s3_key, carried into the snapshot as routeUrl."),
                    sha256 = table.Column<string>(type: "char(64)", nullable: false, comment: "Lowercase hex SHA-256 of the canonical object bytes."),
                    point_count = table.Column<int>(type: "integer", nullable: false),
                    uploaded_by = table.Column<string>(type: "text", nullable: false, comment: "Admin email claim, or 'migration'."),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("route_pkey", x => x.id);
                    table.UniqueConstraint("route_s3_key_key", x => x.s3_key);
                },
                comment: "Metadata for a route object on the CDN. The bytes live only in the bucket under s3_key; the row is never re-serialized from the database.");

            migrationBuilder.CreateTable(
                name: "site_setting_draft",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false),
                    data = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'{}'"),
                    updated_by = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("site_setting_draft_pkey", x => x.id);
                    table.CheckConstraint("site_setting_draft_id_check", "id = 1");
                },
                comment: "Single row (id = 1). The site settings working set (contracts 1.3a SiteSettings), draft-validated on PUT, publish-validated on publish. Seeded from the starter content on first boot.");

            migrationBuilder.CreateTable(
                name: "snapshot",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false, comment: "Increments on every rebuild, including rebuilds that produce identical bytes. Nodes watch it on the reconcile tick."),
                    url = table.Column<string>(type: "text", nullable: false),
                    s3_key = table.Column<string>(type: "text", nullable: false),
                    built_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("snapshot_pkey", x => x.id);
                    table.CheckConstraint("snapshot_id_check", "id = 1");
                },
                comment: "Single row (id = 1). The current snapshot object. Rewritten in the same transaction as every snapshot-affecting write. Inserted by the first boot of an environment; absent means the environment has never finished booting.");

            migrationBuilder.CreateTable(
                name: "beacon_enrollment_token",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    beacon_id = table.Column<long>(type: "bigint", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false, comment: "sha256 of the wet_ token."),
                    key_ciphertext = table.Column<byte[]>(type: "bytea", nullable: true, comment: "AES-256-GCM of the plaintext beacon key (12-byte nonce prepended, 16-byte tag appended). Nulled when consumed."),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("beacon_enrollment_token_pkey", x => x.id);
                    table.UniqueConstraint("beacon_enrollment_token_token_hash_key", x => x.token_hash);
                    table.ForeignKey(
                        name: "beacon_enrollment_token_beacon_id_fkey",
                        column: x => x.beacon_id,
                        principalTable: "beacon",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "One-time tokens behind the enrollment QR code. 15-minute lifetime, single use.");

            migrationBuilder.CreateTable(
                name: "beacon_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    beacon_id = table.Column<long>(type: "bigint", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    app_version = table.Column<string>(type: "text", nullable: true),
                    size_bytes = table.Column<int>(type: "integer", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("beacon_log_pkey", x => x.id);
                    table.ForeignKey(
                        name: "beacon_log_beacon_id_fkey",
                        column: x => x.beacon_id,
                        principalTable: "beacon",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "Red-Nose debug log uploads (admin-role beacons). Up to 2 MB each. 30-day retention.");

            migrationBuilder.CreateTable(
                name: "sponsor",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    contact_person = table.Column<string>(type: "text", nullable: true),
                    email = table.Column<string>(type: "text", nullable: true),
                    phone = table.Column<string>(type: "text", nullable: true),
                    address = table.Column<string>(type: "text", nullable: true),
                    website_url = table.Column<string>(type: "text", nullable: true),
                    fb_url = table.Column<string>(type: "text", nullable: true),
                    ig_url = table.Column<string>(type: "text", nullable: true),
                    logo_media_id = table.Column<Guid>(type: "uuid", nullable: true, comment: "A ready media_asset chosen from the library; null when no logo. The API refuses to delete a referenced asset (409 media_in_use)."),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("sponsor_pkey", x => x.id);
                    table.ForeignKey(
                        name: "sponsor_logo_media_id_fkey",
                        column: x => x.logo_media_id,
                        principalTable: "media_asset",
                        principalColumn: "id");
                },
                comment: "Sponsor master data. contact_person, email, phone, and address are admin-only and never reach the snapshot.");

            migrationBuilder.CreateTable(
                name: "section",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    page_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false, comment: "Compacted to 0, 1, 2, ... within the page after every reorder, move, insert, and delete."),
                    is_hidden = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    data = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'{}'"),
                    presentation = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'{}'"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("section_pkey", x => x.id);
                    table.ForeignKey(
                        name: "section_page_id_fkey",
                        column: x => x.page_id,
                        principalTable: "page",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "Working set. kind names a registry entry (contracts 1.3a); data is validated against the kind schema, presentation against the shared presentation schema, at the draft level on write and at the publish level on publish.");

            migrationBuilder.CreateTable(
                name: "subscriber",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    person_id = table.Column<long>(type: "bigint", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: false),
                    address = table.Column<string>(type: "text", nullable: false, comment: "Stored trimmed and lowercased."),
                    verify_token_hash = table.Column<byte[]>(type: "bytea", nullable: true, comment: "sha256 of the last minted wsv_ token. Kept after verification until the next mint so the verify page is idempotent."),
                    verify_expires_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    unsubscribe_token = table.Column<string>(type: "text", nullable: false, comment: "Plaintext wsu_ token, stable for the life of the row."),
                    unsubscribed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("subscriber_pkey", x => x.id);
                    table.UniqueConstraint("subscriber_channel_address_key", x => new { x.channel, x.address });
                    table.UniqueConstraint("subscriber_unsubscribe_token_key", x => x.unsubscribe_token);
                    table.CheckConstraint("subscriber_channel_check", "channel in ('email', 'sms')");
                    table.ForeignKey(
                        name: "subscriber_person_id_fkey",
                        column: x => x.person_id,
                        principalTable: "person",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "Alert subscriptions. channel is email in v1; sms is reserved and rejected by the API.");

            migrationBuilder.CreateTable(
                name: "event",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    year = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    status_id = table.Column<short>(type: "smallint", nullable: false, comment: "event_status.id. At most one row has 3 (event_one_live)."),
                    is_current = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false, comment: "The event the public site shows. Set only by POST /admin/events/{id}/current. At most one true (event_one_current)."),
                    scheduled_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true, comment: "Admin-entered. Required while status_id = 2."),
                    went_live_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true, comment: "Stamped now() on every entry into status 3; admin-patchable."),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true, comment: "Stamped now() on every entry into status 4; admin-patchable."),
                    funds_percent = table.Column<int>(type: "integer", nullable: false, defaultValue: 0, comment: "Cheer meter, 0 to 100."),
                    route_id = table.Column<long>(type: "bigint", nullable: true, comment: "Route shown for this event; null when unlinked."),
                    final_cookie_tally = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    next_seq = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L, comment: "Next location.seq for this event. Read and incremented under the row lock in the location transaction, so seq order is commit order."),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("event_pkey", x => x.id);
                    table.UniqueConstraint("event_year_key", x => x.year);
                    table.CheckConstraint("event_funds_percent_check", "funds_percent between 0 and 100");
                    table.ForeignKey(
                        name: "event_route_id_fkey",
                        column: x => x.route_id,
                        principalTable: "route",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "event_status_id_fkey",
                        column: x => x.status_id,
                        principalTable: "event_status",
                        principalColumn: "id");
                },
                comment: "One row per flyover. Status changes only when an admin changes them.");

            migrationBuilder.CreateTable(
                name: "sponsor_year",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    sponsor_id = table.Column<long>(type: "bigint", nullable: false),
                    event_year = table.Column<int>(type: "integer", nullable: false),
                    amount_donated = table.Column<decimal>(type: "numeric(12,2)", nullable: true, comment: "Admin-only. Drives lingerMs; never appears in a public object."),
                    active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    can_advertise = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    anonymous = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("sponsor_year_pkey", x => x.id);
                    table.UniqueConstraint("sponsor_year_sponsor_id_event_year_key", x => new { x.sponsor_id, x.event_year });
                    table.CheckConstraint("sponsor_year_amount_donated_check", "amount_donated >= 0");
                    table.ForeignKey(
                        name: "sponsor_year_sponsor_id_fkey",
                        column: x => x.sponsor_id,
                        principalTable: "sponsor",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "A sponsor's participation in one year. Upserted on (sponsor_id, event_year).");

            migrationBuilder.CreateTable(
                name: "section_item",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    section_id = table.Column<long>(type: "bigint", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    is_hidden = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    data = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'{}'"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("section_item_pkey", x => x.id);
                    table.ForeignKey(
                        name: "section_item_section_id_fkey",
                        column: x => x.section_id,
                        principalTable: "section",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "Working set. Repeatable items of a section whose kind has an item schema (media, links, icon_row). position compacted like section.position.");

            migrationBuilder.CreateTable(
                name: "alert_delivery",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    outbox_id = table.Column<long>(type: "bigint", nullable: false),
                    subscriber_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    ses_message_id = table.Column<string>(type: "text", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    last_error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("alert_delivery_pkey", x => x.id);
                    table.UniqueConstraint("alert_delivery_subscriber_id_outbox_id_key", x => new { x.subscriber_id, x.outbox_id });
                    table.ForeignKey(
                        name: "alert_delivery_outbox_id_fkey",
                        column: x => x.outbox_id,
                        principalTable: "outbox",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "alert_delivery_subscriber_id_fkey",
                        column: x => x.subscriber_id,
                        principalTable: "subscriber",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "One email per (subscriber, outbox row). The unique constraint is what makes overlapping leaders safe.");

            migrationBuilder.CreateTable(
                name: "cookie",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    event_id = table.Column<long>(type: "bigint", nullable: false),
                    person_id = table.Column<long>(type: "bigint", nullable: false),
                    cookie_type_id = table.Column<long>(type: "bigint", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true, comment: "Never public. Visible to admins only."),
                    left_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    hidden_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true, comment: "Soft delete for moderation. Hidden cookies leave the tally but still count toward the per-person limit."),
                    hidden_by = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("cookie_pkey", x => x.id);
                    table.ForeignKey(
                        name: "cookie_cookie_type_id_fkey",
                        column: x => x.cookie_type_id,
                        principalTable: "cookie_type",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "cookie_event_id_fkey",
                        column: x => x.event_id,
                        principalTable: "event",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "cookie_person_id_fkey",
                        column: x => x.person_id,
                        principalTable: "person",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "A cookie left by a registered person during a live event. No location.");

            migrationBuilder.CreateTable(
                name: "event_message",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    event_id = table.Column<long>(type: "bigint", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    event_time = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true, comment: "The time the message is about, as entered by the admin. Display only."),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("event_message_pkey", x => x.id);
                    table.ForeignKey(
                        name: "event_message_event_id_fkey",
                        column: x => x.event_id,
                        principalTable: "event",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "Messages shown on the site. The snapshot carries the one with the greatest created_at (ties: greatest id).");

            migrationBuilder.CreateTable(
                name: "event_status_history",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    event_id = table.Column<long>(type: "bigint", nullable: false),
                    from_status_id = table.Column<short>(type: "smallint", nullable: true, comment: "Null when there was no previous status."),
                    to_status_id = table.Column<short>(type: "smallint", nullable: false),
                    changed_by = table.Column<string>(type: "text", nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("event_status_history_pkey", x => x.id);
                    table.ForeignKey(
                        name: "event_status_history_event_id_fkey",
                        column: x => x.event_id,
                        principalTable: "event",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "event_status_history_from_status_id_fkey",
                        column: x => x.from_status_id,
                        principalTable: "event_status",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "event_status_history_to_status_id_fkey",
                        column: x => x.to_status_id,
                        principalTable: "event_status",
                        principalColumn: "id");
                },
                comment: "One row per status change, written in the status change transaction.");

            migrationBuilder.CreateTable(
                name: "location",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    event_id = table.Column<long>(type: "bigint", nullable: false),
                    beacon_id = table.Column<long>(type: "bigint", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false, comment: "Arrival order within the event, from event.next_seq. The only order that exists."),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, comment: "The fix time the beacon sent. Informational; it never decides anything."),
                    received_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()", comment: "When the API stored the row."),
                    lat = table.Column<double>(type: "double precision", nullable: false),
                    lng = table.Column<double>(type: "double precision", nullable: false),
                    speed_mps = table.Column<double>(type: "double precision", nullable: true),
                    altitude_m = table.Column<double>(type: "double precision", nullable: true),
                    heading_deg = table.Column<double>(type: "double precision", nullable: true),
                    accuracy_m = table.Column<double>(type: "double precision", nullable: true),
                    published = table.Column<bool>(type: "boolean", nullable: false, comment: "beacon.is_active at the moment of the insert. Only published rows reach the live object.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("location_pkey", x => x.id);
                    table.UniqueConstraint("location_event_id_seq_key", x => new { x.event_id, x.seq });
                    table.ForeignKey(
                        name: "location_beacon_id_fkey",
                        column: x => x.beacon_id,
                        principalTable: "beacon",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "location_event_id_fkey",
                        column: x => x.event_id,
                        principalTable: "event",
                        principalColumn: "id");
                },
                comment: "Every stored fix. Kept forever; exported by admins; never read by the public site.");

            migrationBuilder.CreateIndex(
                name: "alert_delivery_outbox",
                table: "alert_delivery",
                column: "outbox_id");

            migrationBuilder.CreateIndex(
                name: "alert_delivery_unsent",
                table: "alert_delivery",
                column: "id",
                filter: "sent_at is null");

            migrationBuilder.CreateIndex(
                name: "beacon_one_active",
                table: "beacon",
                column: "is_active",
                unique: true,
                filter: "is_active");

            migrationBuilder.CreateIndex(
                name: "beacon_log_beacon",
                table: "beacon_log",
                columns: new[] { "beacon_id", "received_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "cookie_event_person",
                table: "cookie",
                columns: new[] { "event_id", "person_id" });

            migrationBuilder.CreateIndex(
                name: "cookie_event_type_visible",
                table: "cookie",
                columns: new[] { "event_id", "cookie_type_id" },
                filter: "hidden_at is null");

            migrationBuilder.CreateIndex(
                name: "cookie_person",
                table: "cookie",
                column: "person_id");

            migrationBuilder.CreateIndex(
                name: "event_one_current",
                table: "event",
                column: "is_current",
                unique: true,
                filter: "is_current");

            migrationBuilder.CreateIndex(
                name: "event_one_live",
                table: "event",
                column: "status_id",
                unique: true,
                filter: "status_id = 3");

            migrationBuilder.CreateIndex(
                name: "event_message_event_created",
                table: "event_message",
                columns: new[] { "event_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "event_status_history_event",
                table: "event_status_history",
                columns: new[] { "event_id", "changed_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "location_event_published_seq",
                table: "location",
                columns: new[] { "event_id", "seq" },
                descending: new[] { false, true },
                filter: "published");

            migrationBuilder.CreateIndex(
                name: "media_asset_state_created",
                table: "media_asset",
                columns: new[] { "state", "created_at" });

            migrationBuilder.CreateIndex(
                name: "outbox_unpublished",
                table: "outbox",
                column: "id",
                filter: "published_at is null");

            migrationBuilder.CreateIndex(
                name: "page_one_per_role",
                table: "page",
                column: "role",
                unique: true,
                filter: "role <> 'none'");

            migrationBuilder.CreateIndex(
                name: "section_page_position",
                table: "section",
                columns: new[] { "page_id", "position", "id" });

            migrationBuilder.CreateIndex(
                name: "section_item_section_position",
                table: "section_item",
                columns: new[] { "section_id", "position", "id" });

            migrationBuilder.CreateIndex(
                name: "sponsor_logo_media",
                table: "sponsor",
                column: "logo_media_id",
                filter: "logo_media_id is not null");

            migrationBuilder.CreateIndex(
                name: "subscriber_person",
                table: "subscriber",
                column: "person_id");

            migrationBuilder.CreateIndex(
                name: "subscriber_verify_token_hash",
                table: "subscriber",
                column: "verify_token_hash",
                filter: "verify_token_hash is not null");

            // sql.md 6 seeds - appended per sql.md 14.3.
            migrationBuilder.Sql(@"
insert into event_status (id, name) values
  (1, 'planned'), (2, 'scheduled'), (3, 'live'), (4, 'ended'), (5, 'cancelled');
");

            migrationBuilder.Sql(@"
insert into live_state (id) values (1);
");

            migrationBuilder.Sql(@"
insert into site_setting_draft (id) values (1);
");

            migrationBuilder.Sql(@"
insert into icon_library_state (id) values (1);
");

            migrationBuilder.Sql(@"
insert into app_setting (key, value, updated_by) values
  ('poll_interval_ms',             '5000', 'seed'),
  ('cookie_limit_per_person',      '10',   'seed'),
  ('sponsor_linger_ms_per_dollar', '40',   'seed'),
  ('sponsor_linger_min_ms',        '2000', 'seed'),
  ('beacon_stale_after_s',         '45',   'seed')
on conflict (key) do nothing;
");

            migrationBuilder.Sql(@"
insert into cookie_type (name, sort, icon)
select v.name, v.sort, v.icon::jsonb
from (values ('Chocolate chip', 10, '{""source"":""library"",""id"":""cookie""}'),
             ('Gingerbread',    20, '{""source"":""library"",""id"":""gingerbread""}'),
             ('Snickerdoodle',  30, '{""source"":""library"",""id"":""cookie-swirl""}'),
             ('Sugar',          40, '{""source"":""library"",""id"":""cookie-star""}'),
             ('Happy',          50, '{""source"":""library"",""id"":""cookie-smile""}')) as v (name, sort, icon)
where not exists (select 1 from cookie_type);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alert_delivery");

            migrationBuilder.DropTable(
                name: "app_setting");

            migrationBuilder.DropTable(
                name: "beacon_enrollment_token");

            migrationBuilder.DropTable(
                name: "beacon_log");

            migrationBuilder.DropTable(
                name: "contact_message");

            migrationBuilder.DropTable(
                name: "content_version");

            migrationBuilder.DropTable(
                name: "cookie");

            migrationBuilder.DropTable(
                name: "event_message");

            migrationBuilder.DropTable(
                name: "event_status_history");

            migrationBuilder.DropTable(
                name: "icon_library_state");

            migrationBuilder.DropTable(
                name: "live_state");

            migrationBuilder.DropTable(
                name: "location");

            migrationBuilder.DropTable(
                name: "preview_token");

            migrationBuilder.DropTable(
                name: "section_item");

            migrationBuilder.DropTable(
                name: "site_setting_draft");

            migrationBuilder.DropTable(
                name: "snapshot");

            migrationBuilder.DropTable(
                name: "sponsor_year");

            migrationBuilder.DropTable(
                name: "outbox");

            migrationBuilder.DropTable(
                name: "subscriber");

            migrationBuilder.DropTable(
                name: "cookie_type");

            migrationBuilder.DropTable(
                name: "beacon");

            migrationBuilder.DropTable(
                name: "event");

            migrationBuilder.DropTable(
                name: "section");

            migrationBuilder.DropTable(
                name: "sponsor");

            migrationBuilder.DropTable(
                name: "person");

            migrationBuilder.DropTable(
                name: "route");

            migrationBuilder.DropTable(
                name: "event_status");

            migrationBuilder.DropTable(
                name: "page");

            migrationBuilder.DropTable(
                name: "media_asset");
        }
    }
}
