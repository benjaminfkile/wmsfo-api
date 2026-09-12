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
  Wmsfo.sln
  Dockerfile
  .github/workflows/deploy.yml
  contracts/                          # section 21: openapi.json, schema/ (incl. primitives, sections/<kind>, site-settings, content-document), kinds.json, starter-content.json, fixtures/, admin-thresholds.json, CONTRACTS_VERSION
  icons/                              # the icon library: <id>.svg files plus library.json (id, name, tags)
  templates/email/                    # <name>.html and <name>.txt per contracts 7.8
  src/Wmsfo.Api/
    Program.cs                        # composition root, pipeline order (section 5)
    Config/WmsfoOptions.cs            # every WMSFO_* key, validated on boot (section 4)
    Config/ConnectionStrings.cs       # NpgsqlConnectionStringBuilder per sql.md 13
    Data/WmsfoDbContext.cs            # one entity per table (sql.md 14.2)
    Data/Migrations/                  # EF Core migrations
    Data/Sql/                         # the raw recipes as string constants, one file per recipe
    Data/DatabaseMigrator.cs          # advisory lock, migrate, starter content, icon library, content version 1, snapshot version 1 (sql.md 8.16)
    Http/ErrorHandling.cs             # ApiException, error shape, exception handler
    Http/RequestValidation.cs         # validators returning details.fields
    Http/ForwardedHeaders.cs          # client IP from X-Forwarded-For with WMSFO_TRUSTED_PROXY_HOPS
    Http/RateLimits.cs                # token buckets per contracts 4.0
    Auth/BeaconKeyAuthHandler.cs      # X-Beacon-Key scheme
    Auth/CognitoAuth.cs               # JwtBearer options, admin policy, person upsert
    Auth/AdminTotpGate.cs             # section 6.3
    Auth/ApiKeyAuthHandler.cs         # section 6.4: wak_ bearer scheme, capability requirement, DenyApiKeys
    Endpoints/Health.cs
    Endpoints/Beacons.cs              # enroll, me, locations, heartbeat, logs
    Endpoints/PublicWrites.cs         # contact, subscriptions verify and unsubscribe
    Endpoints/Me.cs                   # me, subscriptions, cookies
    Endpoints/Admin/Events.cs  Routes.cs  Beacons.cs  Sponsors.cs  CookieTypes.cs  Cookies.cs  ApiKeys.cs
    Endpoints/Admin/Settings.cs  Inbox.cs (contact, subscribers, people)  Diagnostics.cs (snapshot, live)
    Endpoints/Admin/Pages.cs  Sections.cs (sections, items, order, move, duplicate)  SiteSettings.cs
    Endpoints/Admin/Content.cs (kinds, status, draft, publish, versions, restore, preview token)  Media.cs  Icons.cs
    Endpoints/Preview.cs              # GET /preview/document
    Endpoints/Realtime.cs             # /realtime/authorize, /realtime/message
    Node/NodeState.cs                 # in-memory state (contracts 7.1)
    Node/ReconcileTick.cs             # hosted service (contracts 7.4)
    Node/LeaderMonitor.cs             # hosted service (contracts 7.5)
    Node/LiveObjectWriter.cs          # build, PUT, publish, live_state (contracts 1.8)
    Node/SnapshotBuilder.cs           # inside the admin transaction (contracts 7.3); embeds the content document, media map, icon map
    Content/KindRegistry.cs           # loads contracts/kinds.json and the kind schemas; KindInfo for the panel
    Content/SchemaValidator.cs        # publish-level and draft-level validation, Problem lists with JSON pointers
    Content/ReferenceChecker.cs       # media readiness, library icon ids, href rule, page-slug links, anchors, map-on-live
    Content/InlineText.cs             # the inline markdown grammar: parse for validation and reference extraction
    Content/DocumentBuilder.cs        # working set to ContentDocument (hidden omitted, contract order) and the media id scan
    Content/Publisher.cs              # the publish transaction (sql.md 8.19)
    Content/Restorer.cs               # sql.md 8.20
    Content/StarterContent.cs         # first-boot seed from contracts/starter-content.json
    Icons/IconLibrary.cs              # loads icons/, hashes, writes to the bucket, builds the icons map
    Chores/ChoreHost.cs               # runs chores while leader
    Chores/OutboxPublisher.cs  AlertSender.cs  StaleBeaconFlagger.cs  MediaOrphanCollector.cs  NightlyCleanup.cs
    Objects/IObjectStore.cs           # PutObject, DeleteObject, ListPrefix, CopyObjectWithHeaders
    Objects/S3ObjectStore.cs  LocalObjectStore.cs
    Objects/CanonicalJson.cs          # the serializer options and sha256 helper (contracts 1.6)
    Objects/CdnObjects.cs             # LiveObject, Snapshot, Route DTOs in contract key order
    Realtime/GatewayInternalClient.cs # publish and leader calls with the injected token
    Media/ImageSniffer.cs  SvgValidator.cs  VariantDeriver.cs  Presigner.cs  MediaConfirm.cs  MediaUsage.cs
    Security/Keys.cs                  # wbk_, wet_, wsv_, wsu_, wpv_ minting, hashing, AES-GCM
    Security/QrRenderer.cs
    Email/SesSender.cs  Templates.cs
    Contracts/OpenApiExport.cs        # writes contracts/openapi.json at build (section 21)
  tools/Wmsfo.Migrate/                # the one-off legacy migration tool (sql.md 15, platform.md 12)
  tests/Wmsfo.Api.Tests/              # unit + contract tests
  tests/Wmsfo.Api.IntegrationTests/   # Testcontainers Postgres, full pipeline
```

---

## 3. Startup sequence

`Program.cs` runs these in order; the container answers `503` on `/api/health` until step 8 completes.

1. Read configuration from environment variables into `WmsfoOptions`; fail fast on any missing or malformed value (section 4). Log one line with every non-secret value.
2. Build the two connection strings (sql.md 13) and register `WmsfoDbContext` (app connection) and the migration factory (migrate connection).
3. Register services: object store (S3 in the fleet, local directory when `WMSFO_OBJECT_STORE_DIR` is set), gateway internal client, SES sender, key service, rate limiter, node state, live object writer, snapshot builder.
4. Build the HTTP pipeline (section 5) and start listening. Listening early lets the gateway's health prober see `503` instead of connection refused.
5. `DatabaseMigrator.MigrateAsync`: advisory lock, `MigrateAsync`, then the first-boot steps of sql.md 8.16 in order: `StarterContent.EnsureSeededAsync`, `IconLibrary.EnsureWrittenAsync`, `Publisher.EnsureVersionOneAsync`, `SnapshotBootstrap.EnsureVersionOneAsync` (or a rebuild when the icon library changed). On failure retry with 5 s, 10 s, 30 s, then 60 s; health stays `503`.
6. `NodeState.LoadAsync`: one reconcile round trip (contracts 7.4) to fill memory.
7. Start hosted services: `ReconcileTick`, `LeaderMonitor`, `ChoreHost`.
8. Mark ready. `GET /api/health` now answers `200` while `select 1` succeeds.

A first boot with an empty database seeds the starter content, writes the icon library, publishes content version 1, builds snapshot version 1 inside step 5, and writes the live object once (contracts 5). The kind registry and the schemas are loaded in step 3 and a malformed schema file fails the boot. `SIGTERM`: stop accepting requests, wait up to 20 s for in-flight requests and any in-flight live-object write, exit. The gateway's blue-green keeps the old container serving until the new one is `200`, so shutdown never drops traffic.

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
| `WMSFO_SES_FROM_ADDRESS` | mailbox | `Name <address>` or bare address |
| `WMSFO_SES_CONFIGURATION_SET` | string | may be empty |
| `WMSFO_CONTACT_NOTIFY_EMAIL` | address | valid |
| `WMSFO_ALERT_SEND_PER_SEC` | int | 1 to 50, default 10 |
| `WMSFO_ENROLLMENT_ENCRYPTION_KEY` | base64 | decodes to exactly 32 bytes |
| `WMSFO_RECONCILE_TICK_MS` | int | 250 to 10000, default 1000 |
| `WMSFO_LOG_LEVEL` | level | `Debug`, `Information`, `Warning` |
| `WMSFO_FORCE_LEADER` | bool | local only; refused (boot failure) when `WMSFO_ENV` is `prod` |
| `WMSFO_OBJECT_STORE_DIR` | path | local only; when set the object store writes to this directory and `WMSFO_CDN_BASE_URL` may point at a local static server |

`WMSFO_DB_MIGRATION_CONNECTION` follows `sql.md` 12 and 13 (two roles).

Pool parameters are set in code (sql.md 13), never in the secret. The options object is immutable after boot; there is no hot reload.

---

## 5. HTTP pipeline

Middleware order in `Program.cs`, outermost first:

1. **Readiness**: until the ready flag is set (section 3 step 8) every request except `GET /api/health` answers `503 unavailable`.
2. **Forwarded headers**: `ForwardedHeadersMiddleware` with `ForwardLimit = WMSFO_TRUSTED_PROXY_HOPS`, known networks cleared, so `HttpContext.Connection.RemoteIpAddress` is the client IP counted from the right of `X-Forwarded-For`. It is mounted with `UseWhen` on every path except `/realtime/*`, because the middleware removes the `X-Forwarded-For` entries it consumes and the callback guard (section 12.1) must see the raw headers.
3. **Request id and logging scope**: `Activity.Current?.Id ?? HttpContext.TraceIdentifier` becomes `requestId`; every log line in the request carries it.
4. **Exception handler**: maps `ApiException` to its status and code, `BadHttpRequestException` (body too large, malformed JSON) to `413 payload_too_large` or `400 validation_failed`, `OperationCanceledException` on a client abort to nothing, everything else to `500 internal_error` with the stack logged at Error and never returned.
5. **Body size limits**: per route through `RequestSizeLimit` metadata: 64 KB JSON default, 256 KB section, item, and site settings bodies, 5 MB routes, 2 MB beacon logs, 8 KB heartbeats. Media bytes never arrive here.
6. **CORS**: one policy with the exact origins from `WMSFO_CORS_ORIGINS`, methods `GET, POST, PUT, PATCH, DELETE`, headers `Authorization, Content-Type, X-Beacon-Key, X-App-Version`, `SetPreflightMaxAge(600)`, credentials off. Applied to every route except the two callbacks and `/api/health`.
7. **Authentication**: two schemes registered; each endpoint names the one it requires through `RequireAuthorization(policy)`. A request carrying both `Authorization` and `X-Beacon-Key` is `400 validation_failed` before any scheme runs.
8. **Rate limiting**: `Microsoft.AspNetCore.RateLimiting` token-bucket policies per contracts 4.0, partitioned by beacon id, person id, or client IP as the table says; over budget writes the error shape with `retryAfterSeconds` and the `Retry-After` header. The callbacks and `/api/health` carry `DisableRateLimiting`.
9. **Endpoints**.

Response conventions: every JSON response uses the shared serializer options; `Cache-Control: no-store` on every API response; `serverTime` is added by a result filter on the beacon endpoints, stamped after the handler returned, which is after any transaction committed.

Error shape: `ApiException(status, code, message, details)`; `RequestValidation` collects field errors into `details.fields` and throws one `validation_failed`. Unknown JSON fields are rejected by `JsonSerializerOptions.UnmappedMemberHandling = Disallow` on request DTOs, with the three exceptions the contracts list handled by dedicated DTOs that allow extension data.

---

## 6. Authentication and authorization

### 6.1 Beacon key scheme

`BeaconKeyAuthHandler` (an `AuthenticationHandler`):

1. Read `X-Beacon-Key`. Absent: no result (the endpoint's policy then answers `401`).
2. Check the regex `^wbk_[A-Za-z0-9_-]{43}$`; on mismatch fail with `401 unauthenticated`.
3. `select id, role, is_active, revoked_at, key_version from beacon where key_hash = sha256($key)`; missing or `revoked_at` set: `401 unauthenticated`. The lookup is one indexed read; no timing-sensitive comparison is needed because the index lookup is on the hash.
4. Principal claims: `beacon_id`, `beacon_role`, `beacon_active`, `key_version`. Policies: `Beacon` (any beacon), `BeaconAdmin` (`beacon_role = admin`, else `403 forbidden`).

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
| `POST /locations` | Beacon | validate; location transaction; respond; then `LiveObjectWriter.WriteForLocation` when `published` | contracts 7.2 |
| `POST /beacons/heartbeat` | Beacon | validate the seven groups; store telemetry, stamp `last_heartbeat_at`, `last_seen_at`, clear `stale_since`; respond with the live event id and `is_active` | |
| `POST /beacons/logs` | BeaconAdmin | `text/plain` only; insert `beacon_log` | |
| `POST /contact` | none (IP-limited) | validate; insert `contact_message` and outbox `contact.received` in one transaction | |
| `POST /subscriptions/verify` | none | hash lookup; set `verified_at` per the contracts' three cases | |
| `POST /subscriptions/unsubscribe` | none | token from query or JSON; accept form-urlencoded and ignore its body; set `unsubscribed_at` | |
| `GET /me` | Person | person row and `isAdmin` | |
| `GET /me/subscriptions`, `POST`, `POST .../resend-verification`, `DELETE .../{id}` | Person | per contracts 4.4; verify token mint and outbox `subscription.verify` in the same transaction | |
| `GET /me/cookies` | Person | current event, limit from settings, counts | |
| `POST /cookies` | Person | cookie transaction; increment the node tally | sql.md 8 cookie |
| `/admin/events*` | Admin (`events`) | events, status, messages, status history, locations export, cookies list; `routeImageMediaId` must name a `ready` raster asset (`409 media_not_ready`, `400` for svg or gif) | contracts 7.3 for the [snapshot] writes; status transaction per contracts 4.5 |
| `/admin/routes*` | Admin (`routes`) | flight recordings: canonicalize, hash, existing-row check, PUT, insert; `from-event` reads the event's published locations in `seq` order and feeds the same path | section 11.1 |
| `/admin/beacons*` | Admin | create, patch, activate, deactivate, rotate, revoke, logs; list and get resolve `hubConnected` from one presence call | section 14 |
| `/admin/sponsors*` | Editor (`sponsors`) | CRUD, years upsert with `pinnedPosition` (`409 pinned_position_taken` from the partial unique index) and `lingerMsOverride`; `order/{eventYear}` reads and rewrites the pinned list for a year in one transaction; `logoMediaId` must name a `ready` asset (`409 media_not_ready`); every `SponsorYear` answered carries the computed `lingerMs` | [snapshot] |
| `/admin/api-keys*` | Admin, Cognito only (`DenyApiKeys`) | list, mint, revoke | section 6.4 |
| `/admin/cookie-types*` | Admin | CRUD with an `icon` value (library id checked against the library, media icon must be a `ready` svg asset); `409 event_live` guard | [snapshot] |
| `/admin/pages*`, `/admin/sections*`, `/admin/items*` | Editor | working-set CRUD, order, move, duplicate; draft validation through `SchemaValidator`; `kind_not_allowed` from the registry's `allowedRoles` | sql.md 8.21 |
| `/admin/site-settings` | Editor | read and replace the single row; draft validation | sql.md 8.21 |
| `GET /admin/content/kinds` | Editor | the registry as `KindInfo[]` with schemas inlined | section 11a.1 |
| `GET /admin/content/status`, `GET /admin/content/draft` | Editor | build the draft document (11a.3), validate at the publish level, hash, compare with the newest version | |
| `POST /admin/content/publish` | Editor | section 11a.4 | sql.md 8.19; [snapshot] |
| `GET /admin/content/versions*`, `POST .../restore` | Editor | list, get, restore (11a.5) | sql.md 8.20 |
| `POST /admin/content/preview-token` | Editor | mint `wpv_`, insert the hash, answer the site URL | sql.md 8.23 |
| `GET /preview/document` | none (IP-limited) | resolve the token, build the draft bundle (11a.6) | sql.md 8.23 |
| `/admin/media*` | Editor | list, ticket, confirm, get, usage, patch (alt and title, [snapshot]), delete with the usage guard | section 11.2 to 11.5 |
| `GET /admin/icons` | Editor | the library as `IconInfo[]` from `IconLibrary` | section 11a.7 |
| `/admin/cookies/{id}/hide`, `unhide`, `DELETE` | Admin | moderation; tally re-read and live-object write only while status 3 | |
| `/admin/settings*` | Admin | list with defaults; `PUT` validates type and range per contracts 6 | [snapshot] |
| `/admin/contact-messages*`, `/admin/subscribers*`, `/admin/people*` | Admin | paged lists, deletes, summary | |
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
    Settings Settings,                        // the five keys
    DateTimeOffset RefreshedAt);
```

Besides the snapshot: `Leader { IsLeader, EvaluatedAt }` (section 13), `LastWrittenVersion`, `WroteForLocationSinceVersionChange`, and a per-node `TallyDelta` counter map that `POST /cookies` increments and the next tick folds into the refreshed tally (the tick's SQL count is the truth; the delta only bridges the second between insert and tick).

`Refresh(reason)` runs the six statements of contracts 7.4 on the app connection in one round trip (a single batched command) and replaces the record. `Load` on boot is the same call. Every endpoint reads `NodeState.Current` once at the start of the request.

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

Build: `CdnObjects.LiveObject` in contract key order, `publishedAt = now`, `cookieTally` from memory while the event's status is not 4 and from `event.final_cookie_tally` while it is (keys in ascending numeric order), serialized with `CanonicalJson.Options`. PUT: `Key = "live/location.json"`, `ContentType = "application/json; charset=utf-8"`, `CacheControl = "s-maxage=1, max-age=0"`. Publish: `GatewayInternalClient.PublishAsync("<service>:location", "location", bytes)` always, whether or not the PUT succeeded. Then `update live_state set ...` per contracts 1.8. Failures log at Warning with marker `wmsfo_live_put_failed` or `wmsfo_publish_failed`.

### 10.2 Snapshot builder

Runs inside the admin transaction (contracts 7.3) on the transaction's connection:

1. `select * from snapshot where id = 1 for update`.
2. Apply the write (the endpoint's own statements).
3. Run the snapshot reads of sql.md 7 in the same transaction; build `CdnObjects.Snapshot` in contract key order with the sponsor filter and ordering of contracts 1.3 (pinned rows first, then amount desc, name, id), `lingerMs` from `linger_ms_override` when the row has one and otherwise from the settings read in the transaction, `event.routeImageMediaId` from `event.route_image_media_id` (the asset joins the `media` map like any other referenced asset), `event.flightHistory` from the linked `route` row's object thinned to `flight_history_max_points` by keeping every `ceil(n / max)`-th point starting from the first and always the last. The builder reads the route object from the object store inside the transaction; there is no `route_point` cache table. A route is at most 50,000 points (contracts 1.4, 5 MB cap), so one `GetObject` inside the transaction is cheap. `content` is the newest `content_version.document` parsed and re-serialized through the same options (byte-identical by construction); `media` is the referenced assets (the version's `media_ids`, the listed sponsors' logos, the cookie types' media icons, plus `event.route_image_media_id`) with keys in ascending order; `icons` is `IconLibrary.Map` (id to CDN URL, ascending).
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

`POST /admin/routes`: read the body (5 MB limit is enforced first), deserialize into `RouteUpload { name, points[] }` with unknown keys rejected, validate per contracts 1.4, build `CdnObjects.Route` (adds `schemaVersion: 1`), canonicalize, hash, `select ... from route where s3_key = $key` (found: `200` that row), PUT with the immutable header (3 s, one attempt, else `502 route_write_failed`), insert; on `23505` on `s3_key` re-read and answer `200`. `POST /admin/routes/from-event/{eventId}` streams `select lat, lng, recorded_at from location where event_id = $id and published order by seq` into the same `RouteUpload` shape (`404` for an unknown event, `400` under 2 points, `413` over 50,000) and continues identically. `DELETE` checks `event.route_id` references (`409 route_in_use`), deletes the row, then the object (a failed delete of the object is logged; the row is already gone).

### 11.2 Media tickets and presigned uploads

`POST /admin/media/upload-url`: validate the body (contracts 4.5 Media); sanitize the filename (`[^A-Za-z0-9._-]` to `-`, collapse repeats, trim to 100, and make sure the extension matches the declared type: `png`, `jpg` or `jpeg`, `webp`, `gif`, `svg`); mint a UUID; insert the `pending` row (sql.md 8.22) with `s3_key = media/{id}/{filename}`; presign a `PUT` for that key with `Expires = 15 minutes`, `ContentType` set to the declared type, and the header `x-amz-tagging: state=pending` included in the signature; answer `UploadTicket` with `headers = { "Content-Type": <type>, "x-amz-tagging": "state=pending" }`. The browser must send exactly those headers or S3 rejects the signature. The bucket's CORS rule (platform.md 1.4) allows the PUT from the admin origins. Limits by type: raster and gif 20 MB, svg 1 MB, enforced on `sizeBytes` here and on the real object at confirm.

### 11.3 Confirm and variants

`POST /admin/media/{id}/confirm` (`MediaConfirm.RunAsync`):

1. Read the row; `404` when missing, `409 media_not_pending` unless `state = 'pending'`.
2. `HeadObject`; missing: `404 upload_not_found`. Size over the type's limit: delete the object and the row, `413`.
3. `GetObject` into memory (at most 20 MB). `ImageSniffer` decides png, jpeg, webp, gif, or svg from the bytes; a mismatch with `content_type`: delete both, `400 validation_failed` on `file`. SVG runs `SvgValidator` (11.4); failure deletes both, `400`.
4. Raster (png, jpeg, webp): ImageSharp decode with a 40-megapixel ceiling (`400` beyond it), record width and height; for each of 480, 960, 1600 that is less than the width, resize to that width (aspect kept), encode WebP quality 82, PUT `media/{id}/w{width}.webp` with the immutable header and the same pending tag (3 s, one attempt each; failure: `502 media_write_failed`, row stays pending, the panel retries confirm). GIF: width and height from the decoder, no variants. SVG: width and height null, no variants.
5. `sha256` of the original bytes. `DeleteObjectTagging` on the original and every variant (removes the pending tag so the lifecycle rule ignores them). Update the row to `ready` (sql.md 8.22). Answer `MediaAsset`.

Confirm is idempotent while the row is pending: a retry after a step 4 failure re-derives and re-PUTs (same bytes, same keys). No snapshot rebuild: an asset reaches the site only when something published references it.

### 11.4 SVG validation

`XmlReader` with `DtdProcessing.Prohibit`, `XmlResolver = null`, and a 1 MB character limit. Walk every element and attribute; reject with `400 validation_failed` on field `file` when: any element local name is `script` or `foreignObject`; any attribute name starts with `on` (case-insensitive); any `href` or `xlink:href` value, trimmed, starts with `http:`, `https:`, or `javascript:` (case-insensitive); the root is not `svg`. The stored bytes are the uploaded bytes, not a re-serialization. The same validator runs over every file in `icons/` in a unit test, so a library icon can never fail it at boot.

### 11.5 Usage, patch, delete, orphans

`MediaUsage.ForAsync(id)` runs the five usage statements of sql.md 8.22 and returns `MediaUsage`. `DELETE /admin/media/{id}` runs it first and answers `409 media_in_use` with the usage in `details` when anything references the asset; otherwise `ListObjectsV2` under `media/{id}/`, `DeleteObjects`, then the row delete. `PATCH` (alt, title) runs the [snapshot] frame because `alt` rides in the snapshot's media map. `MediaOrphanCollector` (section 13) is the only other writer of media state.

## 11a. Content

### 11a.1 Kind registry and schemas

`KindRegistry` loads `contracts/kinds.json` (kind, title, description, live, hasItems, allowedRoles, defaults, itemDefaults) and, per kind, `contracts/schema/sections/<kind>.schema.json` and, when `hasItems`, `<kind>.item.schema.json`; plus `primitives.schema.json`, `site-settings.schema.json`, and `content-document.schema.json`. Every schema is compiled once with `JsonSchema.Net` with a resolver that serves the primitives by `$ref`. `GET /admin/content/kinds` returns each kind with its schemas as JSON with the referenced primitives inlined under `$defs`, so the panel's form generator needs no resolver. Adding a kind to the API is one line in `kinds.json` and one schema file; the site and the panel pick it up from the contracts copy.

### 11a.2 Validation

`SchemaValidator.Validate(schema, instance, level)` returns `Problem[]` with JSON-pointer paths. `Draft` uses the derived lenient schema (the publish schema with `required`, `minLength`, `minItems`, and `minimum` removed at every level, computed once at boot); unknown properties are still rejected (`additionalProperties: false` on every object in every contract schema). `Publish` uses the full schema, then `ReferenceChecker` adds the semantic problems of contracts 1.3a: it walks the instance for every `MediaRef`, media-sourced `Icon`, library `Icon`, `Link`, and `Inline` value (the walker knows the primitive shapes, not the kinds, so a new kind needs no walker change), resolves media ids in one `select id, state, kind from media_asset where id = any($ids)`, checks library ids against `IconLibrary`, parses every `Inline` with `InlineText` to extract inline links and icons and to reject malformed markup, checks hrefs (absolute http or https, `mailto:`, or `/<slug>` with an optional `#anchor` naming a non-hidden page), checks anchors for uniqueness per page, and refuses a `map` section outside the `live` page.

`InlineText.Parse` is the reference grammar for the site's renderer: tokens `**`, `*`, backtick, `[label](href)`, `{icon:<id>}`, `{icon:media:<uuid>}`, `{event:name|year|scheduledAt}`, newline; anything else is text. Unbalanced markers are text, not errors; a malformed `{...}` construct is a publish-level problem.

### 11a.3 Document builder

`DocumentBuilder.BuildAsync(connection)` reads the working set in document order (sql.md 8.19), omits hidden pages, sections, and items, and produces `ContentDocument` DTOs in contract key order plus the set of referenced media ids (collected by the same walker as 11a.2). `GET /admin/content/draft` and the preview endpoint serialize this through `CanonicalJson`; the publish transaction hashes exactly those bytes.

### 11a.4 Publish

`Publisher.PublishAsync(label, email)` is the [snapshot] frame with the statements of sql.md 8.19: lock the snapshot row, build, validate at the publish level (problems: rollback, `422 content_invalid` with `details.problems` as `ProblemRef[]` carrying page, section, and item ids), hash, compare with the newest version (`409 content_unchanged`), insert the version with its media ids, prune to 50, rebuild the snapshot (which now embeds this document), PUT, update the snapshot row, commit, then `liveObjectWriter.WriteFromState("publish")`. `EnsureVersionOneAsync` on first boot is the same code path with `published_by = 'seed'` and without the snapshot steps (sql.md 8.16 does them next).

### 11a.5 Restore

`Restorer.RestoreAsync(versionId, email)` runs sql.md 8.20 in one transaction: delete every page (cascade), reinsert pages, sections, and items from the version's document with new ids and `updated_by = email`, replace the site settings draft. It answers `GET /admin/content/status` computed afterwards. Because a published document always holds exactly one page per role, the six role pages come back; the partial unique index is satisfied because the delete precedes the inserts in the same transaction.

### 11a.6 Preview

`POST /admin/content/preview-token` mints `wpv_` + 43 base64url characters, inserts `sha256(token)` with a 15-minute expiry, and answers `{ token, url: WMSFO_SITE_BASE_URL + "/preview?token=" + token, expiresAt }`. `GET /preview/document?token=` (public, IP-limited, no `serverTime`) looks the hash up, then answers `ContentBundle`: the draft document (11a.3, no validation), the media map for every referenced asset whatever its state (a pending reference is a missing image in the preview, which is the point), and the icon map. `Cache-Control: no-store`. The site's `/preview` route is the only consumer.

### 11a.7 Icon library

`IconLibrary` loads `icons/library.json` and every `icons/<id>.svg` at boot, validates each with `SvgValidator` (a failure fails the boot; the unit test catches it first), computes each file's sha256 and the library hash (sha256 over the sorted `id:sha256` lines), and exposes `Map` (id to `WMSFO_CDN_BASE_URL/icons/{sha256}.svg`) and `Infos` for `GET /admin/icons`. `EnsureWrittenAsync` (under the migration lock, sql.md 8.16 step 2) compares the hash with `icon_library_state.library_sha256`, PUTs every icon with the immutable header when they differ (an icon whose bytes did not change lands on the same key), updates the row, and reports whether it wrote so the migrator runs a snapshot rebuild afterwards. Library ids match `^[a-z0-9]+(-[a-z0-9]+)*$`; removing an icon from the library is a breaking change for any published document that uses it, so the unit test that guards `library.json` fails when an id disappears unless the change is marked deliberate in the test.

### 11a.8 Starter content

`StarterContent.EnsureSeededAsync` (sql.md 8.16 step 1) inserts `contracts/starter-content.json` when `page` is empty: the six role pages with a section stack that makes sense for each status (for example the live page with a `map` section and a `leaderboard`; the ended page with `event_times`, a `leaderboard` in the `full` variant, a `sponsor_grid`, and a `latest_message`), the ordinary pages (`about`, `sponsors`, `route`, `donate`, `contact`, `alerts`), and the site settings. It references library icons only and no media, so it publishes cleanly on an empty bucket. A contract test publishes it against the schemas at the publish level.

---

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

The `payload` for a publish is the exact bytes the writer PUT, passed as raw JSON (`JsonSerializer.SerializeToUtf8Bytes` is not re-run; the body is assembled with the bytes spliced in) so the hub and the CDN carry identical bytes. When `GATEWAY_REALTIME_TOKEN` is absent (local runs) every call is a no-op that logs once at startup.

---

## 13. Leadership and chores

`LeaderMonitor` polls `GET /internal/leader` every 2 s (1 s timeout) and stores `Leader { IsLeader, EvaluatedAt, InstanceId }`. `IsLeader` is true only when the latest answer was `2xx`, `isLeader` was true, and `evaluatedAt` is non-null and under 90 s old at evaluation time (the gateway refreshes it on its 30 s reconcile loop); the value expires on its own 90 s after the answer, so a stopped poll never leaves a stale true. `WMSFO_FORCE_LEADER=true` short-circuits to true (refused in prod at boot). Transitions log at Information with markers `wmsfo_leader_gained` and `wmsfo_leader_lost`.

`ChoreHost` is one `BackgroundService` running each chore on its own cadence only while `Leader.IsLeader` is true at the moment the chore starts; a chore in flight finishes even if leadership lapses (every chore is idempotent).

| Chore | Cadence | Implementation |
|---|---|---|
| `OutboxPublisher` | 2 s | the claim query of contracts 7.6 (`skip locked`, 50 rows); per row per contracts 7.7; success sets `published_at`, failure sets `last_error` and leaves `claimed_at` for the 2-minute reclaim |
| `AlertSender` | 5 s | reads up to `5 * WMSFO_ALERT_SEND_PER_SEC` unsent deliveries oldest first, sends through `SesSender` under a `RateLimiter` of `WMSFO_ALERT_SEND_PER_SEC` per second, updates each row per contracts 7.6 |
| `StaleBeaconFlagger` | 15 s | the single `update` of contracts 7.6 with `beacon_stale_after_s` from memory |
| `MediaOrphanCollector` | 1 h | the statements of sql.md 9.6: referenced set in one query, stamp and clear `unreferenced_since`, tag and mark orphans older than 30 days (`PutObjectTagging state=orphaned` on the original and each variant before the row update), untag and revive orphans that are referenced again, delete rows orphaned more than 8 days ago; every S3 call 3 s, a failure skips that row until the next run |
| `NightlyCleanup` | 09:00 UTC | the six deletes of contracts 7.6 (tokens, outbox with its deliveries by cascade, unverified subscribers, beacon logs, expired preview tokens, stale pending media rows), each its own statement, each logged with its row count |

`SesSender` builds the message from `templates/email/<name>.html` and `.txt` with `{{token}}` substitution (HTML-escaped in the HTML body), sets `From`, `To`, `Subject`, `List-Unsubscribe` and `List-Unsubscribe-Post` on alerts, `Reply-To` on `contact_received`, and the configuration set when non-empty. Templates are read once at boot and validated for the required substitutions (section 21).

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

`HeartbeatIngest.HandleAsync(beaconId, body)`: validate the seven top-level keys (`sentAt` required rfc3339; each group object or null), the known leaves (finite numbers, strings up to 64 characters, enum values from contracts 0.5), and the 8 KB limit; unknown nested keys pass through. Store with one statement:

```sql
update beacon set telemetry = $body::jsonb, last_heartbeat_at = now(), last_seen_at = now(), stale_since = null
where id = $id;
```

Respond with `liveEventId` from memory and `isActive` from the row. Heartbeats arrive over HTTP only and never touch the live object.

---

## 16. Logging and metrics

JSON console logging with these fields on every line: `ts`, `level`, `msg`, `requestId` (when in a request), `service` (`WMSFO_SERVICE_NAME`), `env`, `node` (the gateway instance id once known, else the hostname), plus event-specific properties. Request logging: one line per request at Information with properties `method`, `route` (the path template), `status`, `durationMs`, and `principal` (`beacon:<id>`, `person:<id>`, `admin:<id>`, `gateway`, `anon`); bodies and credentials are never logged; the authorize public branch logs at Debug only.

The location handler logs `location stored` at Information with properties `seq`, `beaconId`, `eventId`, and `published` (bool) after every stored update; the platform's `LocationPublished` metric filter keys on that line. Markers that CloudWatch metric filters key on (platform.md 10): `wmsfo_live_put_failed`, `wmsfo_publish_failed`, `wmsfo_leader_gained`, `wmsfo_leader_lost`, `wmsfo_snapshot_write_failed`, `wmsfo_outbox_exhausted` (a row reached 5 attempts), `wmsfo_alert_exhausted`, `wmsfo_health_unavailable`, `wmsfo_media_write_failed` (a variant PUT or tag call failed), `wmsfo_content_published` (Information, with the version id and the publisher), `wmsfo_icon_library_written`. Each is a constant property `marker` on the log line.

Counters kept in memory and exposed on `GET /admin/live` under `node.counters`: locations stored, locations published, live PUTs ok and failed, publishes ok and failed, heartbeats, authorize calls by branch, message-path calls by outcome. No metrics endpoint; CloudWatch metric filters on the log markers are the alarm source.

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
USER app
EXPOSE 5000
ENTRYPOINT ["dotnet", "Wmsfo.Api.dll"]
```

Multi-arch (`linux/arm64` for the fleet, `linux/amd64` for local runs). No environment is baked into the image; everything comes from the container env the gateway injects from the secret. The RDS bundle install is what lets `Trust Server Certificate=false` validate (sql.md 13).

---

## 19. CI

`.github/workflows/deploy.yml`, on push to `dev` (environment `dev`, service `wmsfo-api-dev`, tag `<sha>-dev`) and `main` (environment `prod`, `wmsfo-api`, `<sha>-prod`):

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

plus a dev Cognito pool for tokens, or `WMSFO_DEV_STATIC_TOKENS=true` to accept the three fixed test tokens (person, editor, admin) the integration tests use (refused when `WMSFO_ENV` is `prod`). With `WMSFO_OBJECT_STORE_DIR` set, `LocalObjectStore` cannot presign, so upload tickets point `uploadUrl` at `PUT /local-upload/{id}` on the API itself, which writes the bytes and the tag into the directory; that route exists only when the directory store is active and is refused in prod. SES is replaced by `WMSFO_SES_DRY_RUN=true`, which logs the rendered message instead of sending. The site and the admin panel point `VITE_CDN_BASE_URL` at the static server and `VITE_API_BASE_URL` at `http://localhost:5000`. Red-Nose's dev flavour enrolls against the LAN address of this API over cleartext HTTP.

The integration tests pick a Postgres server in this order: `WMSFO_TEST_DB_CONNECTION` when set, otherwise the libpq environment variables (`PGHOST`, `PGPORT`, `PGUSER`, `PGPASSWORD`, `PGDATABASE`, with `5432`, `postgres`, empty, `postgres` as the defaults) when `PGHOST` is set, otherwise Testcontainers. The grunt runner exports the libpq variables against its local cluster on `127.0.0.1:5432` with trust auth, so `dotnet test` inside the runner needs nothing set by the task.

---

## 21. Contract artifacts and tests

`contracts/` is produced and checked in the API repository (contracts 13):

- `dotnet run --project src/Wmsfo.Api -- export-contracts contracts` writes every artifact under `contracts/` in one pass: the OpenAPI document from the endpoint metadata and the request and response DTOs, the JSON Schemas, the fixtures, `starter-content.json`, and `admin-thresholds.json`. The export mode builds the endpoint table and exits before configuration validation, so it needs no database, bucket, or secret. CI fails when the checked-in files differ.
- JSON Schemas for the CDN objects and the beacon and callback bodies are generated from the DTOs with `System.Text.Json.Schema` and written next to the fixtures; the fixtures are serialized from fixed DTO instances through `CanonicalJson` so `fixtures/live-object.json` is byte-for-byte what the API writes for that data. The content schemas (`primitives`, `content-document`, `site-settings`, `sections/*`) and `kinds.json` are hand-written and are the source the API validates with; `fixtures/content-document.json` is the starter content re-serialized through `CanonicalJson`.
- `admin-thresholds.json` is a checked-in constant file `{ "batteryLowPercent": 20, "noFixAgeS": 30, "noLocationAgeS": 30 }` (the shape the admin panel document proposes).
- `CONTRACTS_VERSION` is bumped by hand in the commit that changes anything under `contracts/`; a test fails when the directory's hash changed and the version did not.

Tests:

| Suite | Covers |
|---|---|
| Unit | key minting and hashing, AES-GCM round trip, canonical JSON byte equality against every fixture, SVG validator cases (and every library icon), image sniffing, variant width selection (700 px source yields 480 only), filename sanitizing, validation tables (every rule, one case each), the inline grammar (each token, malformed constructs, reference extraction), the reference walker over every kind's `defaults`, draft-schema derivation, apply-order of the live object builder, template substitution |
| Integration (Postgres from `WMSFO_TEST_DB_CONNECTION` when set, else the libpq environment variables `PGHOST` / `PGPORT` / `PGUSER` / `PGPASSWORD` / `PGDATABASE` when `PGHOST` is set, the grunt runner exports these against its local cluster, else Testcontainers when Docker is available; local object store; fake gateway client) | every endpoint's success and every listed error code; the location transaction under concurrency (two beacons, one active); `seq` monotonic across 1,000 concurrent inserts; the snapshot transaction rollback on a failing PUT; partial unique indexes (`23505` on the three); the tick rewrite rule; leader gating of chores with an overlapping leader; outbox and alert idempotency; nightly cleanup counts; the callback guard on forwarded headers; rate limits; the media pipeline end to end against the local store (ticket, PUT, confirm, variants, tag removal, usage, `409 media_in_use`, delete); publish with problems, unchanged, and success (version pruned at 51, snapshot embeds the document, media map contains exactly the referenced assets); restore recreates six role pages; preview token expiry; the orphan collector's four transitions with a clock stub; `Editor` and `Admin` policy matrix over every `/admin/*` route, and the API-key matrix over the same routes (all capabilities, one capability, the wrong capability, expired, revoked, a key on the key endpoints); sponsor order rewrite and `pinned_position_taken`; route image linking and its two rejections |
| Contract | `openapi.json` up to date; schemas validate the fixtures; the migration has no pending model changes; templates contain their required substitutions |

---

## 22. Decisions made here

- Two database connections per sql.md 12 and 13: the app role for every request, the migrate role for the boot migrator only; pool parameters set in code.
- The object store is behind `IObjectStore` with an S3 implementation and a local-directory implementation for development and tests; the API otherwise never branches on environment.
- The publish body splices the exact bytes the writer PUT, so hub and CDN payloads are identical without a second serialization.
- Leadership expires 10 s after the last good answer on its own, so a stalled poll can never leave a node believing it is leader.
- The tally delta counter bridges the second between a cookie insert and the next tick on the inserting node; the tick's SQL count is the truth every second.
- API keys share the `Authorization` header with ID tokens and are told apart by the `wak_` prefix; capabilities are endpoint metadata checked by one authorization requirement, so adding an endpoint group is one `.RequireCapability` call.
- Raster uploads decode with a 40-megapixel ceiling; the width variants are WebP quality 82 at 480, 960, and 1600 px, each only when narrower than the source.
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

## 23. Needs a decision

Nothing at the moment. Add here as it comes up.
