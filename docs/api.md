# WMSFO v2 API technical design

The API is the only writer of the database and the bucket, the only publisher on the hub, and the only surface beacons, registered people, and admins talk to. The public site never calls it. Every path, shape, code, and rule is the one in the shared contracts (`docs/contracts.md`); the schema is the one in `docs/sql.md`. Where this document restates either it does so for the implementer's convenience and the contracts win on any difference. Choices this document makes are in section 22; choices that need the owner are in section 23.

---

## 1. Shape

| Item | Value |
|---|---|
| Runtime | .NET 10, ASP.NET Core minimal APIs, one process, one container, port 5000 |
| Data | EF Core 10 with Npgsql for CRUD; raw SQL through Npgsql for every recipe in `sql.md` sections 8 and 9 (`for update`, `skip locked`, `on conflict`, `returning`) |
| Object store | S3 through `AWSSDK.S3`; credentials from the instance role (IMDSv2) in the fleet, from the default chain locally |
| Email | SES v2 through `AWSSDK.SimpleEmailV2` |
| Auth | `Microsoft.AspNetCore.Authentication.JwtBearer` for Cognito ID tokens; a custom scheme for `X-Beacon-Key` |
| Realtime | HTTP to the gateway's internal listener (`/internal/publish`, `/internal/leader`); two callback endpoints the gateway POSTs to. The API hosts no SignalR of its own |
| Images | `SixLabors.ImageSharp` for raster sniffing, dimensions, and the WebP width variants; `System.Xml` for SVG validation |
| Content schemas | `JsonSchema.Net` (draft 2020-12) validating section data, items, presentation, and site settings against the files under `contracts/schema/`; the draft-level variant is derived at boot by stripping `required`, `minLength`, `minItems`, and `minimum` |
| Uploads | S3 presigned `PUT` URLs from `AWSSDK.S3` (`GetPreSignedURL` with the content type and the `x-amz-tagging` header signed); the API never receives media bytes |
| QR | `QRCoder` for the enrollment PNG |
| Serialization | `System.Text.Json` with one shared `JsonSerializerOptions` (contracts 1.6) used for REST, CDN objects, hub payloads, and the fixtures |
| Logging | JSON lines to stdout (`Microsoft.Extensions.Logging` JSON console formatter); CloudWatch through the gateway |
| Tests | xUnit; Testcontainers for Postgres; a local object store for S3; contract tests against `contracts/` |

The service name, and therefore the hub channel prefix, comes from `WMSFO_SERVICE_NAME`; nothing in the code knows whether it is dev or prod beyond `WMSFO_ENV` for log tags.

---

## 2. Solution layout

Repository `wmsfo-api`, branch flow `dev` and `main`.

```
wmsfo-api/
  Wmsfo.slnx
  Dockerfile
  compose.yaml  dev/                  # local Postgres with both roles and the static file server (section 20)
  .github/workflows/deploy.yml
  contracts/                          # section 21: openapi.json, schema/ (incl. primitives, sections/<kind>, site-settings, content-document), kinds.json, starter-content.json, fixtures/, icons/, admin-thresholds.json, help-keys.json, CONTRACTS_VERSION
  icons/                              # the icon library: <id>.svg files plus library.json (id, name, tags)
  help/topics.json                    # the help topic seed: one entry per help popover of the admin panel (section 11a.9)
  templates/email/                    # _layout.html and _layout.txt, <name>.html and <name>.txt body fragments, logo.png, ornaments.png, lights.png, _golden/ (contracts 7.8)
  src/Wmsfo.Api/
    Program.cs                        # composition root, startup order (section 3), readiness, GET /api/health
    Config/WmsfoOptions.cs            # every WMSFO_* key, validated on boot (section 4)
    Config/ConnectionStrings.cs       # NpgsqlConnectionStringBuilder per sql.md 13
    Config/WmsfoReadinessGate.cs      # the ready flag the migrator flips; read by the readiness middleware and the health probe
    Data/WmsfoDbContext.cs  Entities.cs   # one entity per table (sql.md 14.2)
    Data/DesignTimeDbContextFactory.cs    # for dotnet ef
    Data/Migrations/                  # EF Core migrations
    Data/Sql/Sql.cs                   # the raw recipes of sql.md 8 and 9 as string constants
    Data/DatabaseMigrator.cs          # advisory lock, migrate, then the first-boot steps (sql.md 8.16)
    Http/WmsfoPipeline.cs             # the middleware order of section 5 as one extension, shared with the test hosts
    Http/ApiException.cs  ErrorHandling.cs      # the error shape and the exception handler
    Http/RequestValidation.cs         # validators returning details.fields
    Http/ConstraintErrorMapping.cs    # 23505 constraint names to error codes (sql.md 4.3)
    Http/ForwardedHeaders.cs          # client IP from X-Forwarded-For with WMSFO_TRUSTED_PROXY_HOPS
    Http/BothAuthHeadersGuard.cs      # Authorization plus X-Beacon-Key is 400
    Http/RateLimits.cs                # token buckets per contracts 4.0
    Http/BodyLimits.cs  CorsPolicy.cs  NoStore.cs  ServerTimeFilter.cs
    Http/JsonConsoleLogging.cs  LogMarkers.cs  HealthMarkerLogger.cs   # sections 16 and 17
    Auth/AuthConstants.cs             # scheme and policy names
    Auth/BeaconKeyAuthHandler.cs      # X-Beacon-Key scheme
    Auth/CognitoAuth.cs               # the two JwtBearer schemes, the policies, person upsert
    Auth/AdminTotpGate.cs             # section 6.3
    Auth/ApiKeyAuthHandler.cs         # section 6.4: wak_ bearer scheme
    Auth/CapabilityAuthorization.cs   # RequireCapability, DenyApiKeys, CapabilityOrGroup
    Auth/DevStaticTokens.cs           # WMSFO_DEV_STATIC_TOKENS (section 20)
    Endpoints/BeaconEndpoints.cs      # enroll, me, locations, heartbeat, logs
    Endpoints/LocationIngest.cs       # the validate-and-store path shared by POST /locations and the hub message path (contracts 7.2)
    Endpoints/PublicWriteEndpoints.cs # contact, subscriptions verify and unsubscribe
    Endpoints/MeEndpoints.cs          # me, subscriptions, cookies, alerts
    Endpoints/RealtimeEndpoints.cs    # /realtime/authorize, /realtime/message
    Endpoints/AdminEventEndpoints.cs  AdminRouteEndpoints.cs  AdminBeaconEndpoints.cs  AdminSponsorEndpoints.cs
    Endpoints/AdminCookieTypeEndpoints.cs  AdminApiKeyEndpoints.cs  AdminSettingsEndpoints.cs
    Endpoints/AdminInboxEndpoints.cs  # contact messages, subscribers, people
    Endpoints/AdminContentEndpoints.cs    # pages, sections, items, site settings, kinds, status, draft, publish, versions, restore, preview token, GET /preview/document
    Endpoints/AdminMediaEndpoints.cs  # list, ticket (presign), confirm, usage, patch, delete
    Endpoints/AdminIconEndpoints.cs
    Endpoints/AdminAuditEndpoints.cs  AuditRecorder.cs     # section 5a
    Endpoints/AdminImpactEndpoints.cs  Impact/             # section 5b: one <Resource>ImpactQueries per resource, LocationClearQueries, ImpactHelpers, ImpactBefore
    Endpoints/QrEndpoints.cs  QrRead.cs  PlaceEndpoints.cs  PlaceRead.cs   # section 11b
    Endpoints/AdminPosterEndpoints.cs # posters: list, create, get, patch, delete (contracts 4.5 Posters)
    Endpoints/AdminHelpEndpoints.cs   # help topics: list, put, reset (contracts 4.5 Help)
    Endpoints/AdminMapEndpoints.cs    # tracker maps: list, create (multipart start), parts, complete, confirm, patch, delete (section 11.6)
    Endpoints/AdminThemeEndpoints.cs  # tracker themes: list, create, patch, sprite tickets and confirm, default, delete (section 11a.10)
    Endpoints/AdminHelpers.cs         # the audit email, the [snapshot] frame, shared plumbing
    Node/NodeState.cs                 # in-memory state (contracts 7.1)
    Node/NodeCounters.cs              # per-node counters on GET /admin/live
    Node/ReconcileTick.cs             # hosted service (contracts 7.4)
    Node/LeaderMonitor.cs             # hosted service (contracts 7.5)
    Node/LiveObjectWriter.cs          # build, PUT, publish, live_state (contracts 1.8)
    Node/SnapshotBuilder.cs           # inside the admin transaction (contracts 7.3); embeds the content document, media map, icon map
    Node/RouteMapBuilder.cs           # event.routeMap: simplified, smoothed, capped path and the 5 minute timeline (contracts 1.3)
    Node/SnapshotBootstrap.cs  FleetFirstBootHook.cs   # the first-boot steps of sql.md 8.16
    Node/AdminDiagnosticsEndpoints.cs # snapshot, rebuild, live, republish (section 10.3)
    Content/KindRegistry.cs           # loads contracts/kinds.json and the kind schemas; KindInfo for the panel
    Content/SchemaValidator.cs        # publish-level and draft-level validation, Problem lists with JSON pointers
    Content/ReferenceChecker.cs       # media readiness, library icon ids, href rule, page-slug links, anchors, map-on-live
    Content/InlineText.cs             # the inline markdown grammar: parse for validation and reference extraction
    Content/DocumentBuilder.cs        # working set to ContentDocument (hidden omitted, contract order) and the media id scan
    Content/Publisher.cs              # the publish transaction (sql.md 8.19)
    Content/Restorer.cs               # sql.md 8.20
    Content/StarterContent.cs         # first-boot seed from contracts/starter-content.json
    Icons/IconLibrary.cs              # loads icons/, hashes, writes to the bucket, builds the icons map
    Themes/ThemeStyles.cs             # the seeded themes' style bodies from contracts/fixtures/themes/, the boot ensure of their objects (section 11a.10)
    Themes/ThemeStyleValidator.cs  ChromeContrast.cs   # the style shape rules and the WCAG contrast check of contracts 4.5 Themes
    Help/HelpTopics.cs                # loads and validates help/topics.json, the boot ensure of help_topic
    Chores/ChoreHost.cs               # runs chores while leader
    Chores/OutboxPublisher.cs  AlertSender.cs  StaleBeaconFlagger.cs  MediaOrphanCollector.cs  NightlyCleanup.cs  PendingMapSweeper.cs  IChoreClock.cs
    Objects/IObjectStore.cs           # put, delete, list, copy with headers, presign PUT; multipart start, presign part, complete, abort, list open uploads; ranged get (section 11.6)
    Objects/S3ObjectStore.cs  LocalObjectStore.cs
    Objects/CanonicalJson.cs          # the serializer options and sha256 helper (contracts 1.6)
    Objects/CdnObjects.cs  ContentDocument.cs   # LiveObject, Snapshot, Route, and the content document DTOs in contract key order
    Realtime/GatewayInternalClient.cs # publish, leader, and presence calls with the injected token
    Media/ImageSniffer.cs  SvgValidator.cs  VariantDeriver.cs  DeepZoomTiler.cs  MediaUsage.cs  FilenameSanitizer.cs
    Security/Keys.cs                  # key and token minting, hashing, AES-GCM
    Security/QrRenderer.cs
    Email/SesSender.cs  EmailTemplates.cs  EmailQuotaReader.cs
    Contracts/Dtos/                   # request and response DTOs per resource family
    Contracts/OpenApiExport.cs  SchemaExport.cs  FixtureExport.cs  FixtureData.cs  StarterContentBuilder.cs  AdminThresholds.cs  EndpointStubs.cs   # the export-contracts mode (section 21)
  tools/Wmsfo.Migrate/                # the one-off legacy migration tool (sql.md 15, platform.md 12)
  tools/tiles/                        # the tile package CLI, Node 22 with its own package.json and node --test, run on an operator's machine (platform.md 1.8)
  .dockerignore                       # created by the CLI task (it does not exist today): tools/tiles/node_modules and tools/tiles/out, so COPY . . leaves them out of the image
  tests/Wmsfo.Api.Tests/              # unit + contract tests
  tests/Wmsfo.Api.IntegrationTests/   # Postgres-backed, full pipeline
```

---

## 3. Startup sequence

`Program.cs` runs these in order; the container answers `503` on `/api/health` until step 8 completes.

1. Read configuration from environment variables into `WmsfoOptions`; fail fast on any missing or malformed value (section 4). Log one line with every non-secret value.
2. Build the two connection strings (sql.md 13) and register `WmsfoDbContext` (app connection) and the migration factory (migrate connection).
3. Register services: object store (S3 in the fleet, local directory when `WMSFO_OBJECT_STORE_DIR` is set), gateway internal client, SES sender, key service, rate limiter, node state, live object writer, snapshot builder.
4. Build the HTTP pipeline (section 5) and start listening. Listening early lets the gateway's health prober see `503` instead of connection refused.
5. `DatabaseMigrator.MigrateAsync`: advisory lock, `MigrateAsync`, then the first-boot steps of sql.md 8.16 in order: `StarterContent.EnsureSeededAsync`, `IconLibrary.EnsureWrittenAsync`, `ThemeStyles.EnsureWrittenAsync` (section 11a.10), `HelpTopics.EnsureWrittenAsync` (section 11a.9), `EmailLogo.EnsureWrittenAsync` (contracts 7.8), `Publisher.EnsureVersionOneAsync`, `SnapshotBootstrap.EnsureVersionOneAsync` (or a rebuild when the icon library changed). On failure retry with 5 s, 10 s, 30 s, then 60 s; health stays `503`.
6. `NodeState.LoadAsync`: one reconcile round trip (contracts 7.4) to fill memory.
7. Start hosted services: `ReconcileTick`, `LeaderMonitor`, `ChoreHost`.
8. Mark ready. `GET /api/health` now answers `200` while `select 1` succeeds.

A first boot with an empty database seeds the starter content, writes the icon library, publishes content version 1, builds snapshot version 1 inside step 5, and writes the live object once (contracts 5). The kind registry, the schemas, and the help topic seed are loaded in step 3 and a malformed schema file or seed entry fails the boot. `SIGTERM`: stop accepting requests, wait up to 20 s for in-flight requests and any in-flight live-object write, exit. The gateway's blue-green keeps the old container serving until the new one is `200`, so shutdown never drops traffic.

---

## 4. Configuration

Every key in contracts 8.1 binds to `WmsfoOptions`. Validation happens once at boot with `IValidateOptions`; a failure prints the key name (never the value) and exits with code 2.

| Key | Type | Validation |
|---|---|---|
| `ASPNETCORE_URLS` | url | Kestrel reads it directly; `http://0.0.0.0:5000` in the fleet |
| `WMSFO_ENV` | `dev` or `prod` | log tag only |
| `WMSFO_SERVICE_NAME` | string | `^[a-z0-9-]+$`; the hub channel prefix and `ApplicationName` on the connection |
| `WMSFO_DB_CONNECTION`, `WMSFO_DB_MIGRATION_CONNECTION` | Npgsql strings | parse with `NpgsqlConnectionStringBuilder`; `SSL Mode=Require` required |
| `AWS_REGION` | region | non-empty |
| `WMSFO_S3_BUCKET` | string | non-empty |
| `WMSFO_CDN_BASE_URL`, `WMSFO_PUBLIC_API_BASE_URL`, `WMSFO_SITE_BASE_URL` | https url | absolute, no path, no trailing slash |
| `WMSFO_HUB_URL` | wss url | absolute |
| `WMSFO_GATEWAY_INTERNAL_URL` | http url | absolute; `http://<docker-bridge-ip>:8080` |
| `GATEWAY_REALTIME_TOKEN` | string | injected by the gateway; optional locally, then publish and leader calls are disabled and logged once |
| `WMSFO_CORS_ORIGINS` | list | comma-separated absolute origins, no wildcard |
| `WMSFO_TRUSTED_PROXY_HOPS` | int | 0 to 5, default 2 |
| `WMSFO_COGNITO_ISSUER` | https url | the people pool's issuer |
| `WMSFO_COGNITO_CLIENT_IDS` | list | one or more, the people pool's clients |
| `WMSFO_COGNITO_USER_POOL_ID` | string | the people pool id |
| `WMSFO_COGNITO_ADMIN_ISSUER` | https url | the admin pool's issuer; empty means the people pool also carries the admins (refused when `WMSFO_ENV` is `prod`) |
| `WMSFO_COGNITO_ADMIN_CLIENT_IDS` | list | the admin pool's clients; required with the admin issuer |
| `WMSFO_COGNITO_ADMIN_USER_POOL_ID` | string | the admin pool id for `AdminGetUser`; required with the admin issuer |
| `WMSFO_ADMIN_GROUP` | string | default `admin` |
| `WMSFO_EDITOR_GROUP` | string | default `editor` |
| `WMSFO_CANVASSER_GROUP` | string | default `canvasser`, a slug; the admin-pool group admitted to the QR codes and places endpoints (section 11b) |
| `WMSFO_SCAN_SALT` | string | required, non-empty; salts `qr_scan.ip_hash` (section 11b) |
| `WMSFO_SES_FROM_ADDRESS` | mailbox | `Name <address>` or bare address |
| `WMSFO_SES_CONFIGURATION_SET` | string | may be empty |
| `WMSFO_CONTACT_NOTIFY_EMAIL` | address | valid |
| `WMSFO_ALERT_SEND_PER_SEC` | int | 1 to 50, default 10 |
| `WMSFO_ENROLLMENT_ENCRYPTION_KEY` | base64 | decodes to exactly 32 bytes |
| `WMSFO_RECONCILE_TICK_MS` | int | 250 to 10000, default 1000 |
| `WMSFO_LOG_LEVEL` | level | `Debug`, `Information`, `Warning` |
| `WMSFO_FORCE_LEADER` | bool | local only; refused (boot failure) when `WMSFO_ENV` is `prod` |
| `WMSFO_OBJECT_STORE_DIR` | path | local only; when set the object store writes to this directory and `WMSFO_CDN_BASE_URL` may point at a local static server |
| `WMSFO_DEV_STATIC_TOKENS` | bool | local and tests only (section 20); refused when `WMSFO_ENV` is `prod` |
| `WMSFO_SES_DRY_RUN` | bool | local only: logs the rendered message instead of sending (section 20); refused when `WMSFO_ENV` is `prod` |

`WMSFO_DB_MIGRATION_CONNECTION` follows `sql.md` 12 and 13 (two roles).

Pool parameters are set in code (sql.md 13), never in the secret. The options object is immutable after boot; there is no hot reload.

---

## 5. HTTP pipeline

Middleware order in `Program.cs`, outermost first:

1. **Readiness**: until the ready flag is set (section 3 step 8) every request except `GET /api/health` answers `503 unavailable`.
2. **Forwarded headers**: `ForwardedHeadersMiddleware` with `ForwardLimit = WMSFO_TRUSTED_PROXY_HOPS`, known networks cleared, so `HttpContext.Connection.RemoteIpAddress` is the client IP counted from the right of `X-Forwarded-For`. It is mounted with `UseWhen` on every path except `/realtime/*`, because the middleware removes the `X-Forwarded-For` entries it consumes and the callback guard (section 12.1) must see the raw headers.
3. **Request id and logging scope**: `Activity.Current?.Id ?? HttpContext.TraceIdentifier` becomes `requestId`; every log line in the request carries it.
4. **Exception handler**: maps `ApiException` to its status and code, `BadHttpRequestException` (body too large, malformed JSON) to `413 payload_too_large` or `400 validation_failed`, `OperationCanceledException` on a client abort to nothing, everything else to `500 internal_error` with the stack logged at Error and never returned.
5. **Body size limits**: per route through `RequestSizeLimit` metadata: 64 KB JSON default, 256 KB section, item, and site settings bodies, 5 MB routes, 2 MB beacon logs, 32 KB heartbeats. Media bytes never arrive here.
6. **CORS**: one policy with the exact origins from `WMSFO_CORS_ORIGINS`, methods `GET, POST, PUT, PATCH, DELETE`, headers `Authorization, Content-Type, X-Beacon-Key, X-App-Version, If-None-Match`, exposed header `ETag`, `SetPreflightMaxAge(600)`, credentials off. Applied to every route except the two callbacks and `/api/health`.
7. **Authentication**: two schemes registered; each endpoint names the one it requires through `RequireAuthorization(policy)`. A request carrying both `Authorization` and `X-Beacon-Key` is `400 validation_failed` before any scheme runs.
8. **Rate limiting**: `Microsoft.AspNetCore.RateLimiting` token-bucket policies per contracts 4.0, partitioned by beacon id, person id, or client IP as the table says; over budget writes the error shape with `retryAfterSeconds` and the `Retry-After` header. The callbacks and `/api/health` carry `DisableRateLimiting`.
9. **Endpoints**.

Response conventions: every JSON response uses the shared serializer options; `Cache-Control: no-store` on every API response; `serverTime` is added by a result filter on the beacon endpoints, stamped after the handler returned, which is after any transaction committed.

Error shape: `ApiException(status, code, message, details)`; `RequestValidation` collects field errors into `details.fields` and throws one `validation_failed`. Unknown JSON fields are rejected by `JsonSerializerOptions.UnmappedMemberHandling = Disallow` on request DTOs, with the three exceptions the contracts list handled by dedicated DTOs that allow extension data.

## 5a. Audit recording

Every admin write records one `audit_log` row inside the write's own transaction (contracts 4.5 Audit). `AuditRecorder` (a scoped service) is handed the open `NpgsqlTransaction` by the endpoint and takes `(action, entity, entityId, before, after)`; the actor comes from the request's principal (`person:<email>` for an ID token, `key:<name>` for an API key) and the request id from `HttpContext.TraceIdentifier`. `before` and `after` are the endpoint's own response DTOs serialized with the wire options, so the log reads like the API answers; a delete records `before` only, a create `after` only. The generic CRUD endpoints call it through one helper (`Audited.Create`, `.Update`, `.Delete`); the endpoints with their own verbs (status, notify, current, clone, activate, deactivate, revoke, rotate, order, copy, import, confirm, publish, restore, move, duplicate, enroll, reset, parts, complete, sprite, default) pass that verb. The map and theme writes record entities `tracker_map` and `tracker_theme` with the `TrackerMap` and `TrackerTheme` DTOs; `parts` records the request's `file` and `partNumbers` in `after` (nothing on the row changes), `complete` and `confirm` the DTO. The help topic writes record entity `help_topic` with the key as `entity_id`: `PUT /admin/help/{key}` action `update`, `POST /admin/help/{key}/reset` action `reset`, both with the `HelpTopic` before and after. Two event writes record shapes of their own rather than the `Event` DTO: `locations_cleared` (`before = { count, byBeacon }`, `after` null) and `cookies_seeded` (`before` null, `after = { items: [ { cookieTypeId, count } ], seeded }`, the request's items in request order and their total). A write that commits without an audit row is a bug the integration tests catch: a test runs every admin write once and asserts the row count grew by one per call.

`AuditStamp` on the DTOs is one lateral subquery per list or detail read (`select action, actor, at from audit_log where entity = $kind and entity_id = $id order by id desc limit 1`), served by the `(entity, entity_id, id desc)` index; the media, page, section, and setting reads that already carry `updatedBy` keep those fields as they are.

`GET /admin/audit` pages by `id desc` with a keyset cursor; `entity`, `entityId`, `action`, `actor` filter with equality; `limit` 1 to 200. `GET /admin/audit/entities` is `select distinct entity`.

---

## 5b. Delete impact and cascades

`DeleteImpact` (contracts 4.5 Delete impact) comes from one `ImpactQueries` class per resource in `Endpoints/Impact/`: each has `PreviewAsync(conn, tx, id)` returning the `DeleteImpact` and `ApplyAsync(conn, tx, id)` running the same statements as deletes and updates inside the delete's transaction (close, null, or delete the dependents, then the row). The preview endpoint `GET /admin/<resource>/{id}/impact` calls `PreviewAsync` alone; the delete endpoint calls `PreviewAsync`, then `ApplyAsync`, then records its audit row with `before = { ...dto, impact }`. Names in a group are the first ten by id (`name`, `title`, `tag`, `email`, or `filename` as the resource has); counts are exact. Warnings are fixed sentences: "This page holds the <role> role; pick the page that takes it.", "This beacon is active; the live feed stops.", "An event is live; its cookie tally drops by <n>." `blocked` is set by the events preview: "This event is live. End it first." (status 3) or "This is the current event. Make another event current first." (`is_current`); the events delete answers `409 event_live` or `409 event_current` in the same cases and never runs its cascade for them. It is also set by the themes preview for a `google` theme that is the only enabled Google theme on some event ("This is the only Google theme enabled on <event name>. Enable another there first.", the first such event by `year` desc); the theme delete answers `409 last_google_theme` unless its body names a `google` `replacementId`. `TrackerMapImpactQueries` and `TrackerThemeImpactQueries` take the optional `replacementId` of the delete body into `ApplyAsync`: with one, the referencing events are repointed (map) or re-enabled on the replacement (theme) and the default flags move, instead of the unlink; the preview is the same either way (contracts 4.5 Maps and Themes). Foreign keys back the cascades (sql.md 14 `DeleteCascades`): `location.event_id`, `cookie.cookie_type_id`, `event_message.event_id`, `status_history.event_id`, `event_tracker_theme.event_id`, `event_tracker_theme.theme_id` cascade; `event.route_id`, `poster.route_id`, `sponsor.logo_media_id`, `event.tracker_map_id`, `tracker_theme.thumbnail_media_id` set null; content references to media are cleared by `ApplyAsync` (they live in JSON).

## 6. Authentication and authorization

### 6.1 Beacon key scheme

`BeaconKeyAuthHandler` (an `AuthenticationHandler`):

1. Read `X-Beacon-Key`. Absent: no result (the endpoint's policy then answers `401`).
2. Check the regex `^wbk_[A-Za-z0-9_-]{43}$`; on mismatch fail with `401 unauthenticated`.
3. `select id, is_active, revoked_at, key_version from beacon where key_hash = sha256($key)`; missing or `revoked_at` set: `401 unauthenticated`. The lookup is one indexed read; no timing-sensitive comparison is needed because the index lookup is on the hash.
4. Principal claims: `beacon_id`, `beacon_active`, `key_version`. One policy, `Beacon`; there are no beacon roles.

`last_seen_at` is stamped by the handlers that the contracts say stamp it, not by the scheme.

### 6.2 Cognito ID token scheme

Two `JwtBearer` schemes, `CognitoPeopleJwt` from `WMSFO_COGNITO_ISSUER` and `CognitoAdminJwt` from `WMSFO_COGNITO_ADMIN_ISSUER` (the people pool's values again when the admin issuer is empty), behind one policy scheme named `CognitoJwt` whose selector reads the bearer's unverified `iss` and forwards to the admin scheme when it equals the admin issuer, else to the people scheme; the chosen scheme then verifies the token in full:

```csharp
options.Authority = issuer;                            // discovery + JWKS, cached, refreshed on unknown kid
options.TokenValidationParameters = new()
{
    ValidIssuer = issuer,
    ValidAudiences = audiences,
    ValidateLifetime = true,
    ClockSkew = TimeSpan.FromSeconds(60),
};
options.Events.OnTokenValidated = ctx => RequireTokenUse(ctx, "id") then stamp wmsfo_pool = "admin" | "people";
```

After validation an endpoint filter upserts the person (contracts 3.1 SQL) and attaches `person_id` to the request. Policies: `Person` (any valid token from either pool), `Editor` (`wmsfo_pool` is `admin` and `cognito:groups` contains `WMSFO_EDITOR_GROUP` or `WMSFO_ADMIN_GROUP`), `Admin` (`wmsfo_pool` is `admin` and the groups contain `WMSFO_ADMIN_GROUP`); a policy miss is `403 forbidden`. Under `WMSFO_DEV_STATIC_TOKENS` the static handler registers as `CognitoJwt` itself and stamps the admin and editor tokens with the admin pool. Every `/admin/*` route names `Editor` or `Admin` per the contracts' group headings (4.5); the audit email is the `email` claim.

### 6.3 Admin TOTP gate

`AdminTotpGate` is an endpoint filter attached by `RequireCapability` and `DenyApiKeys` (so every `/admin/*` endpoint carries it, under both the `Editor` and the `Admin` policy) that calls `AdminGetUser` for the token's `sub` and answers `403 mfa_required` unless `UserMFASettingList` contains `SOFTWARE_TOKEN_MFA`; an enrolled answer is cached 5 minutes per user and a not-enrolled answer is not cached at all, so an admin who enrols on the panel's setup page is admitted on the very next request. Needs `cognito-idp:AdminGetUser` on the admin pool for the instance role and `WMSFO_COGNITO_ADMIN_USER_POOL_ID` in the secret (the people pool id when the admin issuer is empty). A Cognito call failure is treated as not enabled (`403`), logged at Warning; the cache means one failure per user per 5 minutes at most. The gate skips requests authenticated by an API key (6.4). The checker is `CognitoAdminTotpChecker` over an `AmazonCognitoIdentityProviderClient` for `AWS_REGION`; with `WMSFO_DEV_STATIC_TOKENS` (section 20) there is no pool to ask and every admin counts as enrolled.

### 6.4 API key scheme

`ApiKeyAuthHandler` (an `AuthenticationHandler`), registered alongside the JWT scheme on the same `Authorization` header; the composite `CognitoOrApiKey` scheme picks by prefix: a bearer value starting with `wak_` goes here, anything else goes to JwtBearer.

1. Check the regex `^wak_[A-Za-z0-9_-]{43}$`; on mismatch fail with `401 unauthenticated`.
2. `select id, name, all_capabilities, capabilities, expires_at, revoked_at from api_key where key_hash = sha256($key)`; missing, `revoked_at` set, or `expires_at` in the past: `401 unauthenticated`. One indexed read on the hash, no timing-sensitive comparison.
3. Principal claims: `api_key_id`, `api_key_name`, `api_key_all`, and one `api_key_capability` claim per entry. Audit actor is `key:<name>`; no `person` upsert; `last_used_at` stamped fire-and-forget at most once a minute per key.

Capabilities are endpoint metadata: every `/admin/*` endpoint group is mapped with `.RequireCapability("<group>")` (the names in contracts 3.6), and the `Editor` and `Admin` policies each carry a second requirement, `CapabilityOrGroup`, which passes when the principal is a Cognito user in an admitted group, or an API key with `api_key_all` or a matching `api_key_capability`. The three API-key endpoints carry `.DenyApiKeys()` and answer `403 forbidden` to a key principal. `BothAuthHeadersGuard` keeps rejecting `X-Beacon-Key` alongside `Authorization`.

Minting (`POST /admin/api-keys`): generate 32 CSPRNG bytes, base64url without padding, prefix `wak_`; store `sha256` and the first 12 characters; validate `name` (unique among unrevoked, `409 name_taken`), `allCapabilities` with `capabilities` (empty when all, a non-empty distinct subset otherwise), `expiresAt` (null or `>= now + 1h`); answer `201` with the key once. `Keys.cs` already holds the hashing and generation used for beacon keys; the same helpers apply.

---

## 7. Endpoint map

Every endpoint from contracts section 4, with the handler responsibility and the recipe it runs. Bodies, responses, and error codes are as the contracts specify and are not repeated. The capability in parentheses after the policy is the API-key capability that also admits the group (6.4).

| Endpoint | Policy | Handler responsibility | Recipe |
|---|---|---|---|
| `GET /api/health` | none | ready flag and `select 1` with 2 s timeout | |
| `POST /beacons/enroll` | none (IP-limited) | validate token format; one transaction: lock the token row by hash, check unconsumed and unexpired and the beacon not revoked, decrypt `key_ciphertext`, set `consumed_at`, null the ciphertext; respond with the key and URLs from options | sql.md 8 enroll |
| `GET /beacons/me` | Beacon | stamp `last_seen_at`; read the live event id | |
| `POST /locations` | Beacon | validate; the `BeaconRateLimiter` (section 8) drops fixes inside the beacon's effective min interval before any transaction and flushes a per-beacon count to `beacon.fixes_rate_limited` at most every 5 s; the location transaction (7.2) reads the beacon's last stored fix and decides `stored` vs `carried` per `location_min_distance_m`, and the unique `(event_id, lat, lng)` index carries any repeat of a position already in the event; the response body carries `outcome`; then `LiveObjectWriter.WriteForLocation` when `published` (carried publishes just like stored) | contracts 7.2 |
| `POST /beacons/heartbeat` | Beacon | validate `sentAt`, the three optional `health` leaves, and the `debug` object's depth and size (section 15); store the body as telemetry, stamp `last_heartbeat_at`, `last_seen_at`, clear `stale_since`; respond with the live event id and `is_active` | |
| `POST /beacons/logs` | Beacon | `text/plain` only; insert `beacon_log` | |
| `POST /contact` | none (IP-limited) | validate; insert `contact_message` and outbox `contact.received` in one transaction | |
| `POST /subscriptions/verify` | none | hash lookup; set `verified_at` per the contracts' three cases | |
| `POST /subscriptions/unsubscribe` | none | token from query or JSON; accept form-urlencoded and ignore its body; set `unsubscribed_at` | |
| `GET /me` | Person | person row and `isAdmin` | |
| `GET /me/subscriptions`, `POST`, `POST .../resend-verification`, `DELETE .../{id}` | Person | per contracts 4.4; verify token mint and outbox `subscription.verify` in the same transaction | |
| `GET /me/cookies` | Person | current event, limit from settings, counts | |
| `POST /cookies` | Person | the whole pick in one cookie transaction (every type checked, the limit checked against the total, one multi-row insert); increment the node tally per cookie | sql.md 8.9 |
| `/admin/events*` | Admin (`events`) | events, status (with the optional `message`: trimmed, 1 to 1000, inserted as an `event_message` row (`created_by` the actor, its `notify` false) before the history row with `notify` on or off; without a `message` the row is inserted anyway with `EmailTemplates.StockParagraph(to, name, scheduled_at, schedule_time_zone)` as its body, read from the locked event row, so the email's `{{customMessage}}` is that same text; the history row's `message_id` and the `event.status_changed` payload's `messageId` reference it, the audit `after` is the Event plus `messageId`, and the snapshot rebuild in the same transaction carries it as `latestMessage`), notify (announce the current status again; the same `message` handling with `messageId` in the `event.status_notified` payload, run through `AdminSnapshotTransaction.RunAsync` when a message is given and `RunWithoutSnapshotAsync` otherwise), clone (with sponsors, route, route map configuration as ticked), messages (`CreateEventMessageRequest` is `body` and `notify`, `PatchEventMessageRequest` is `body`, both with `[JsonUnmappedMemberHandling(Disallow)]`, so `eventTime` or any other field fails binding with `400`; a message's time is its `created_at`; a post with `notify` true inserts the `event.message_posted` outbox row and sets the message's `outbox_id` in the same transaction; `EventMessage` carries `notify` and `sentCount`), status history (with `notify`, `message` and `messageId` from a left join to `event_message`, `sentCount`), locations export (CSV header `seq,beaconId,published,recordedAt,receivedAt,lat,lng,speedMps,speedSource,altitudeM,headingDeg,accuracyM`), clear recording (`DELETE /admin/events/{id}/locations?beaconId=` with `409 event_live`; the audit action is `event.locations_cleared`), locations impact preview, seeded cookies (`POST /admin/events/{id}/cookies`: validate 1 to 50 items, each type once, `count` 1 to 100; one transaction locks the event row `for update`, refuses `409 event_not_live` unless status 3, checks the types like `POST /cookies` (`404` with `details.cookieTypeIds`), inserts one row per cookie with `person_id` null and `seeded_by` the `AuditRecorder` actor, reads the event's tally, records `cookies_seeded`, commits; then `NodeStateService.IncrementTallyDelta` once per cookie; no outbox row, no snapshot rebuild; sql.md 8.9a), route map (`GET /admin/events/{id}/route-map` under the Editor policy, any event, `RouteMapBuilder.Build` over the linked route object with the three `route_map_*` settings, `{ routeMap: null }` without a recording, not a snapshot write), the tracker fields (`trackerBbox` validated against `$defs/Bbox` with the 0.05 to 20 degree side rule in `Validate(dto)`; create fills an absent box from the published document's `settings.tracker.defaultBbox` or the Missoula valley constant and copies the map and the theme set from the event with the greatest `year` per sql.md 8.7; patch checks `trackerMapId` against a `ready` row whose `bbox` contains the event's box, `trackerBbox` against the linked map's package, and `trackerThemeIds` for at least one `google` theme, each `400 validation_failed` at its path, then replaces the `event_tracker_theme` rows whole; clone copies the box always and the map and theme set under `copy.tracker`; the `Event` DTO reads `trackerThemeIds` with one `select theme_id from event_tracker_theme ... order by sort_order, id` join); the event has no route image; `scheduleTimeZone` must resolve with `TimeZoneInfo.TryFindSystemTimeZoneById` (`400 validation_failed`), null clears it; `routeMapConfig` is checked with `SchemaValidator.ValidateRouteMapConfig` (`$defs/RouteMapConfig`, full level; a `landmarks` key is unknown there and fails at `routeMapConfig.landmarks`, the landmarks being `settings.landmarks`), stored as the canonical `RouteMapConfig` in `event.route_map_config`, null clears it, and the snapshot builder reads it for the current event; clone copies it behind `copy.routeMapConfig`; `PatchEventRequest` carries `[JsonUnmappedMemberHandling(Disallow)]`, so a field it does not list (such as `posterLayout` or `routeImageMediaId`) fails binding with `400`; status 3 requires a healthy active beacon (`409 no_healthy_beacon` with `details.beacon`), checked on the locked event row against the `is_active` beacon's `revoked_at`, `stale_since`, and `last_seen_at` | contracts 7.3 for the [snapshot] writes; status transaction per contracts 4.5 and sql.md 8.4 |
| `/admin/routes*` | Admin (`routes`) | flight recordings: canonicalize, hash, existing-row check, PUT, insert; `from-event` reads the event's published locations in `seq` order and feeds the same path; route map (`GET /admin/routes/{id}/route-map` under the Editor policy, any recording, `AdminRouteEndpoints.BuildRouteMapAsync`, the same `RouteMapBuilder.Build` path as the event route map, `404` for an unknown route); `DELETE` unlinks events and posters (`RouteImpactQueries` lists both under `unlinks`) | section 11.1 |
| `/admin/maps*` | Admin (`maps`) | tracker maps (section 11.6): list with `eventCount`, create (package key, pending reuse, multipart start), parts (presigned `UploadPart` URLs), complete, confirm (the 127-byte PMTiles header check, `manifest.json`, `ready`), patch (`name`), impact, delete with the optional `replacementId`; `TrackerMapImpactQueries` lists the events under `unlinks` | contracts 4.5 Maps |
| `/admin/themes*` | Admin (`themes`) | tracker themes (section 11a.10): list with `eventCount`, create (style validation, canonical body to `themes/{sha}.json`, contrast check), patch, sprite tickets under a content-addressed prefix and sprite confirm, default (clear then set per renderer), impact (the last-Google-theme `blocked`), delete with the optional `replacementId`; `TrackerThemeImpactQueries` lists the events under `unlinks` | contracts 4.5 Themes |
| `/admin/posters*` | Editor (`events`) | list (`order by id desc`: `id`, `name`, `routeId`, `updatedAt`), create, get, patch, delete, impact preview; `routeId` must name a route (`404`), null clears it; `layout` must be a JSON object whose `CanonicalJson.SerializeOpaqueToUtf8Bytes` form is at most 32768 bytes (`400 validation_failed`), null clears it, stored as that canonical text and never read; every write runs through `AdminSnapshotTransaction.RunWithoutSnapshotAsync` (no snapshot rebuild) and records an `audit_log` row with entity `poster`; `PosterImpactQueries` names the poster under `deletes`; no endpoint attaches a poster or its image to an event, and nothing here reaches the site | contracts 4.5 Posters |
| `/admin/help*` | Editor for `GET`, Admin for `PUT` and `POST .../reset` (`help`) | list every `help_topic` row ordered by `page`, `key` (`collate "C"`) with the newest audit stamp; `PUT /admin/help/{key}` validates title 1 to 120, body 1 to 2000, links 0 to 6 of label 1 to 60 and `to` a `/` path or an `https://` URL (`HelpTopicSeed.IsValidLinkTarget`), trims, refuses em and en dashes with `validation_failed` on the field, then locks the row (`404` for an unknown key), sets the text, `edited_by` (the email claim or `key:<name>`), `edited_at = now()`; `POST /admin/help/{key}/reset` copies `default_*` back and nulls the stamp; both run through `AdminSnapshotTransaction.RunWithoutSnapshotAsync` (no snapshot rebuild) and audit (section 5a); `defaultChanged` is computed on read | contracts 4.5 Help; sql.md 3.32 |
| `/admin/beacons*` | Admin | create, patch, activate, deactivate, rotate, revoke, logs; list and get resolve `hubConnected` from one presence call (the gateway's `members[].identity`) and compute `healthy` (not revoked, not stale, seen at least once) | section 14 |
| `/admin/sponsors*` | Editor (`sponsors`) | CRUD, a year copied from another year (`copy-from`, `409 year_exists`), the bulk `import` between two years in one transaction, years upsert with `pinnedPosition` (`409 pinned_position_taken` from the partial unique index) and `lingerMsOverride`; `order/{eventYear}` reads and rewrites the pinned list for a year in one transaction; `logoMediaId` must name a `ready` asset (`409 media_not_ready`); every `SponsorYear` answered carries the computed `lingerMs` | [snapshot] |
| `/admin/api-keys*` | Admin, Cognito only (`DenyApiKeys`) | list, mint, revoke | section 6.4 |
| `/admin/audit*` | Admin (`audit`) | the log by entity and id, by action, by actor; the entity kinds | section 5a |
| `/admin/qr-codes*`, `/admin/places*` | Canvasser (`qr`); the two deletes Admin | codes: list, mint a batch, detail with history and daily counts, patch, attach, detach, delete; places: tree, create, patch (with the cycle check), location put and delete, delete, the map | section 11b |
| `/qr-codes/{tag}/scans` | none; rate limited like `/contact` | the scan beacon, always 204 | section 11b |
| `/me/alerts` | Person | the alert emails sent to the caller's subscriptions, newest first, 100 at most, each with `message`: the body of the `event_message` row the payload's `messageId` names (status and message alerts alike; null when none or deleted) (contracts 4.4) | |
| `/admin/cookie-types*` | Admin | list (with `cookieCount`), create, patch, delete with an `icon` value (library id checked against the library, media icon must be a `ready` media asset of any kind); `409 event_live` guard on every write, the delete included; a delete takes the type's cookies with it (sql.md 8.10) | [snapshot] |
| `/admin/pages*`, `/admin/sections*`, `/admin/items*` | Editor | working-set CRUD, order, move, duplicate; draft validation through `SchemaValidator`; `kind_not_allowed` from the registry's `allowedRoles` | sql.md 8.21 |
| `/admin/site-settings` | Editor | read and replace the single row; draft validation | sql.md 8.21 |
| `GET /admin/content/kinds` | Editor | the registry as `KindInfo[]` with schemas inlined | section 11a.1 |
| `GET /admin/content/status`, `GET /admin/content/draft` | Editor | build the draft document (11a.3), validate at the publish level, hash, compare with the newest version | |
| `POST /admin/content/publish` | Editor | section 11a.4 | sql.md 8.19; [snapshot] |
| `GET /admin/content/versions*`, `POST .../restore` | Editor | list, get, restore (11a.5) | sql.md 8.20 |
| `POST /admin/content/preview-token` | Editor | mint `wpv_`, insert the hash, answer the site URL | sql.md 8.23 |
| `GET /preview/document` | none (240/min burst 60 per client IP) | resolve the token, build the draft bundle (11a.6) | sql.md 8.23 |
| `/admin/media*` | Editor | list, ticket, confirm, get, usage, patch (`alt`, `title`, `darkMediaId`, `invertInDark`, `smallMediaId`, `credit`, [snapshot]), delete with every reference cleared | section 11.2 to 11.5 |
| `GET /admin/icons` | Editor | the library as `IconInfo[]` from `IconLibrary` | section 11a.7 |
| `/admin/settings*` | Admin | list with defaults; `PUT` validates type and range per contracts 6 | [snapshot] |
| `/admin/contact-messages*`, `/admin/subscribers*`, `/admin/people*` | Admin | paged lists, deletes, summary | |
| `GET /admin/email/quota` | Editor, Cognito only (`DenyApiKeys`) | one `GetAccount` reading through `EmailQuotaReader` (cached), two `CountAsync` queries (unsent alert deliveries, verified email subscribers), one `EmailQuota` (section 13); reads only, never audits | contracts 4.5 Email quota, 7.8 |
| `GET /admin/snapshot`, `POST /admin/snapshot/rebuild`, `GET /admin/live`, `POST /admin/live/republish` | Admin | diagnostics (section 10.3) | |
| `POST /realtime/authorize`, `POST /realtime/message` | none, guarded | section 12 | |

Paging: cursor is the base64url of the last row's `id` (descending lists) or `seq` (ascending); `limit` clamps to 500. The CSV export streams with `IAsyncEnumerable` and `Transfer-Encoding: chunked`.

Validation lives next to each endpoint in a static `Validate(dto)` returning the field map; every rule in the contracts' field tables is one line there, and the integration tests cover each line.

---

## 8. In-memory node state

`NodeState` is a singleton holding exactly the fields of contracts 7.1, replaced atomically as one immutable record per refresh (`Interlocked.Exchange`), so readers never see a half-updated state:

```csharp
public sealed record NodeSnapshot(
    long SnapshotVersion, string SnapshotUrl,
    CurrentEvent? CurrentEvent,              // id, statusId, finalCookieTally (non-null only while statusId is 4)
    long? ActiveBeaconId,
    PublishedLocation? LatestPublished,      // the location row fields
    IReadOnlyDictionary<long, int> CookieTally,
    Settings Settings,                        // the keys of contracts 6
    DateTimeOffset RefreshedAt);
```

Besides the snapshot: `Leader { IsLeader, EvaluatedAt }` (section 13), `LastWrittenVersion`, `WroteForLocationSinceVersionChange`, a per-node `TallyDelta` counter map that `POST /cookies` increments and the next tick folds into the refreshed tally (the tick's SQL count is the truth; the delta only bridges the second between insert and tick), and a `BeaconRateLimiter` map keyed by beacon id (contracts 7.2): `lastAcceptedAt`, the last accepted seq for the dropped-response body, `overrideMs` (mirrored from the row on the last write), a pending drop counter, and a per-beacon flush timestamp so `beacon.fixes_rate_limited` is written at most every 5 s per beacon.

`Refresh(reason)` runs the six statements of contracts 7.4 on the app connection in one round trip (a single batched command) and replaces the record. `Load` on boot is the same call. Every endpoint reads `NodeState.Current` once at the start of the request. `LatestPublished` comes from `event.latest_fix` when non-null (contracts 1.2, 7.2), and from the newest published `location` row for the current event only when `latest_fix` is null (the fallback for events that ran before the column existed).

---

## 9. Reconcile tick

`ReconcileTick` is a `BackgroundService` with `PeriodicTimer(WMSFO_RECONCILE_TICK_MS)`:

```
loop every tick:
  before = state.SnapshotVersion
  await state.Refresh("tick")            // settings re-read when version moved or 5 s since last settings read
  if state.SnapshotVersion != before and state.SnapshotVersion != LastWrittenVersion:
      if WroteForLocationSinceVersionChange:
          await liveObjectWriter.WriteFromState("tick-rewrite")   // once; clears the flag
      else: nothing
  elif this node is the leader and the event is live and state.CookieTally != the tally in the object this node last wrote:
      await liveObjectWriter.WriteFromState("tally")              // cookies reach the CDN within a tick, beacon or no beacon
```

A failed tick (database error) logs at Warning, keeps the previous state, and the next tick retries. The tick never publishes unless it rewrote the object. The tally, the current event, the active beacon, and the latest published location are re-read every tick regardless of version, so a cookie left on another node is in this node's tally within one tick; settings are re-read on a version change and at least every 5 s. The tally rewrite is the leader's alone (one writer per tally change across the fleet) and only while the event is live; a location write from a beacon carries the tally anyway, so the rewrite matters when the beacon is quiet.

---

## 10. Live object writer, snapshot builder, diagnostics

### 10.1 Live object writer

One `LiveObjectWriter` per node. Single-flight with coalescing: a `SemaphoreSlim(1)` guards the write; a caller that finds a write in flight sets a `pending` flag and returns; the writer loops while `pending` is set, each iteration building from the state that is current at that moment. Two entry points:

- `WriteForLocation(LocationTransactionResult r)`: called by the location handler after its response is queued. Builds the object from the fields the transaction read (event status, snapshot URL, the inserted row) plus the tally and settings from memory; one PUT attempt with a 3 s timeout; then publish; sets `WroteForLocationSinceVersionChange = true`.
- `WriteFromState(reason)`: called after an admin commit, after moderation while live, on the tick rewrite, on republish, and on first boot. Refreshes state first, then builds; up to three PUT attempts one second apart; then publish.

Build: `CdnObjects.LiveObject` in contract key order, `publishedAt = now`, `onlineCount` per contracts 1.2 and 7.4 (null unless the event's status is 3 and `hub_enabled` is true; otherwise `GatewayInternalClient.GetPresenceCountAsync("<service>:location")` through a one-second per-node cache, awaited for at most 250 ms, null on any failure, never failing the write), `cookieTally` from memory while the event's status is not 4 and from `event.final_cookie_tally` while it is (keys in ascending numeric order), serialized with `CanonicalJson.Options`. PUT: `Key = "live/location.json"`, `ContentType = "application/json; charset=utf-8"`, `CacheControl = "s-maxage=1, max-age=0"`. Publish: `GatewayInternalClient.PublishAsync("<service>:location", "location", bytes)` always, whether or not the PUT succeeded. Then `update live_state set ...` per contracts 1.8. Failures log at Warning with marker `wmsfo_live_put_failed` or `wmsfo_publish_failed`.

### 10.2 Snapshot builder

Runs inside the admin transaction (contracts 7.3) on the transaction's connection:

1. `select * from snapshot where id = 1 for update`.
2. Apply the write (the endpoint's own statements).
3. Run the snapshot reads of sql.md 7 in the same transaction; build `CdnObjects.Snapshot` in contract key order with the sponsor filter and ordering of contracts 1.3 (pinned rows first, then amount desc, name, id), `lingerMs` from `linger_ms_override` when the row has one and otherwise from the settings read in the transaction, `event.latestMessage` from one read of the event's newest message (`order by created_at desc, id desc limit 1`; null when there is none), `event.flightHistory` from the linked `route` row's object thinned to `flight_history_max_points` by keeping every `ceil(n / max)`-th point starting from the first and always the last, and `event.routeMap` from the same object through `Node/RouteMapBuilder.cs` (Douglas-Peucker at `route_map_simplify_tolerance_m`, two Chaikin passes, even thinning to `route_map_max_points`, the 5 minute timeline of contracts 1.3; every raw point's time rides through simplification and smoothing with the position, so the timeline is read off the drawn path). The builder reads the route object from the object store inside the transaction; there is no `route_point` cache table. A route is at most 50,000 points (contracts 1.4, 5 MB cap), so one `GetObject` inside the transaction is cheap. `event.trackerBbox` from the event row and `event.trackerMap` from one read of the linked `ready` `tracker_map` row (sql.md 7; `tilesUrl` and `terrainUrl` from its `prefix`, `terrainUrl` null without `terrain_max_zoom`), `trackerThemes` from the event's `event_tracker_theme` join in `sort_order`, `id` order (`styleUrl` from `style_sha256`, `spriteUrl` from `sprite`; both renderers; empty when no event is current). `content` is the newest `content_version.document` parsed and re-serialized through the same options (byte-identical by construction); `media` is the referenced assets (the version's `media_ids`, the listed sponsors' logos, the cookie types' media icons, and the enabled themes' `thumbnail_media_id` values, `CollectSnapshotLevelMediaIdsAsync`) with keys in ascending order; `icons` is `IconLibrary.Map` (id to CDN URL, ascending).
4. `bytes = CanonicalJson.Serialize(snapshot)`, `key = "snapshots/" + sha256hex + ".json"`.
5. `objectStore.PutAsync(key, bytes, immutable)` with a 3 s timeout and one attempt; any failure throws `ApiException(502, "snapshot_write_failed")`, which rolls the transaction back.
6. `update snapshot set version = version + 1, url = $cdn + '/' + key, s3_key = key, built_at = now() where id = 1`.
7. Commit. After commit: `LastWrittenVersion = version`, `liveObjectWriter.WriteFromState("admin")` fire-and-forget (the response does not wait).

First boot (`SnapshotBootstrap.EnsureVersionOneAsync`): the same steps with no write applied, executed under the migration advisory lock; a PUT failure releases the lock and retries every 5 s while health stays `503`.

### 10.3 Diagnostics

`GET /admin/live` returns the `live_state` row plus this node's `NodeState.Current`, `Leader`, and `GatewayInternalClient.LastInstanceId`. `POST /admin/live/republish` runs `WriteFromState("republish")` and returns the object it wrote. `POST /admin/snapshot/rebuild` runs the snapshot transaction with no write applied.

---

## 11. Uploads and media

### 11.1 Routes

`POST /admin/routes`: read the body (5 MB limit is enforced first), deserialize into `RouteUpload { name, points[] }` with unknown keys rejected, validate per contracts 1.4, build `CdnObjects.Route` (adds `schemaVersion: 1`), canonicalize, hash, `select ... from route where s3_key = $key` (found: `200` that row), PUT with the immutable header (3 s, one attempt, else `502 route_write_failed`), insert; on `23505` on `s3_key` re-read and answer `200`. `POST /admin/routes/from-event/{eventId}` streams `select lat, lng, recorded_at from location where event_id = $id and published order by seq` into the same `RouteUpload` shape (`404` for an unknown event, `400` under 2 points, `413` over 50,000) and continues identically. `DELETE` deletes the row (the foreign keys unlink the events and the posters that used it, section 5b), then the object (a failed delete of the object is logged; the row is already gone).

### 11.2 Media tickets and presigned uploads

`POST /admin/media/upload-url`: validate the body (contracts 4.5 Media); sanitize the filename (`[^A-Za-z0-9._-]` to `-`, collapse repeats, trim to 100, and make sure the extension matches the declared type: `png`, `jpg` or `jpeg`, `webp`, `gif`, `svg`); mint a UUID; insert the `pending` row (sql.md 8.22) with `s3_key = media/{id}/{filename}`; presign a `PUT` for that key with `Expires = 15 minutes`, `ContentType` set to the declared type, and the header `x-amz-tagging: state=pending` included in the signature; answer `UploadTicket` with `headers = { "Content-Type": <type>, "x-amz-tagging": "state=pending" }`. The browser must send exactly those headers or S3 rejects the signature. The bucket's CORS rule (platform.md 1.4) allows the PUT from the admin origins. Limits by type: raster and gif 20 MB, svg 1 MB, enforced on `sizeBytes` here and on the real object at confirm.

### 11.3 Confirm and variants

`POST /admin/media/{id}/confirm` (`MediaConfirm.RunAsync`):

1. Read the row; `404` when missing, `409 media_not_pending` unless `state = 'pending'`.
2. `HeadObject`; missing: `404 upload_not_found`. Size over the type's limit: delete the object and the row, `413`.
3. `GetObject` into memory (at most 20 MB). `ImageSniffer` decides png, jpeg, webp, gif, or svg from the bytes; a mismatch with `content_type`: delete both, `400 validation_failed` on `file`. SVG runs `SvgValidator` (11.4); failure deletes both, `400`.
4. Raster (png, jpeg, webp): ImageSharp decode with a 40-megapixel ceiling (`400` beyond it), record width and height; for each of 480, 960, 1600 that is less than the width, resize to that width (aspect kept), encode WebP quality 82, PUT `media/{id}/w{width}.webp` with the immutable header and the same pending tag (3 s, one attempt each; failure: `502 media_write_failed`, row stays pending, the panel retries confirm). GIF: width and height from the decoder, no variants. SVG: width and height null, no variants.
5. Tile pyramid (`DeepZoomTiler`), raster only, when `max(width, height) >= 2048`: from the decoded image build the Deep Zoom pyramid with tile size 254 and overlap 1 (the format's defaults, which OpenSeadragon reads without configuration): level `n = ceil(log2(max(width, height)))` is the full image, each lower level halves both dimensions (rounding up) down to level 0 at 1 by 1; every level is cut into tiles `{col}_{row}.png` of 254 px plus the 1 px overlap on inner edges, lossless PNG (ImageSharp `PngEncoder` with `PngColorType.Rgb` or `Rgba` as the source has alpha, compression level 6; no chroma subsampling, no quantization: the top level is the poster's own pixels), PUT under `media/{id}/dzi/poster_files/{level}/` with the immutable header and the pending tag; then PUT the descriptor `media/{id}/dzi/poster.dzi` (`<Image TileSize="254" Overlap="1" Format="png" xmlns="http://schemas.microsoft.com/deepzoom/2008"><Size Width=".." Height=".."/></Image>`, `Content-Type: application/xml`). Lower levels are resized with the Lanczos3 resampler. The tiles are PUT concurrently, eight at a time, 3 s each; a 40-megapixel poster is about 1,000 tiles, three to four times the bytes of a JPEG pyramid, and a few seconds. A pyramid cut before this change stays as it is: its descriptor says `Format="jpg"` and the viewer follows the descriptor. Any failure: `502 media_write_failed`, row stays pending, confirm retried. Record `dzi_key`.
6. `sha256` of the original bytes. `DeleteObjectTagging` on the original, every variant, the descriptor, and every tile (removes the pending tag so the lifecycle rule ignores them; the tile untagging runs concurrently like the PUTs). Update the row to `ready` (sql.md 8.22). Answer `MediaAsset` with `dziUrl` when a pyramid was cut.

Confirm is idempotent while the row is pending: a retry after a step 4 or 5 failure re-derives and re-PUTs (same bytes, same keys). No snapshot rebuild: an asset reaches the site only when something published references it. `DELETE /admin/media/{id}` removes the pyramid with everything else under `media/{id}/` (11.5).

### 11.4 SVG validation

`XmlReader` with `DtdProcessing.Prohibit`, `XmlResolver = null`, and a 1 MB character limit. Walk every element and attribute; reject with `400 validation_failed` on field `file` when: any element local name is `script` or `foreignObject`; any attribute name starts with `on` (case-insensitive); any `href` or `xlink:href` value, trimmed, starts with `http:`, `https:`, or `javascript:` (case-insensitive); the root is not `svg`. The stored bytes are the uploaded bytes, not a re-serialization. The same validator runs over every file in `icons/` in a unit test, so a library icon can never fail it at boot.

### 11.5 Usage, patch, delete, orphans

`MediaUsage.ForAsync(id)` runs the seven usage statements of sql.md 8.22 (the tracker themes whose thumbnail the asset is come back as `themes`) and returns `MediaUsage`. `DELETE /admin/media/{id}` never refuses for a reference: in one transaction it clears every reference (section 5b: the content JSON of sections, items, and site settings, page icons, cookie type icons, and the `media_ids` of content versions; sponsor logos, theme thumbnails, dark versions, and small versions go null through their foreign keys), deletes the row, then `ListObjectsV2` under `media/{id}/` and `DeleteObjects`. `PATCH` (alt, title, darkMediaId, invertInDark, smallMediaId, credit) runs the [snapshot] frame because `alt`, the dark version, `invertInDark`, the small version, and `credit` ride in the snapshot's media map; `darkMediaId` and `smallMediaId` must each name another `ready` asset (`404`, `409 media_not_ready`, `400` for the asset itself). `MediaOrphanCollector` (section 13) is the only other writer of media state.

### 11.6 Maps

`AdminMapEndpoints` (contracts 4.5 Maps), Admin policy with `.RequireCapability("maps")`. A map package is built and uploaded by the tile CLI (platform.md 1.8); the API starts and signs the upload, verifies the result, and never touches an archive's bytes beyond its header.

**Object store.** `IObjectStore` gains `StartMultipartAsync(key, contentType, cacheControl)` (answers the upload id), `PresignUploadPart(key, uploadId, partNumber, expires)`, `CompleteMultipartAsync(key, uploadId, parts)`, `AbortMultipartAsync(key, uploadId)`, `ListMultipartUploadsAsync(prefix)` (open uploads by key), and `GetObjectRangeAsync(key, from, to)`. `S3ObjectStore` maps them to `InitiateMultipartUpload` (with the content type and the immutable cache header, which the completed object carries), `GetPreSignedURL` with `Verb = PUT`, `UploadId`, and `PartNumber`, `CompleteMultipartUpload`, `AbortMultipartUpload`, `ListMultipartUploads` with `Prefix`, and `GetObject` with a `ByteRange`. `LocalObjectStore` keeps parts as files under `<dir>/.multipart/<uploadId>/<partNumber>`, answers part URLs as `PUT /local-upload/parts/{uploadId}/{partNumber}` on the API itself (the same route family and guard as `/local-upload/{id}`, section 20), concatenates the parts in order on complete, and reads the range from the file. The existing `GetObjectAsync` materializes a body and stays documented for small objects; nothing here calls it on an archive. Presigned `PUT` for the sprite keys of 11a.10 goes through the existing `PresignPut`.

**Create.** `Validate(dto)` per the contracts' field rules (`bbox` against `$defs/Bbox` plus the 20 degree side cap, the zoom ranges, `terrainMaxZoom` null or 8 to 13, `sourceBuild` a date); `packageKey` is `sha256` of `CanonicalJson.Serialize(new { bbox, minZoom, maxZoom, terrainMaxZoom })`. One transaction: `select id, state from tracker_map where package_key = $key for update`; `ready` is `409 package_exists`; `pending` is reused; otherwise insert the row (`prefix = 'maps/' + key`, `state = 'pending'`, `created_by = updated_by` the actor) and record `create`. Then, outside the transaction, resolve the open uploads: `ListMultipartUploadsAsync(prefix)`, and `StartMultipartAsync` for each declared archive (`tiles.pmtiles`, and `terrain.pmtiles` when `terrainMaxZoom` is set) that has none. The upload ids are not stored (sql.md 3.33); every later call resolves them the same way, which is what makes a re-run of the CLI resume. Answer `TrackerMapUpload` (`201`, or `200` for a reused row).

**Parts and complete.** Load the row (`404`); `ready` is `409 package_exists`; `file` must be an archive the row declares (`terrain` only with `terrainMaxZoom`), else `400 validation_failed` at `file`. Parts: resolve the archive's open upload (start one when none is open), presign each requested part for 15 minutes, answer `TrackerMapParts`, record `parts`. Complete: resolve the upload, `CompleteMultipartAsync` with the listed parts; an S3 refusal (a missing part, a wrong etag) is `400 validation_failed` at `parts`; record `complete`. Both run through `AdminSnapshotTransaction.RunWithoutSnapshotAsync`.

**Confirm.** For each declared archive: `HeadObject` (missing: `404 upload_not_found` with `details.file`) for the length, then `GetObjectRangeAsync(key, 0, 126)`: the 127-byte PMTiles v3 header. Check the magic `PMTiles` and version 3; read the min and max zoom and the bounds (E7 integers); the archive's bounds must contain the row's `bbox` and its zooms must equal the row's (`min_zoom` and `max_zoom` for `tiles`; 0 and `terrain_max_zoom` for `terrain`, whose minimum zoom is not checked). A failed check is `409 package_invalid` with `details.field` (`tiles` or `terrain`) and `details.reason` (`magic`, `version`, `bounds`, `minZoom`, `maxZoom`), the row left `pending`. Then PUT `maps/{packageKey}/manifest.json` (the `TrackerMap` DTO through `CanonicalJson`, immutable header, 3 s, one attempt; failure `502 upstream_failed`, row left `pending`, confirm retried), and in one transaction `update tracker_map set state = 'ready', built_at = now(), tiles_bytes = $t, terrain_bytes = $tr, updated_by = $actor, updated_at = now() where id = $id and state = 'pending'` and record `confirm`. A `ready` row answers `200` with nothing written. Never snapshot-affecting: no event references a pending map.

**Patch, impact, delete.** `PATCH` sets `name` and the `updated_*` stamp through `AdminSnapshotTransaction.RunAsync` when the current event's `tracker_map_id` is the row (the snapshot carries the name) and `RunWithoutSnapshotAsync` otherwise. `TrackerMapImpactQueries.PreviewAsync`: `unlinks` one group `event` (`select id, name from event where tracker_map_id = $id order by id`, the count exact, ten names); `warnings` "<n> events lose their map; their viewers get Google Maps at the next snapshot." plus "<event name> is the current event; its viewers move to Google Maps at the next snapshot." when it is among them, and "<event name> is live." when that event is live; `blocked` null. `ApplyAsync(conn, tx, id, replacementId)`: with a replacement, `select bbox, state from tracker_map where id = $r` (missing: `404`; not `ready` or the row itself: `400 validation_failed` at `replacementId`), then for every referencing event check containment (`west <= event.west and south <= event.south and east >= event.east and north >= event.north` on the JSON numbers; a miss is `400 validation_failed` at `replacementId` with `details.eventId`), then `update event set tracker_map_id = $r, updated_at = now() where tracker_map_id = $id`; without one, the same update to null; then `delete from tracker_map where id = $id`. The frame is the [snapshot] one when the current event is among the referencing events, else the plain one; the audit row is `delete` with `before = { ...dto, impact }`. After commit: `ListMultipartUploadsAsync` and abort each, then `ListObjectsV2` under the prefix and `DeleteObjects`, skipped entirely when the prefix is `basemap` (the seeded map's objects stay); a failed object delete is logged, the row is already gone.

**Event writes** (`AdminEventEndpoints`): the containment checks of `trackerMapId` and `trackerBbox` and the Google rule of `trackerThemeIds` run inside the event's [snapshot] transaction against the locked rows (sql.md 8.7 and 8.4b for create and clone), each failure `400 validation_failed` at its path.

## 11a. Content

### 11a.1 Kind registry and schemas

`KindRegistry` loads `contracts/kinds.json` (kind, title, description, live, hasItems, allowedRoles, defaults, itemDefaults) and, per kind, `contracts/schema/sections/<kind>.schema.json` and, when `hasItems`, `<kind>.item.schema.json`; plus `primitives.schema.json`, `site-settings.schema.json`, and `content-document.schema.json`. Every schema is compiled once with `JsonSchema.Net` with a resolver that serves the primitives by `$ref`. `GET /admin/content/kinds` returns each kind with its schemas as JSON with the referenced primitives inlined under `$defs`, so the panel's form generator needs no resolver. Adding a kind to the API is one line in `kinds.json` and one schema file; the site and the panel pick it up from the contracts copy.

### 11a.2 Validation

`SchemaValidator.Validate(schema, instance, level)` returns `Problem[]` with JSON-pointer paths. `Draft` uses the derived lenient schema (the publish schema with `required`, `minLength`, `minItems`, and `minimum` removed at every level, computed once at boot); unknown properties are still rejected (`additionalProperties: false` on every object in every contract schema). `Publish` uses the full schema, then `ReferenceChecker` adds the semantic problems of contracts 1.3a: it walks the instance for every `MediaRef`, media-sourced `Icon`, library `Icon`, `Link`, and `Inline` value (the walker knows the primitive shapes, not the kinds, so a new kind needs no walker change), resolves media ids in one `select id, state, kind from media_asset where id = any($ids)`, checks library ids against `IconLibrary`, parses every `Inline` with `InlineText` to extract inline links and icons and to reject malformed markup, checks hrefs (absolute http or https, `mailto:`, or `/<slug>` with an optional `#anchor` naming a non-hidden page), checks anchors for uniqueness per page, and refuses a `map` section outside the `live` page. The site settings' `landmarks` (`$defs/Landmark[]`, 0 to 50) follow the `headerLinks` recipe: the draft schema enforces the shape, the maximums, and the fifty entry ceiling on every `PUT /admin/site-settings`, and publish adds the required keys and the minimums; the walker treats a landmark's `name` and `description` as plain text and checks only its `icon` (a library id in `IconLibrary`, a media id naming a `ready` asset), reported at `/settings/landmarks/<i>/icon/id`.

`InlineText.Parse` is the reference grammar for the site's renderer: tokens `**`, `*`, backtick, `[label](href)`, `{icon:<id>}`, `{icon:media:<uuid>}`, `{event:name|year|scheduledAt}`, newline; anything else is text. Unbalanced markers are text, not errors; a malformed `{...}` construct is a publish-level problem.

### 11a.3 Document builder

`DocumentBuilder.BuildAsync(connection)` reads the working set in document order (sql.md 8.19), omits hidden pages, sections, and items, and produces `ContentDocument` DTOs in contract key order plus the set of referenced media ids (collected by the same walker as 11a.2, so every landmark media icon in `settings.landmarks` lands in `content_version.media_ids` and the snapshot's `media`). `GET /admin/content/draft` and the preview endpoint serialize this through `CanonicalJson`; the publish transaction hashes exactly those bytes.

### 11a.4 Publish

`Publisher.PublishAsync(label, email)` is the [snapshot] frame with the statements of sql.md 8.19: lock the snapshot row, build, validate at the publish level (problems: rollback, `422 content_invalid` with `details.problems` as `ProblemRef[]` carrying page, section, and item ids), hash, compare with the newest version (`409 content_unchanged`), insert the version with its media ids, prune to 50, rebuild the snapshot (which now embeds this document), PUT, update the snapshot row, commit, then `liveObjectWriter.WriteFromState("publish")`. `EnsureVersionOneAsync` on first boot is the same code path with `published_by = 'seed'` and without the snapshot steps (sql.md 8.16 does them next).

### 11a.5 Restore

`Restorer.RestoreAsync(versionId, email)` runs sql.md 8.20 in one transaction: delete every page (cascade) except a role page whose role the document has no page for, reinsert pages, sections, and items from the version's document with new ids and `updated_by = email`, replace the site settings draft, stripping from every section's `data` any key the kind's current schema no longer allows (a `map` section's `themes` and `defaultTheme` from a pre-A<task> version), so a restore can never reinstate a key the next publish would refuse with `content_invalid`. It answers `GET /admin/content/status` computed afterwards. A published document holds one page per role it knows and the kept pages cover the rest, so the seven role pages remain; the partial unique index is satisfied because the delete precedes the inserts in the same transaction.

### 11a.6 Preview

`POST /admin/content/preview-token` mints `wpv_` + 43 base64url characters, inserts `sha256(token)` with an expiry of the optional body's `ttlMinutes` (15 to 1440, default 15, out of range `400 validation_failed` on `ttlMinutes`), and answers `{ token, url: WMSFO_SITE_BASE_URL + "/preview?token=" + token, expiresAt }`. `GET /preview/document?token=` (public, IP-limited, no `serverTime`) looks the hash up, then answers `ContentBundle`: the draft document (11a.3, no validation), the media map, and the icon map. The media map holds every referenced asset whatever its state (a pending reference is a missing image in the preview, which is the point) plus the snapshot-level media the snapshot builder adds to its own map (`SnapshotBuilder.CollectSnapshotLevelMediaIdsAsync`: logos of the sponsors the snapshot lists, media icons of active cookie types), because the site takes sponsors and cookie types from the snapshot but the whole media map from this bundle; `GET /admin/content/draft` builds its media map the same way. `Cache-Control: no-store`; `ETag` is the quoted sha256 hex of the body bytes, and a matching `If-None-Match` answers `304` with no body. The site's `/preview` route is the only consumer.

### 11a.7 Icon library

`IconLibrary` loads `icons/library.json` and every `icons/<id>.svg` at boot, validates each with `SvgValidator` (a failure fails the boot; the unit test catches it first), computes each file's sha256 and the library hash (sha256 over the sorted `id:sha256` lines), and exposes `Map` (id to `WMSFO_CDN_BASE_URL/icons/{sha256}.svg`) and `Infos` for `GET /admin/icons`. `EnsureWrittenAsync` (under the migration lock, sql.md 8.16 step 2) compares the hash with `icon_library_state.library_sha256`, PUTs every icon with the immutable header when they differ (an icon whose bytes did not change lands on the same key), updates the row, and reports whether it wrote so the migrator runs a snapshot rebuild afterwards. Library ids match `^[a-z0-9]+(-[a-z0-9]+)*$`; removing an icon from the library is a breaking change for any published document that uses it, so the unit test that guards `library.json` fails when an id disappears unless the change is marked deliberate in the test.

### 11a.8 Starter content

`StarterContent.EnsureSeededAsync` (sql.md 8.16 step 1) inserts `contracts/starter-content.json` when `page` is empty: the seven role pages with a section stack that makes sense for each status (for example the live page with a `map` section and a `leaderboard`; the ended page with `event_times`, a `leaderboard` in the `full` variant, a `sponsor_grid`, and a `latest_message`; the postponed page, titled `Postponed`, with the cancelled page's stack under a hero reading `Santa's flight is postponed` and `A new time will be announced here and by email.`), the ordinary pages (`about`, `sponsors`, `route`, `donate`, `contact`, `alerts`), and the site settings. It references library icons only and no media, so it publishes cleanly on an empty bucket; its `route_preview` sections carry `heading`, `disclaimer`, and `emptyText` only, the section always drawing the route map. A contract test publishes it against the schemas at the publish level.

### 11a.9 Help topics

`HelpTopicSeed.Load(root)` reads `help/topics.json`, found like `icons/`: `HelpTopicSeed.ResolveRoot` tries the binary's directory and then the content root, each with up to seven parents, for a `help/topics.json` (the image carries it at `/app/help`). The file is a JSON array of `{ key, page, label, title, body, links: [ { label, to } ] }`, an unknown field refused. `Program.cs` loads it in step 3 and the boot fails, with a message naming the file and the entry, on a duplicate key; a key not matching `^[a-z0-9-]+(\.[a-z0-9-]+)*$`; an empty page, label, title, or body; a title over 120 or a body over 2000 characters, or a link label outside 1 to 60 (the `PUT` limits, so a seed text can always be saved back unchanged); more than 6 links; a link `to` that is neither a path starting with one `/` nor an `https://` URL with a host; or an em dash or en dash in any string. Strings are trimmed. `HelpTopics.EnsureWrittenAsync(conn)` (under the migration lock, sql.md 8.16 step 2a) upserts every entry in one transaction and deletes the rows whose key the file no longer lists, and the hook logs one line with the inserted, updated, unchanged, and deleted counts. `export-contracts` writes `contracts/help-keys.json` from the same loader (`KeysToCanonicalJson`: `{ key, page, label }` per entry in file order, canonical JSON), and a unit test fails when the checked-in copy differs.

### 11a.10 Themes

`AdminThemeEndpoints` (contracts 4.5 Themes), Admin policy with `.RequireCapability("themes")`. A theme row carries what the snapshot needs inline; its style body is an immutable CDN object the API writes once per distinct body.

**Create and patch.** The body is JSON, or `multipart/form-data` whose `style` part is parsed as JSON (an unparsable part is `400 validation_failed` at `style`). `Validate(dto)` covers `key` (the slug regex), `name`, `sortOrder`, and `thumbnailMediaId` (a `ready` raster asset: `404`, `409 media_not_ready`). `ThemeStyleValidator.Validate(renderer, style)` returns the reason strings of the contracts' **Style rules**: the canonical size cap (64 KB `google`, 512 KB `maplibre`), the `google` array shape (objects with only `featureType`, `elementType`, `stylers`), the `maplibre` shape (`version` 8; `sources` exactly `basemap` of type `vector` and optionally `terrain` of type `raster-dem`, neither with `url` or `tiles`; every layer with a `source` (every type but `background`) names one of the two; no string starting with `http`), and rewrites `glyphs` to `WMSFO_CDN_BASE_URL/basemap/glyphs/{fontstack}/{range}.pbf` and `sprite` to the theme's sprite prefix or removes it while `sprite` is false (so a sprite confirm or a sprite loss rewrites the body: a new object, a new `style_sha256`, the old object left). `ChromeContrast.Ratio(fg, bg)` composites `#rrggbbaa` over the other colour, computes WCAG relative luminance, and requires 4.5 or better for `chrome.text` on `chrome.bg` and `chrome.tileFg` on `chrome.tile` (`400 validation_failed` at `chrome.text` or `chrome.tileFg`); every colour and opacity key is required and nothing else is allowed. The accepted style is serialized with `CanonicalJson.SerializeOpaqueToUtf8Bytes` (the poster layout path: ordinal key order, no whitespace), hashed, and, when a `HEAD` on `themes/{sha}.json` finds the key absent, PUT with the immutable header (3 s, one attempt; failure `502 upstream_failed` before any row is written). Then one transaction: insert (or update, `updated_by` and `updated_at` stamped) with `style_sha256` and `style_bytes`; a `23505` on `tracker_theme_renderer_key_key` is `400 validation_failed` at `key` (sql.md 4.3); record `create` or `update`. Create runs `RunWithoutSnapshotAsync`; patch runs `RunAsync` when `exists (select 1 from event_tracker_theme et join event e on e.id = et.event_id where e.is_current and et.theme_id = $id)` and `RunWithoutSnapshotAsync` otherwise, the rule every theme write below applies.

**Sprite.** `POST .../sprite` (a `google` theme: `400 validation_failed` at `renderer`; `indexSha256` not 64 lowercase hex characters: `400` at `indexSha256`) answers four tickets from `PresignPut` exactly as a media ticket is made (11.2): keys `themes/{id}/sprites/{indexSha256}/sprite.json`, `sprite.png`, `sprite@2x.json`, `sprite@2x.png`, the content type (`application/json; charset=utf-8` or `image/png`) and `x-amz-tagging: state=pending` signed, 15 minutes; the bucket's CORS rule covers the PUT (platform.md 1.4) and the pending tag lets the lifecycle rule expire a set that is never confirmed. The panel computes `indexSha256` over the canonical bytes of the `sprite.json` it will upload (`CanonicalJson`, contracts 1.6), so the prefix is content-addressed and nothing under `themes/` is ever overwritten. `POST .../sprite/confirm { indexSha256 }`: `HeadObject` each under that prefix (missing: `404 upload_not_found` with `details.file`), `GetObject` each (the JSON files at most 1 MB and parsing to an object, the PNG files at most 8 MB and starting with the eight-byte PNG signature; a failure deletes the four objects and is `400 validation_failed` at `file` with `details.file`), canonicalize `sprite.json` and compare its hash with `indexSha256` (a mismatch is `400 validation_failed` at `indexSha256`, the four objects deleted), `DeleteObjectTagging` each, then in one transaction set `sprite_sha256 = $indexSha256`, rewrite the style body with the new sprite base (a new style object as above; the old style object and the previous sprite prefix stay), stamp, record `sprite`. Idempotent on a confirmed set.

**Default.** `POST .../default` runs the clear-then-set pair of sql.md 4.2 per flag given, on the theme's renderer, in one transaction; the frame is [snapshot] when the theme or the flag's previous holder is enabled on the current event; record `default`.

**Impact and delete.** `TrackerThemeImpactQueries.PreviewAsync`: `unlinks` one group `event` (`select e.id, e.name from event e join event_tracker_theme et on et.event_id = e.id where et.theme_id = $id order by e.id`); `warnings` "The <renderer> renderer loses its default <light|dark> theme." per flag the theme carries; for a `google` theme, `blocked` from `select e.name from event e where exists (select 1 from event_tracker_theme et where et.event_id = e.id and et.theme_id = $id) and not exists (select 1 from event_tracker_theme et2 join tracker_theme t on t.id = et2.theme_id where et2.event_id = e.id and t.renderer = 'google' and t.id <> $id) order by e.year desc limit 1`. `ApplyAsync(conn, tx, id, replacementId)`: a replacement must exist (`404`), be another theme of the same renderer (`400 validation_failed` at `replacementId`); the delete answers `409 last_google_theme` when the preview is blocked and no `google` replacement is given. With a replacement: `insert into event_tracker_theme (event_id, theme_id) select event_id, $r from event_tracker_theme where theme_id = $id on conflict do nothing`, then each default flag the theme carries moves to the replacement through the 4.2 pair; then `delete from tracker_theme where id = $id` (the join rows cascade). The frame is [snapshot] when the current event enables the theme or the replacement; the audit row is `delete` with `before = { ...dto, impact }`. After commit, `ListObjectsV2` under `themes/{id}/` and `DeleteObjects`; the style object stays (content-addressed, possibly shared with another theme).

**Audit stamp.** Every row carries `created_by`, `created_at`, `updated_by`, `updated_at`, and the lists and details add the lateral `AuditStamp` subquery of 5a, so the panel's audit cell works unchanged.

**Seeded style objects.** `ThemeStyles` loads `contracts/fixtures/themes/<key>.json` at boot, the eight bodies the migration seeded (`route-light`, `route-dark`, and the six Google keys), canonicalizes and hashes each, and fails the boot when a fixture's hash differs from the seeded row's `style_sha256` (a fixture edit is a migration, never a silent rewrite). The migration's seed values (contracts 13 `fixtures/themes/seed.json`) are hardcoded in the migration; a unit test holds them equal to the file and each fixture's canonical hash equal to its `styleSha256`. `ThemeStyles.EnsureWrittenAsync` (under the migration lock, sql.md 8.16 step 2b, right after `IconLibrary.EnsureWrittenAsync`) `HEAD`s `themes/{style_sha256}.json` for every `tracker_theme` row and PUTs the body it holds when the key is absent (immutable header, 3 s, one attempt; a failure aborts the step and the boot retries), then stamps `tracker_theme_state.written_at` when anything was written. It writes only what is missing, so it is idempotent. The row stores the hash, not the body, so only the eight seeded bodies can be rewritten: a missing object of a theme created through the API is logged at Warning (marker `wmsfo_theme_style_missing`) and restored only by a style replace in the panel.

---

## 11b. QR codes and places

`QrEndpoints` and `PlaceEndpoints` under the `Canvasser` policy (`admin`, `editor`, or `canvasser` in `cognito:groups`; API keys with `qr`). Tags mint as `qr-` plus the next number zero-padded to three digits (`qr-1000` after `qr-999`), one batch per `POST`, in a transaction that locks `qr_code` against concurrent mints. Attach closes the open attachment and opens the new one, then `update qr_scan set attachment_id = $new where qr_code_id = $code and attachment_id is null and at > now() - interval '1 hour'` (the early scans of contracts 4.5a). Resolution of `opens` and pins walks the ancestor chain in SQL with a recursive CTE, at most 32 levels; the place tree is validated for cycles on `parentId` changes by walking up from the new parent. `GET /admin/places/map` aggregates `qr_scan` rows with `is_bot = false and is_repeat = false` through their attachment's place and its ancestors.

The snapshot builder (10.2) adds `qrCodes`: every active code with its resolved `pageSlug` or `forwardUrl`; codes and places are read in the builder's transaction like sponsors are.

`POST /qr-codes/{tag}/scans` records the row with `ip_hash = sha256(WMSFO_SCAN_SALT || client IP)`, `is_bot` from a fixed marker list on the user agent (`bot`, `crawler`, `spider`, `preview`, `facebookexternalhit`, `Slackbot`, `WhatsApp`, `Twitterbot`, `LinkedInBot`, `HeadlessChrome`), `is_repeat` when a row with the same code, `ip_hash`, and user agent exists within ten seconds, and `event_id` from the current event; unknown or inactive tags are dropped silently. Always `204`; the rate limit is the contact form's.

## 12. Gateway callbacks and internal client

### 12.1 Guard

`Endpoints/Realtime.cs` registers both callbacks with a filter that runs before authentication; the forwarded-headers middleware is not mounted on `/realtime/*` (section 5), so the filter inspects the raw `X-Forwarded-For`, `X-Forwarded-Host`, and `X-Forwarded-Proto` headers and answers `404` with an empty body when any is present. The gateway's direct calls carry none. Both endpoints are exempt from CORS, rate limiting, and the `serverTime` filter.

### 12.2 `/realtime/authorize`

Deserialize `{ channel, credential, connectionId }` with extension data allowed. Apply contracts 2.4 in order. The public-topic branch returns before touching the database and logs at Debug. The ingest branch: regex check, one indexed lookup by hash, `revoked_at` check, `update beacon set last_seen_at = now()`, respond `{ allow: true, identity: "<id>:<keyVersion>" }`. Every response is `200` with a JSON body; the handler's budget is 2 s and the only I/O is the lookup and the stamp.

### 12.3 `/realtime/message`

Deserialize `{ channel, event, data, connectionId, identity }`. Apply contracts 2.5 in order: channel check (`403 forbidden`, no I/O), identity parse and beacon check including `key_version` (`403`), then, for `event === "location"` only, dispatch `data` to the same validate-and-store code the REST handler uses (`LocationIngest.HandleAsync`), returning the same body and codes; any other event is `400 validation_failed`. Heartbeats never arrive here. The gateway reads only the status.

### 12.4 `GatewayInternalClient`

One `HttpClient` with base address `WMSFO_GATEWAY_INTERNAL_URL`, header `X-Gateway-Realtime-Token` from `GATEWAY_REALTIME_TOKEN`:

| Call | Timeout | Failure handling |
|---|---|---|
| `POST /internal/publish { channel, event, payload }` | 2 s | logged with the status, never retried, never throws to callers |
| `GET /internal/leader` | 1 s | any failure is "follower" (section 13) |
| `GET /internal/presence/<service>:ingest` | 1 s | `hubConnected` on the beacons responses; failure returns `null` |
| `GET /internal/presence/<service>:location/count` | 1 s | `onlineCount` on the live object (section 10.1); failure, a non-2xx, or no integer `count` returns `null` |

The `payload` for a publish is the exact bytes the writer PUT, passed as raw JSON (`JsonSerializer.SerializeToUtf8Bytes` is not re-run; the body is assembled with the bytes spliced in) so the hub and the CDN carry identical bytes. When `GATEWAY_REALTIME_TOKEN` is absent (local runs) every call is a no-op that logs once at startup.

---

## 13. Leadership and chores

`LeaderMonitor` polls `GET /internal/leader` every 2 s (1 s timeout) and stores `Leader { IsLeader, EvaluatedAt, InstanceId }`. `IsLeader` is true only when the latest answer was `2xx`, `isLeader` was true, and `evaluatedAt` is non-null and under 90 s old at evaluation time (the gateway refreshes it on its 30 s reconcile loop); the value expires on its own 90 s after the answer, so a stopped poll never leaves a stale true. `WMSFO_FORCE_LEADER=true` short-circuits to true (refused in prod at boot). Transitions log at Information with markers `wmsfo_leader_gained` and `wmsfo_leader_lost`.

`ChoreHost` is one `BackgroundService` running each chore on its own cadence only while `Leader.IsLeader` is true at the moment the chore starts; a chore in flight finishes even if leadership lapses (every chore is idempotent).

| Chore | Cadence | Implementation |
|---|---|---|
| `OutboxPublisher` | 2 s | the claim query of contracts 7.6 (`skip locked`, 50 rows); per row per contracts 7.7; success sets `published_at`, failure sets `last_error` and leaves `claimed_at` for the 2-minute reclaim |
| `AlertSender` | 5 s | reads up to `5 * WMSFO_ALERT_SEND_PER_SEC` unsent deliveries oldest first, sends through `SesSender` under a `RateLimiter` of `WMSFO_ALERT_SEND_PER_SEC` per second, updates each row per contracts 7.6; a status alert (`event.status_changed`, `event.status_notified`) reads the payload's `messageId`, looks the body up in `event_message` as the `event.message_posted` path does, and renders it as `{{customMessage}}`, falling back to the stock paragraph when the payload names none or the row is gone; after each send it runs `update event_status_history set sent_count = sent_count + 1 where outbox_id = $1` and `update event_message set sent_count = sent_count + 1 where outbox_id = $1`, of which at most one matches, so a status alert counts on its history row and an `event.message_posted` alert on its message row |
| `StaleBeaconFlagger` | 15 s | the single `update` of contracts 7.6 with `beacon_stale_after_s` from memory |
| `MediaOrphanCollector` | 1 h | the statements of sql.md 9.6: referenced set in one query (`tracker_theme.thumbnail_media_id` included), stamp and clear `unreferenced_since`, tag and mark orphans older than 30 days (`PutObjectTagging state=orphaned` on the original and each variant before the row update), untag and revive orphans that are referenced again, delete rows orphaned more than 8 days ago; every S3 call 3 s, a failure skips that row until the next run |
| `NightlyCleanup` | 09:00 UTC | the six deletes of contracts 7.6 (tokens, outbox with its deliveries by cascade, the alert topics after 400 days and everything else after 30, unverified subscribers, beacon logs, expired preview tokens, stale pending media rows), each its own statement, each logged with its row count |
| `PendingMapSweeper` | 09:00 UTC, after `NightlyCleanup` | sql.md 9.7: every `tracker_map` row `pending` for more than a day; per row `ListMultipartUploadsAsync` under its prefix and abort each, `ListObjectsV2` and `DeleteObjects` under it, then the row delete; every S3 call 3 s, a failure skips that row until the next run; one log line with the row count |

`EmailTemplates.Load(dir, cdnBaseUrl, siteUrl)` reads `_layout.html` and `_layout.txt`, places each template's body fragment (`<name>.html` and `.txt`) as-is at the layout's `{{content}}`, validates the required substitutions against the composed result, and fails the boot on a missing layout, bundled image (logo, ornaments, light string), or fragment. `Render` supplies the layout tokens: `{{subject}}` (the rendered subject), `{{preheader}}` and `{{footerReason}}` from the template's spec, `{{ornamentsUrl}}` and `{{lightsUrl}}` (`WMSFO_CDN_BASE_URL/email/{sha256}.png` of `templates/email/ornaments.png` and `lights.png`, from `EmailTemplates.Ornaments` and `EmailTemplates.Lights`), `{{logoUrl}}` (`WMSFO_CDN_BASE_URL/email/{sha256}.png`, from `EmailLogo`), `{{siteName}}` (`Santa Tracker`), and `{{siteUrl}}` (`WMSFO_SITE_BASE_URL`), each unless the values carry one. `AlertSender` resolves `{{logoUrl}}` and `{{siteName}}` once per batch through `EmailLogoResolver` from the published settings (contracts 7.8): `logoMedia` as an opaque 192 px PNG tile from `EmailLogo.DeriveTile`, written once per asset to `email/{sha256 of the tile}.png`, the bundled logo when `logoMedia` is absent, svg, not ready, or fails to derive (a Warning, never a failed send), and the published `siteName` beside it. Every value is HTML-escaped in the HTML part and raw in the text part, and both parts are sent. `EmailLogo.EnsureWrittenAsync` runs in step 5 after the icon library for each of `EmailTemplates.BundledImages`, the bundled logo and beside it the ornaments and the light string: it `HEAD`s `email/{sha256 of the file}.png` and PUTs `templates/email/logo.png`, `ornaments.png`, or `lights.png` there (`image/png`, immutable cache header) only when the key is absent. Composition fills the layout's `{{statusPill}}` with the spec's `StatusPill` markup and its `{{footerLink}}` line with the spec's `FooterLink` (the unsubscribe line on alerts), dropping that line when the template has none, so every email renders the banner layout of contracts 7.8 with the ornaments above the card, the light string under the band, and the site's faces. The layout declares `light dark`, carries the light design inline, and holds the site's dark palette in one `prefers-color-scheme: dark` block keyed on the classes of the layout, the fragments, and `StatusPill` (`pill` and its `Tone`, one of `pillAccent`, `pillOk`, `pillDim`, `pillErr`, `pillWarn`), after a reset of every element inside the ground. The unit tests render every template in the layout and compare `templates/email/_golden/` byte for byte.

`SesSender` builds the message from the composed templates with `{{token}}` substitution (HTML-escaped in the HTML body), sets `From`, `To`, `Subject`, `List-Unsubscribe` and `List-Unsubscribe-Post` on alerts, `Reply-To` on `contact_received`, and the configuration set when non-empty. Templates are read once at boot and validated for the required substitutions (section 21). `EmailTemplates.FormatScheduleTime(value, zoneId)` renders a scheduled time as the wall time in the event's `schedule_time_zone` (`America/Denver` when null or unresolvable) followed by the IANA id in parentheses, for example `Sat, Dec 19 2026 at 6:00 PM (America/Denver)`; `AlertSender` reads the zone with `scheduled_at` from the event row.

`EmailQuotaReader` calls SES v2 `GetAccount` on the same optional `IAmazonSimpleEmailServiceV2` the sender uses (null in dry run) and returns the three `SendQuota` numbers as `(available, max24HourSend, sentLast24Hours, maxSendRate)`; a `Max24HourSend` of -1 becomes null. It caches the answer for 60 seconds behind a semaphore so concurrent callers on the same node share one call, and caches an unavailable answer (SES error, or no client) for 10 seconds only, logging the exception type and message once at Warning. `GET /admin/email/quota` (4.5 Email quota) is the only reader; it combines the reading with `count(*) filter (where sent_at is null)` on `alert_delivery` and the same verified-email-subscriber count the alert fan-out uses (contracts 7.7), and never records an audit row.

---

## 14. Beacon keys and enrollment

`Security/Keys.cs`:

| Function | Implementation |
|---|---|
| `MintKey()` | 32 bytes from `RandomNumberGenerator`, base64url without padding, prefixed `wbk_`; returns the key, `sha256` bytes, and the 12-character prefix |
| `MintEnrollmentToken()` | same with prefix `wet_` |
| `MintVerifyToken()`, `MintUnsubscribeToken()` | prefixes `wsv_`, `wsu_` |
| `Hash(string)` | `SHA256.HashData(Encoding.UTF8.GetBytes(s))` |
| `Encrypt(key)` / `Decrypt(blob)` | `AesGcm` with the 32-byte `WMSFO_ENROLLMENT_ENCRYPTION_KEY`, 12-byte random nonce prepended, 16-byte tag appended (contracts 3.3) |

`POST /admin/beacons` and `.../rotate` run one transaction: mint key, insert or update the beacon row (`key_hash`, `key_prefix`, `key_version`), delete pending tokens, insert the enrollment token row with `expires_at = now() + 15 minutes` and the encrypted key, commit; then build `Enrollment { token, url, qrPngDataUrl, expiresAt }` where `url = "rednose://enroll?api=" + Uri.EscapeDataString(WMSFO_PUBLIC_API_BASE_URL) + "&token=" + token` and `qrPngDataUrl` is `QRCoder` PNG at 8 px per module, error correction M, as `data:image/png;base64,...`. The plaintext key appears in the response once and in no log.

Activate, deactivate, revoke follow contracts 3.4 exactly; activate runs the two updates in one transaction in the stated order.

---

## 15. Telemetry and heartbeats

`HeartbeatIngest.HandleAsync(beaconId, body)`: validate the body per contracts 4.2: exactly the keys `sentAt` (required rfc3339), `health` (optional object or null with only `batteryPercent` 0 to 100, `lastFixAgeS` 0 or more, `socketState` from contracts 0.5, each optional and nullable; any other key inside is `400 validation_failed`), and `debug` (optional object or null, any content, nesting at most 8 levels, checked by walking the `JsonElement`); the whole body at most 32 KB (the route's body limit). Nothing inside `debug` is inspected beyond depth. Store with one statement:

```sql
update beacon set telemetry = $body::jsonb, last_heartbeat_at = now(), last_seen_at = now(), stale_since = null
where id = $id;
```

Respond with `liveEventId` from memory and `isActive` from the row. Heartbeats arrive over HTTP only and never touch the live object.

---

## 16. Logging and metrics

JSON console logging with these fields on every line: `ts`, `level`, `msg`, `requestId` (when in a request), `service` (`WMSFO_SERVICE_NAME`), `env`, `node` (the gateway instance id once known, else the hostname), plus event-specific properties. Template holes are written as camel case whatever their case in the code, so a `{Marker}` hole lands as the property `marker`. Request logging: one line per request at Information with properties `method`, `route` (the path template, or `unmatched` when there is none), `status`, `durationMs`, and `principal` (`beacon:<id>`, `person:<id>`, `admin:<id>`, `gateway`, `anon`); `GET /api/health` and paths under `/realtime` are skipped (the first would drown the log, the second is the gateway authorize traffic that stays at Debug); bodies and credentials are never logged; the authorize public branch logs at Debug only.

The location handler logs `location accepted` at Information with properties `seq`, `beaconId`, `eventId`, `published` (bool), and `outcome` (`stored`, `carried`, or `dropped`) on every accept and drop; the platform's `LocationPublished` metric filter keys on the line with `outcome=stored`. Markers that CloudWatch metric filters key on (platform.md 10): `wmsfo_live_put_failed`, `wmsfo_publish_failed`, `wmsfo_leader_gained`, `wmsfo_leader_lost`, `wmsfo_snapshot_write_failed`, `wmsfo_outbox_exhausted` (a row reached 5 attempts), `wmsfo_alert_exhausted`, `wmsfo_health_unavailable`, `wmsfo_media_write_failed` (a variant PUT or tag call failed), `wmsfo_content_published` (Information, with the version id and the publisher), `wmsfo_icon_library_written`. Each is a constant property `marker` on the log line.

Counters kept in memory and exposed on `GET /admin/live` under `node.counters`: locations stored, locations published, locations carried, locations rate-limited, live PUTs ok and failed, publishes ok and failed, heartbeats, authorize calls by branch, message-path calls by outcome. No metrics endpoint; CloudWatch metric filters on the log markers are the alarm source.

---

## 17. Health

`GET /api/health`: `503 unavailable` until the ready flag is set (migrations done, snapshot row present, state loaded); afterwards `select 1` with a 2 s timeout, `200 { "status": "ok" }` on success, `503` on failure with the `wmsfo_health_unavailable` marker logged at most once per 30 s. `Cache-Control: no-store`. The gateway's blue-green candidate check and its health prober both use it.

---

## 18. Container image

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/Wmsfo.Api && dotnet publish src/Wmsfo.Api -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl \
 && curl -fsSL https://truststore.pki.rds.amazonaws.com/global/global-bundle.pem -o /usr/local/share/ca-certificates/rds-global.crt \
 && update-ca-certificates && apt-get purge -y curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out .
COPY templates ./templates
COPY contracts ./contracts
COPY icons ./icons
COPY help ./help
USER app
EXPOSE 5000
ENTRYPOINT ["dotnet", "Wmsfo.Api.dll"]
```

Multi-arch (`linux/arm64` for the fleet, `linux/amd64` for local runs). No environment is baked into the image; everything comes from the container env the gateway injects from the secret. The RDS bundle install is what lets `Trust Server Certificate=false` validate (sql.md 13).

---

## 19. CI

`.github/workflows/deploy.yml`, on push to `dev` (environment `dev`, service `wmsfo-api-dev`, tag `<sha>-dev`) and `main` (environment `prod`, `wmsfo-api`, `<sha>-prod`). The test job (steps 1 and 2, plus a verify-only image build) runs on every push and pull request; the deploy job (steps 3 and 4) runs for `dev` only until the prod manifest entry and the prod environment secrets exist (platform.md 13):

1. `dotnet test` for both test projects, with a Postgres service container for the integration tests.
2. Contract check: build, run `dotnet run --project src/Wmsfo.Api -- export-contracts contracts` (which writes the OpenAPI document, the JSON Schemas, the fixtures, `starter-content.json`, and `admin-thresholds.json` in one pass), then `git diff --exit-code contracts/`; run `dotnet ef migrations has-pending-model-changes`; the schema tests (every kind in `kinds.json` has a schema file, every schema compiles, the fixtures and the starter content validate at the publish level, every library icon passes the SVG validator).
3. Assume `AWS_ROLE_ARN` through OIDC; `docker buildx build --platform linux/arm64,linux/amd64` and push `ECR_REPOSITORY:<sha>-<env>`.
4. Client-credentials token from `GATEWAY_TOKEN_URL` with scope `mgmt/deploy`; `POST GATEWAY_BASE_URL/mgmt/services/GATEWAY_SERVICE_NAME/deploy { "tag" }`; poll `GET /mgmt/deploys/{id}` until `done` (fail the job on `failed` or `partial`, 10-minute cap).

Secrets per environment are the ones in contracts 8.2. CI never upserts the manifest entry.

---

## 20. Local development

`docker compose up` in the repository starts Postgres 16 with both roles created by an init script (sql.md 12) and a static file server on `http://localhost:9000` serving `./.local-cdn`. The API runs with:

```
WMSFO_ENV=dev  WMSFO_SERVICE_NAME=wmsfo-api-dev  WMSFO_FORCE_LEADER=true
WMSFO_OBJECT_STORE_DIR=./.local-cdn  WMSFO_CDN_BASE_URL=http://localhost:9000
WMSFO_GATEWAY_INTERNAL_URL=http://localhost:1  (unreachable; publishes and leader polls no-op)
```

plus a dev Cognito pool for tokens, or `WMSFO_DEV_STATIC_TOKENS=true` to accept the three fixed test tokens (person, editor, admin) the integration tests use (refused when `WMSFO_ENV` is `prod`). With `WMSFO_OBJECT_STORE_DIR` set, `LocalObjectStore` cannot presign, so upload tickets point `uploadUrl` at `PUT /local-upload/{id}` on the API itself, which writes the bytes and the tag into the directory; that route exists only when the directory store is active and is refused in prod. The same store answers the map upload's part URLs as `PUT /local-upload/parts/{uploadId}/{partNumber}` (section 11.6), so the tile CLI runs against a local API unchanged. SES is replaced by `WMSFO_SES_DRY_RUN=true`, which logs the rendered message instead of sending. The site and the admin panel point `VITE_CDN_BASE_URL` at the static server and `VITE_API_BASE_URL` at `http://localhost:5000`. Red-Nose's dev flavour enrolls against the LAN address of this API over cleartext HTTP.

The integration tests pick a Postgres server in this order: `WMSFO_TEST_DB_CONNECTION` when set, otherwise the libpq environment variables (`PGHOST`, `PGPORT`, `PGUSER`, `PGPASSWORD`, `PGDATABASE`, with `5432`, `postgres`, empty, `postgres` as the defaults) when `PGHOST` is set, otherwise Testcontainers. The grunt runner exports the libpq variables against its local cluster on `127.0.0.1:5432` with trust auth, so `dotnet test` inside the runner needs nothing set by the task.

---

## 21. Contract artifacts and tests

`contracts/` is produced and checked in the API repository (contracts 13):

- `dotnet run --project src/Wmsfo.Api -- export-contracts contracts` writes every artifact under `contracts/` in one pass: the OpenAPI document from the endpoint metadata and the request and response DTOs, the JSON Schemas, the fixtures, `starter-content.json`, `admin-thresholds.json`, and `help-keys.json` (from `help/topics.json`, section 11a.9). The export mode builds the endpoint table and exits before configuration validation, so it needs no database, bucket, or secret; every endpoint, the maps and themes groups included, has a stub in `EndpointStubs.cs` that carries its metadata and DTOs into the OpenAPI document. The fixtures include the eight seeded theme bodies under `fixtures/themes/` (section 11a.10). CI fails when the checked-in files differ.
- JSON Schemas for the CDN objects and the beacon and callback bodies are generated from the DTOs with `System.Text.Json.Schema` and written next to the fixtures; the fixtures are serialized from fixed DTO instances through `CanonicalJson` so `fixtures/live-object.json` is byte-for-byte what the API writes for that data. The content schemas (`primitives`, `content-document`, `site-settings`, `sections/*`) and `kinds.json` are hand-written and are the source the API validates with; `fixtures/content-document.json` is the starter content re-serialized through `CanonicalJson`.
- `admin-thresholds.json` is a checked-in constant file `{ "batteryLowPercent": 20, "noFixAgeS": 30, "noLocationAgeS": 30 }` (the shape the admin panel document proposes).
- `CONTRACTS_VERSION` is bumped by hand in the commit that changes anything under `contracts/`; a test fails when the directory's hash changed and the version did not.

The integration tests give every test database the two roles of sql.md 12 and run the API as the application role and the migrator as the migrate role, so a statement outside the application role's grants fails locally the same way it would fail on dev. The tests' own set-up and assertions use the superuser connection through `PostgresFixture.ConnectionString`, and `TestConnections.For` maps that superuser connection back to the fixture's application and migrate connection strings.

Tests:

| Suite | Covers |
|---|---|
| Unit | key minting and hashing, AES-GCM round trip, canonical JSON byte equality against every fixture, SVG validator cases (and every library icon), image sniffing, variant width selection (700 px source yields 480 only), filename sanitizing, validation tables (every rule, one case each), the inline grammar (each token, malformed constructs, reference extraction), the reference walker over every kind's `defaults`, draft-schema derivation, apply-order of the live object builder, template substitution |
| Integration (Postgres from `WMSFO_TEST_DB_CONNECTION` when set, else the libpq environment variables `PGHOST` / `PGPORT` / `PGUSER` / `PGPASSWORD` / `PGDATABASE` when `PGHOST` is set, the grunt runner exports these against its local cluster, else Testcontainers when Docker is available; local object store; fake gateway client) | every endpoint's success and every listed error code; the location transaction under concurrency (two beacons, one active); `seq` monotonic across 1,000 concurrent inserts; the snapshot transaction rollback on a failing PUT; partial unique indexes (`23505` on the three); the tick rewrite rule; leader gating of chores with an overlapping leader; outbox and alert idempotency; nightly cleanup counts; the callback guard on forwarded headers; rate limits; the media pipeline end to end against the local store (ticket, PUT, confirm, variants, tag removal, usage, delete with its references cleared); publish with problems, unchanged, and success (version pruned at 51, snapshot embeds the document, media map contains exactly the referenced assets); restore keeps seven role pages; preview token expiry; the orphan collector's four transitions with a clock stub; `Editor` and `Admin` policy matrix over every `/admin/*` route, and the API-key matrix over the same routes (all capabilities, one capability, the wrong capability, expired, revoked, a key on the key endpoints); sponsor order rewrite and `pinned_position_taken`; route image linking and its two rejections |
| Contract | `openapi.json` up to date; schemas validate the fixtures; the migration has no pending model changes; templates contain their required substitutions |

---

## 22. Decisions made here

- Two database connections per sql.md 12 and 13: the app role for every request, the migrate role for the boot migrator only; pool parameters set in code.
- The object store is behind `IObjectStore` with an S3 implementation and a local-directory implementation for development and tests; the API otherwise never branches on environment.
- The publish body splices the exact bytes the writer PUT, so hub and CDN payloads are identical without a second serialization.
- Leadership expires 90 s after the last good answer on its own (two missed gateway reconcile loops), so a stalled poll can never leave a node believing it is leader.
- The tally delta counter bridges the second between a cookie insert and the next tick on the inserting node; the tick's SQL count is the truth every second.
- API keys share the `Authorization` header with ID tokens and are told apart by the `wak_` prefix; capabilities are endpoint metadata checked by one authorization requirement, so adding an endpoint group is one `.RequireCapability` call.
- Raster uploads decode with a 40-megapixel ceiling; the width variants are WebP quality 82 at 480, 960, and 1600 px, each only when narrower than the source; a raster with a longest side of 2048 px or more also gets a Deep Zoom pyramid (254 px JPEG tiles, overlap 1, quality 82) cut in the API process at confirm and served immutable beside the asset.
- Beacons carry no role; `POST /beacons/logs` is open to every beacon; a heartbeat is `sentAt` plus an optional typed `health` core and an optional free `debug` object bounded only by depth and size.
- Going live is refused without a healthy active beacon; health is the API's own stamps (not revoked, not stale, seen at least once), never the socket.
- Cookie moderation endpoints do not exist; `cookie.hidden_at` stays null and the tally query is unchanged.
- SVG validation uses a non-resolving `XmlReader` with DTDs prohibited and keeps the uploaded bytes.
- Enrollment QR codes are PNG, 8 px per module, error correction M.
- Cursors are base64url ids; CSV exports stream.
- Local runs accept fixed test tokens and dry-run SES only when `WMSFO_ENV` is not `prod`; `WMSFO_FORCE_LEADER` is refused in prod.
- Log markers, not a metrics endpoint, drive alarms.
- Content schemas are hand-written JSON Schema files and the API validates with `JsonSchema.Net`; the draft-level schema is derived mechanically at boot rather than maintained by hand.
- The reference walker recognizes the primitive shapes, not the kinds, so a new kind gets media, icon, link, and inline checking for free.
- Media never passes through the API: presigned PUT in, `GetObject` at confirm, variants derived in the API process (three resizes of one decode).
- The icon library is a directory of SVG files plus `library.json`; its hash gates a one-time write per deploy.
- Local development replaces presigning with a `PUT /local-upload/{id}` route that exists only with the directory object store.
- Tile packages never pass through the API: the tile CLI uploads them with multipart URLs the API signs, and confirm reads 127 bytes of each archive through a ranged get; the open upload ids are resolved by listing the prefix, never stored. Theme style bodies are content-addressed immutable objects written once per distinct body; a replaced style is a new object and the old one stays.

## 23. Needs a decision

Nothing at the moment. Add here as it comes up.
