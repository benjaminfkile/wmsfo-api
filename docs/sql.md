# WMSFO v2 SQL schema and data layer

This document is the technical design of the Postgres schema and the data layer underneath the API. The design overview is authoritative; the shared contracts fix every name and shape used here. This document adds what an engineer needs to build the database: the complete DDL, every index and why it exists, foreign-key behaviour, seeds, transaction recipes, retention, roles, connection settings, the migration tooling, and the column-level mapping for the one-off legacy migration. Choices the overview and the contracts do not make are listed in section 16; choices that need the owner are in section 17.

---

## 1. Conventions

| Item | Value |
|---|---|
| Server | PostgreSQL, the shared RDS instance. Nothing here needs an extension; the DDL runs on any supported major from 13 up. |
| Databases | `wmsfo_dev`, `wmsfo_prod`. One database per environment, one schema, `public`. |
| Roles | `wmsfo_migrate_dev` / `wmsfo_migrate_prod` own the database and every object and run migrations. `wmsfo_app_dev` / `wmsfo_app_prod` are the API's runtime roles with DML only (section 13). |
| Names | snake_case, singular table names. Column-to-wire mapping is mechanical snake_case to camelCase. |
| Ids | `bigint generated always as identity`, starting at 1. Two single-row tables use `smallint` with `check (id = 1)`. |
| Timestamps | `timestamptz` everywhere. The database runs in UTC; nothing depends on the session time zone. |
| Coordinates | `double precision` `lat` and `lng`. No PostGIS. |
| Money | `numeric(12,2)`; only `sponsor_year.amount_donated`. |
| Hashes | SHA-256 of a credential is `bytea` (32 bytes); SHA-256 of object bytes is `char(64)` lowercase hex where it is displayed (`route.sha256`). |
| Triggers | None. The API writes `updated_at = now()` in every update statement that touches a table with that column. |
| Soft delete | Only `cookie.hidden_at`. Every other delete is a hard delete. |
| Isolation | Read committed. Correctness comes from explicit row locks (`for update`) in the recipes of section 8 and from the unique indexes. |
| Constraint names | Postgres default names (`<table>_pkey`, `<table>_<columns>_key`, `<table>_<column>_fkey`, `<table>_<column>_check`), fixed explicitly in the EF Core model so the generated migration produces exactly these names (section 14). The API maps unique violations to error codes by constraint name (section 4.3). |

---

## 2. Table inventory

| Table | Purpose | Written by | Expected size |
|---|---|---|---|
| `event_status` | Fixed lookup, 5 rows | Migration seed | 5 |
| `event` | One row per flyover year; status, current flag, `next_seq` counter | Admin writes; location transaction (`next_seq`); migration tool | tens |
| `event_status_history` | Every status change | Status change transaction | hundreds |
| `event_message` | Messages shown on the site | Admin writes; migration tool | hundreds |
| `route` | Uploaded route objects (metadata; bytes live on the CDN) | Route upload; migration tool | tens |
| `beacon` | Trusted senders, hashed key, telemetry, health stamps | Admin writes; ingest; heartbeat; chores | tens |
| `beacon_enrollment_token` | One-time QR enrollment tokens | Beacon create, rotate, enroll; nightly cleanup | tens |
| `beacon_log` | Red-Nose debug log uploads | `POST /beacons/logs`; nightly cleanup | tens per month |
| `location` | Every stored fix, published or not | Location transaction; migration tool | about 10k per event (1 per second for a few hours) plus spares; kept forever |
| `sponsor` | Sponsor master data, logo as a media reference | Editor writes; migration tool | tens |
| `sponsor_year` | Sponsor participation per year | Admin writes; migration tool | hundreds |
| `person` | Registered people, keyed by Cognito `sub` | Upsert on every authenticated request | about 20k |
| `subscriber` | Alert subscriptions | Person and admin writes; nightly cleanup | about 20k |
| `cookie_type` | Admin-managed cookie types with an icon value | Admin writes; migration seed | tens |
| `cookie` | Cookies left during live events | `POST /cookies`; moderation | up to `cookie_limit_per_person` × people per event |
| `contact_message` | Contact form submissions | `POST /contact`; migration tool | tens per year |
| `app_setting` | The five admin knobs, one row per key | `PUT /admin/settings/{key}`; seed; migration tool | 5 |
| `media_asset` | The media library: one row per upload, state, dimensions, variants | Upload ticket, confirm, patch, delete; orphan chore; migration tool | hundreds |
| `page` | Site pages: six role pages plus ordinary pages | Editor writes; seed; restore | tens |
| `section` | Typed sections on a page, `data` and `presentation` JSON | Editor writes; seed; restore | hundreds |
| `section_item` | Repeatable items of a section | Editor writes; seed; restore | hundreds |
| `site_setting_draft` | Single row: the site settings working set | `PUT /admin/site-settings`; seed; restore | 1 |
| `content_version` | Published content documents, newest 50 kept | Publish; first boot | 50 |
| `preview_token` | Short-lived preview tokens, hashed | Preview token mint; nightly cleanup | tens |
| `icon_library_state` | Single row: hash of the icon library last written to the bucket | Boot migrator | 1 |
| `snapshot` | Single row: current snapshot URL and version | Snapshot-affecting transactions; first boot | 1 |
| `live_state` | Single row: outcome of the last live-object write | Every live-object write | 1 |
| `outbox` | Work for the leader chores | Status change, message post, subscribe, contact | hundreds live; 30-day retention |
| `alert_delivery` | One row per (subscriber, outbox row) email | Outbox chore; alert send chore | tens of thousands per alert |
| `__EFMigrationsHistory` | EF Core migration history | Migrator only | tens |

---

## 3. DDL

The statements below are the initial migration, in dependency order. Comments are part of the migration.

### 3.1 Lookup: `event_status`

```sql
create table event_status (
  id   smallint primary key,
  name text not null unique
);
insert into event_status (id, name) values
  (1, 'planned'), (2, 'scheduled'), (3, 'live'), (4, 'ended'), (5, 'cancelled');

comment on table event_status is 'Fixed lookup. Never edited through the API. Ids are the statusId values on every wire.';
```

### 3.2 `route`

```sql
create table route (
  id          bigint generated always as identity primary key,
  name        text not null,
  s3_key      text not null unique,
  url         text not null,
  sha256      char(64) not null,
  point_count integer not null,
  uploaded_by text not null,
  created_at  timestamptz not null default now()
);

comment on table route is 'Metadata for a route object on the CDN. The bytes live only in the bucket under s3_key; the row is never re-serialized from the database.';
comment on column route.s3_key is 'routes/{sha256}.json. Content-hashed, so an identical upload resolves to the existing row.';
comment on column route.url is 'Absolute CDN URL of s3_key, carried into the snapshot as routeUrl.';
comment on column route.sha256 is 'Lowercase hex SHA-256 of the canonical object bytes.';
comment on column route.uploaded_by is 'Admin email claim, or ''migration''.';
```

### 3.3 `event`

```sql
create table event (
  id            bigint generated always as identity primary key,
  year          integer not null unique,
  name          text not null,
  status_id     smallint not null references event_status (id),
  is_current    boolean not null default false,
  scheduled_at  timestamptz,
  went_live_at  timestamptz,
  ended_at      timestamptz,
  funds_percent integer not null default 0 check (funds_percent between 0 and 100),
  route_id      bigint references route (id),
  final_cookie_tally jsonb,
  next_seq      bigint not null default 1,
  created_by    text not null,
  created_at    timestamptz not null default now(),
  updated_at    timestamptz not null default now()
);
create unique index event_one_live    on event (status_id) where status_id = 3;
create unique index event_one_current on event (is_current) where is_current;

comment on table event is 'One row per flyover. Status changes only when an admin changes them.';
comment on column event.status_id is 'event_status.id. At most one row has 3 (event_one_live).';
comment on column event.is_current is 'The event the public site shows. Set only by POST /admin/events/{id}/current. At most one true (event_one_current).';
comment on column event.scheduled_at is 'Admin-entered. Required while status_id = 2.';
comment on column event.went_live_at is 'Stamped now() on every entry into status 3; admin-patchable.';
comment on column event.ended_at is 'Stamped now() on every entry into status 4; admin-patchable.';
comment on column event.funds_percent is 'Cheer meter, 0 to 100.';
comment on column event.route_id is 'Route shown for this event; null when unlinked.';
comment on column event.next_seq is 'Next location.seq for this event. Read and incremented under the row lock in the location transaction, so seq order is commit order.';
```

### 3.4 `event_status_history`

```sql
create table event_status_history (
  id             bigint generated always as identity primary key,
  event_id       bigint not null references event (id) on delete cascade,
  from_status_id smallint references event_status (id),
  to_status_id   smallint not null references event_status (id),
  changed_by     text not null,
  changed_at     timestamptz not null default now()
);
create index event_status_history_event on event_status_history (event_id, changed_at desc);

comment on table event_status_history is 'One row per status change, written in the status change transaction.';
comment on column event_status_history.from_status_id is 'Null when there was no previous status.';
```

### 3.5 `event_message`

```sql
create table event_message (
  id         bigint generated always as identity primary key,
  event_id   bigint not null references event (id) on delete cascade,
  body       text not null,
  event_time timestamptz,
  created_by text not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);
create index event_message_event_created on event_message (event_id, created_at desc);

comment on table event_message is 'Messages shown on the site. The snapshot carries the one with the greatest created_at (ties: greatest id).';
comment on column event_message.event_time is 'The time the message is about, as entered by the admin. Display only.';
```

### 3.6 `beacon`

```sql
create table beacon (
  id                bigint generated always as identity primary key,
  name              text not null,
  notes             text not null default '',
  role              text not null check (role in ('beacon', 'admin')),
  key_hash          bytea not null unique,
  key_prefix        text not null,
  key_version       integer not null default 1,
  is_active         boolean not null default false,
  revoked_at        timestamptz,
  last_seen_at      timestamptz,
  last_location_at  timestamptz,
  last_heartbeat_at timestamptz,
  stale_since       timestamptz,
  telemetry         jsonb,
  created_by        text not null,
  created_at        timestamptz not null default now(),
  updated_at        timestamptz not null default now()
);
create unique index beacon_one_active on beacon (is_active) where is_active;

comment on table beacon is 'A trusted sender. Rows are never deleted; revoke is permanent.';
comment on column beacon.role is 'beacon or admin. Immutable after creation.';
comment on column beacon.key_hash is 'sha256 of the plaintext key, 32 bytes. The plaintext is never stored.';
comment on column beacon.key_prefix is 'First 12 characters of the key, for display.';
comment on column beacon.key_version is 'Incremented by rotate. Carried in the hub identity as "<id>:<key_version>"; the message path rejects a stale version.';
comment on column beacon.is_active is 'The one beacon whose updates fan out. At most one true (beacon_one_active). Zero is allowed.';
comment on column beacon.revoked_at is 'Set once by revoke. A revoked beacon never authenticates again.';
comment on column beacon.last_seen_at is 'Any authenticated contact: REST call, hub authorize, hub message.';
comment on column beacon.last_location_at is 'Last stored location, published or not.';
comment on column beacon.last_heartbeat_at is 'Last stored heartbeat.';
comment on column beacon.stale_since is 'Set by the stale-beacon chore; cleared by a heartbeat or a stored location.';
comment on column beacon.telemetry is 'The last heartbeat body, stored as received.';
```

### 3.7 `beacon_enrollment_token`

```sql
create table beacon_enrollment_token (
  id             bigint generated always as identity primary key,
  beacon_id      bigint not null references beacon (id) on delete cascade,
  token_hash     bytea not null unique,
  key_ciphertext bytea,
  expires_at     timestamptz not null,
  consumed_at    timestamptz,
  created_at     timestamptz not null default now()
);

comment on table beacon_enrollment_token is 'One-time tokens behind the enrollment QR code. 15-minute lifetime, single use.';
comment on column beacon_enrollment_token.token_hash is 'sha256 of the wet_ token.';
comment on column beacon_enrollment_token.key_ciphertext is 'AES-256-GCM of the plaintext beacon key (12-byte nonce prepended, 16-byte tag appended). Nulled when consumed.';
```

### 3.8 `beacon_log`

```sql
create table beacon_log (
  id          bigint generated always as identity primary key,
  beacon_id   bigint not null references beacon (id) on delete cascade,
  received_at timestamptz not null default now(),
  app_version text,
  size_bytes  integer not null,
  body        text not null
);
create index beacon_log_beacon on beacon_log (beacon_id, received_at desc);

comment on table beacon_log is 'Red-Nose debug log uploads (admin-role beacons). Up to 2 MB each. 30-day retention.';
```

### 3.9 `location`

```sql
create table location (
  id          bigint generated always as identity primary key,
  event_id    bigint not null references event (id),
  beacon_id   bigint not null references beacon (id),
  seq         bigint not null,
  recorded_at timestamptz not null,
  received_at timestamptz not null default now(),
  lat         double precision not null,
  lng         double precision not null,
  speed_mps   double precision,
  altitude_m  double precision,
  heading_deg double precision,
  accuracy_m  double precision,
  published   boolean not null,
  unique (event_id, seq)
);
create index location_event_published_seq on location (event_id, seq desc) where published;

comment on table location is 'Every stored fix. Kept forever; exported by admins; never read by the public site.';
comment on column location.seq is 'Arrival order within the event, from event.next_seq. The only order that exists.';
comment on column location.recorded_at is 'The fix time the beacon sent. Informational; it never decides anything.';
comment on column location.received_at is 'When the API stored the row.';
comment on column location.published is 'beacon.is_active at the moment of the insert. Only published rows reach the live object.';
```

### 3.10 `sponsor`

```sql
create table sponsor (
  id                bigint generated always as identity primary key,
  name              text not null,
  contact_person    text,
  email             text,
  phone             text,
  address           text,
  website_url       text,
  fb_url            text,
  ig_url            text,
  logo_media_id     uuid references media_asset (id),
  created_at        timestamptz not null default now(),
  updated_at        timestamptz not null default now()
);
create index sponsor_logo_media on sponsor (logo_media_id) where logo_media_id is not null;

comment on table sponsor is 'Sponsor master data. contact_person, email, phone, and address are admin-only and never reach the snapshot.';
comment on column sponsor.logo_media_id is 'A ready media_asset chosen from the library; null when no logo. The API refuses to delete a referenced asset (409 media_in_use).';
```

### 3.11 `sponsor_year`

```sql
create table sponsor_year (
  id             bigint generated always as identity primary key,
  sponsor_id     bigint not null references sponsor (id) on delete cascade,
  event_year     integer not null,
  amount_donated numeric(12,2) check (amount_donated >= 0),
  active         boolean not null default true,
  can_advertise  boolean not null default true,
  anonymous      boolean not null default false,
  registered_at  timestamptz not null default now(),
  unique (sponsor_id, event_year)
);

comment on table sponsor_year is 'A sponsor''s participation in one year. Upserted on (sponsor_id, event_year).';
comment on column sponsor_year.amount_donated is 'Admin-only. Drives lingerMs; never appears in a public object.';
```

### 3.12 `person`

```sql
create table person (
  id           bigint generated always as identity primary key,
  cognito_sub  uuid not null unique,
  email        text not null,
  created_at   timestamptz not null default now(),
  last_seen_at timestamptz not null default now()
);

comment on table person is 'A registered person. Upserted on every authenticated request from the ID token (sub, email).';
```

### 3.13 `subscriber`

```sql
create table subscriber (
  id                bigint generated always as identity primary key,
  person_id         bigint not null references person (id) on delete cascade,
  channel           text not null check (channel in ('email', 'sms')),
  address           text not null,
  verify_token_hash bytea,
  verify_expires_at timestamptz,
  verified_at       timestamptz,
  unsubscribe_token text not null unique,
  unsubscribed_at   timestamptz,
  created_at        timestamptz not null default now(),
  updated_at        timestamptz not null default now(),
  unique (channel, address)
);
create index subscriber_person on subscriber (person_id);
create index subscriber_verify_token_hash on subscriber (verify_token_hash) where verify_token_hash is not null;

comment on table subscriber is 'Alert subscriptions. channel is email in v1; sms is reserved and rejected by the API.';
comment on column subscriber.address is 'Stored trimmed and lowercased.';
comment on column subscriber.verify_token_hash is 'sha256 of the last minted wsv_ token. Kept after verification until the next mint so the verify page is idempotent.';
comment on column subscriber.unsubscribe_token is 'Plaintext wsu_ token, stable for the life of the row.';
```

### 3.14 `cookie_type`

```sql
create table cookie_type (
  id         bigint generated always as identity primary key,
  name       text not null,
  icon       jsonb,
  sort       integer not null default 0,
  active     boolean not null default true,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

comment on table cookie_type is 'Admin-managed. Locked (409 event_live) while any event has status 3. No delete; active = false removes a type from the snapshot.';
comment on column cookie_type.icon is 'Icon value (contracts 1.3a): {"source":"library","id":"cookie"} or {"source":"media","id":"<uuid of a ready svg media_asset>"}; null until chosen.';
```

Initial rows, inserted only when the table is empty:

```sql
insert into cookie_type (name, sort, icon)
select v.name, v.sort, v.icon::jsonb
from (values ('Chocolate chip', 10, '{"source":"library","id":"cookie"}'),
             ('Gingerbread', 20, '{"source":"library","id":"gingerbread"}'),
             ('Snickerdoodle', 30, '{"source":"library","id":"cookie-swirl"}'),
             ('Sugar', 40, '{"source":"library","id":"cookie-star"}'),
             ('Happy', 50, '{"source":"library","id":"cookie-smile"}')) as v (name, sort, icon)
where not exists (select 1 from cookie_type);
```

### 3.15 `cookie`

```sql
create table cookie (
  id             bigint generated always as identity primary key,
  event_id       bigint not null references event (id) on delete cascade,
  person_id      bigint not null references person (id) on delete cascade,
  cookie_type_id bigint not null references cookie_type (id),
  note           text,
  left_at        timestamptz not null default now(),
  hidden_at      timestamptz,
  hidden_by      text
);
create index cookie_event_person       on cookie (event_id, person_id);
create index cookie_event_type_visible on cookie (event_id, cookie_type_id) where hidden_at is null;
create index cookie_person             on cookie (person_id);

comment on table cookie is 'A cookie left by a registered person during a live event. No location.';
comment on column cookie.note is 'Never public. Visible to admins only.';
comment on column cookie.hidden_at is 'Soft delete for moderation. Hidden cookies leave the tally but still count toward the per-person limit.';
```

### 3.16 `contact_message`

```sql
create table contact_message (
  id         bigint generated always as identity primary key,
  name       text not null,
  email      text not null,
  body       text not null,
  client_ip  text not null,
  created_at timestamptz not null default now()
);

comment on table contact_message is 'Contact form submissions. No automatic retention; admins delete.';
comment on column contact_message.client_ip is 'Client IP per the trusted-proxy rule; empty string for migrated rows.';
```

### 3.17 `app_setting`

```sql
create table app_setting (
  key        text primary key,
  value      jsonb not null,
  updated_by text not null,
  updated_at timestamptz not null default now()
);

comment on table app_setting is 'The admin knobs, one row per key. Only PUT /admin/settings/{key} changes a value. A missing row means the compiled default.';
```

Seed rows, the defaults from the design:

```sql
insert into app_setting (key, value, updated_by) values
  ('poll_interval_ms',             '5000', 'seed'),
  ('cookie_limit_per_person',      '10',   'seed'),
  ('sponsor_linger_ms_per_dollar', '40',   'seed'),
  ('sponsor_linger_min_ms',        '2000', 'seed'),
  ('beacon_stale_after_s',         '45',   'seed')
on conflict (key) do nothing;
```

The API validates type and range per key on write (section 6 of the contracts); the column carries no check so the ranges can change without a migration.

### 3.18 `snapshot` and `live_state` (single row each)

```sql
create table snapshot (
  id       smallint primary key check (id = 1),
  version  bigint not null,
  url      text not null,
  s3_key   text not null,
  built_at timestamptz not null
);

comment on table snapshot is 'Single row (id = 1). The current snapshot object. Rewritten in the same transaction as every snapshot-affecting write. Inserted by the first boot of an environment; absent means the environment has never finished booting.';
comment on column snapshot.version is 'Increments on every rebuild, including rebuilds that produce identical bytes. Nodes watch it on the reconcile tick.';

create table live_state (
  id                 smallint primary key check (id = 1),
  last_write_at      timestamptz,
  last_write_seq     bigint,
  last_write_version bigint,
  last_write_error   text,
  last_write_node    text
);
insert into live_state (id) values (1);

comment on table live_state is 'Single row (id = 1). Fleet-wide outcome of the last live-object write. Returned by GET /admin/live.';
```

Single-row enforcement: the primary key admits one value per `id` and the check constraint admits only `id = 1`, so a second row is impossible whatever it carries. `live_state` is seeded by the migration; `snapshot` is inserted once by the node that builds version 1 (section 8.16). Both are written with `update ... where id = 1`.

### 3.19 `outbox`

```sql
create table outbox (
  id           bigint generated always as identity primary key,
  topic        text not null,
  payload      jsonb not null,
  created_at   timestamptz not null default now(),
  claimed_at   timestamptz,
  published_at timestamptz,
  attempts     integer not null default 0,
  last_error   text
);
create index outbox_unpublished on outbox (id) where published_at is null;

comment on table outbox is 'Work for the leader chores, written in the same transaction as the change it announces. Topics: event.status_changed, event.message_posted, subscription.verify, contact.received.';
comment on column outbox.claimed_at is 'Set by the claim query; a claim older than 2 minutes is reclaimable.';
comment on column outbox.attempts is 'Rows with 5 attempts stay unpublished and are visible in the logs.';
```

### 3.20 `alert_delivery`

```sql
create table alert_delivery (
  id             bigint generated always as identity primary key,
  outbox_id      bigint not null references outbox (id) on delete cascade,
  subscriber_id  bigint not null references subscriber (id) on delete cascade,
  created_at     timestamptz not null default now(),
  sent_at        timestamptz,
  ses_message_id text,
  attempts       integer not null default 0,
  last_error     text,
  unique (subscriber_id, outbox_id)
);
create index alert_delivery_unsent on alert_delivery (id) where sent_at is null;
create index alert_delivery_outbox on alert_delivery (outbox_id);

comment on table alert_delivery is 'One email per (subscriber, outbox row). The unique constraint is what makes overlapping leaders safe.';
```

### 3.21 `media_asset`

Created before `sponsor` in the migration (the sponsor foreign key needs it); listed here with the content tables it belongs with.

```sql
create table media_asset (
  id                 uuid primary key,
  filename           text not null,
  content_type       text not null,
  kind               text not null check (kind in ('raster', 'svg', 'gif')),
  state              text not null check (state in ('pending', 'ready', 'orphaned')),
  s3_key             text not null unique,
  size_bytes         bigint,
  width              integer,
  height             integer,
  sha256             char(64),
  variants           jsonb not null default '{}',
  alt                text not null default '',
  title              text not null default '',
  uploaded_by        text not null,
  created_at         timestamptz not null default now(),
  confirmed_at       timestamptz,
  unreferenced_since timestamptz,
  orphaned_at        timestamptz
);
create index media_asset_state_created on media_asset (state, created_at);

comment on table media_asset is 'The media library. pending: ticket issued, bytes may or may not be in the bucket. ready: confirmed. orphaned: unreferenced for 30 days, objects tagged for lifecycle expiry; the row is deleted 8 days later.';
comment on column media_asset.id is 'Minted by the API (UUID v4) when the upload ticket is issued; it is the key segment media/{id}/.';
comment on column media_asset.s3_key is 'media/{id}/{filename}; the original bytes.';
comment on column media_asset.variants is '{ "480": "media/{id}/w480.webp", "960": ..., "1600": ... }: the WebP width variants that exist (raster only, widths below the source width).';
comment on column media_asset.kind is 'raster: png, jpeg, webp (variants derived). gif: stored as is. svg: validated, usable as an icon.';
```

### 3.22 `page`

```sql
create table page (
  id           bigint generated always as identity primary key,
  slug         text not null unique,
  title        text not null,
  nav_label    text,
  nav_position integer not null default 0,
  is_hidden    boolean not null default false,
  role         text not null default 'none'
               check (role in ('none', 'no_event', 'planned', 'scheduled', 'live', 'ended', 'cancelled')),
  created_by   text not null,
  created_at   timestamptz not null default now(),
  updated_by   text not null,
  updated_at   timestamptz not null default now()
);
create unique index page_one_per_role on page (role) where role <> 'none';

comment on table page is 'Working set. The six role pages are seeded, undeletable, and role-immutable; none pages render at /<slug>. Hidden pages are omitted at publish.';
comment on column page.slug is 'One lowercase path segment, ^[a-z0-9]+(-[a-z0-9]+)*$, 1 to 60; never auth, preview, api, admin, assets. Validated by the API.';
comment on column page.nav_label is 'Nav entry text; null keeps the page out of the nav. Always null on role pages.';
```

### 3.23 `section`

```sql
create table section (
  id           bigint generated always as identity primary key,
  page_id      bigint not null references page (id) on delete cascade,
  kind         text not null,
  position     integer not null,
  is_hidden    boolean not null default false,
  data         jsonb not null default '{}',
  presentation jsonb not null default '{}',
  created_at   timestamptz not null default now(),
  updated_by   text not null,
  updated_at   timestamptz not null default now()
);
create index section_page_position on section (page_id, position, id);

comment on table section is 'Working set. kind names a registry entry (contracts 1.3a); data is validated against the kind schema, presentation against the shared presentation schema, at the draft level on write and at the publish level on publish.';
comment on column section.position is 'Compacted to 0, 1, 2, ... within the page after every reorder, move, insert, and delete.';
```

### 3.24 `section_item`

```sql
create table section_item (
  id         bigint generated always as identity primary key,
  section_id bigint not null references section (id) on delete cascade,
  position   integer not null,
  is_hidden  boolean not null default false,
  data       jsonb not null default '{}',
  created_at timestamptz not null default now(),
  updated_by text not null,
  updated_at timestamptz not null default now()
);
create index section_item_section_position on section_item (section_id, position, id);

comment on table section_item is 'Working set. Repeatable items of a section whose kind has an item schema (media, links, icon_row). position compacted like section.position.';
```

### 3.25 `site_setting_draft`

```sql
create table site_setting_draft (
  id         smallint primary key check (id = 1),
  data       jsonb not null default '{}',
  updated_by text,
  updated_at timestamptz
);
insert into site_setting_draft (id) values (1);

comment on table site_setting_draft is 'Single row (id = 1). The site settings working set (contracts 1.3a SiteSettings), draft-validated on PUT, publish-validated on publish. Seeded from the starter content on first boot.';
```

### 3.26 `content_version`

```sql
create table content_version (
  id           bigint generated always as identity primary key,
  document     jsonb not null,
  sha256       char(64) not null,
  media_ids    uuid[] not null default '{}',
  label        text,
  published_by text not null,
  published_at timestamptz not null default now()
);

comment on table content_version is 'Published content documents. The row with the greatest id is what the snapshot carries; the newest 50 are kept, older rows are deleted in the publish transaction. Restore copies a row back into the working set and publishes nothing.';
comment on column content_version.document is 'ContentDocument (contracts 1.3a) exactly as the snapshot embeds it: hidden rows omitted, canonical order.';
comment on column content_version.sha256 is 'SHA-256 of the canonical bytes of document; POST /admin/content/publish answers 409 content_unchanged when the working set hashes to the newest row.';
comment on column content_version.media_ids is 'Every media_asset id the document references (MediaRef, media icons, background media). The orphan chore treats these as referenced while the row exists.';
```

### 3.27 `preview_token`

```sql
create table preview_token (
  id         bigint generated always as identity primary key,
  token_hash bytea not null unique,
  created_by text not null,
  created_at timestamptz not null default now(),
  expires_at timestamptz not null
);

comment on table preview_token is 'wpv_ tokens minted by POST /admin/content/preview-token, sha256 at rest, 15 minutes, reusable until expiry, deleted by the nightly cleanup 24 h after expiry. Database-backed so any node can answer GET /preview/document.';
```

### 3.28 `icon_library_state`

```sql
create table icon_library_state (
  id             smallint primary key check (id = 1),
  library_sha256 char(64),
  written_at     timestamptz
);
insert into icon_library_state (id) values (1);

comment on table icon_library_state is 'Single row (id = 1). Hash of the icon library the fleet last wrote to the bucket. The boot migrator compares it with the compiled library and writes the icons when they differ (section 8.16).';
```

---

## 4. Indexes

Every index, including the ones created implicitly by primary keys and unique constraints, with the query it serves. Section 10 shows the hot-path queries against them.

### 4.1 Index inventory

| Index | Definition | Serves |
|---|---|---|
| `event_status_pkey`, `event_status_name_key` | pk `(id)`, unique `(name)` | FK checks |
| `route_pkey` | pk `(id)` | lookups by id; event join |
| `route_s3_key_key` | unique `(s3_key)` | duplicate upload detection (`POST /admin/routes` looks up by `s3_key` before the PUT and maps a `23505` after it to "return the existing row") |
| `event_pkey` | pk `(id)` | every event write; joins |
| `event_year_key` | unique `(year)` | `409 year_taken` (pre-check plus `23505` mapping); the ordered admin list; migration idempotency |
| `event_one_live` | unique `(status_id) where status_id = 3` | the live-event lock in the location and cookie transactions; the concurrency guard behind `409 another_event_live`; `liveEventId` on heartbeat answers |
| `event_one_current` | unique `(is_current) where is_current` | the reconcile tick's current-event read; the snapshot builder; the concurrency guard behind the current-flag switch |
| `event_status_history_pkey` | pk `(id)` | |
| `event_status_history_event` | `(event_id, changed_at desc)` | `GET /admin/events/{id}/status-history` newest first; cascade from `event` |
| `event_message_pkey` | pk `(id)` | message patch and delete |
| `event_message_event_created` | `(event_id, created_at desc)` | `latestMessage` in the snapshot builder (`order by created_at desc, id desc limit 1`); the admin list; cascade from `event` |
| `beacon_pkey` | pk `(id)` | the message-path identity check; every stamp |
| `beacon_key_hash_key` | unique `(key_hash)` | `X-Beacon-Key` resolution; the ingest branch of the authorize callback |
| `beacon_one_active` | unique `(is_active) where is_active` | the reconcile tick's active-beacon read; the guard behind activate |
| `beacon_enrollment_token_pkey` | pk `(id)` | consume |
| `beacon_enrollment_token_token_hash_key` | unique `(token_hash)` | `POST /beacons/enroll` |
| `beacon_log_pkey` | pk `(id)` | `GET /admin/beacons/{id}/logs/{logId}` |
| `beacon_log_beacon` | `(beacon_id, received_at desc)` | the per-beacon log list newest first; nightly cleanup by `received_at` is a scan of a small table |
| `location_pkey` | pk `(id)` | |
| `location_event_id_seq_key` | unique `(event_id, seq)` | the export (`order by seq asc`, keyset on `seq`); the guarantee that `next_seq` never hands out a duplicate |
| `location_event_published_seq` | `(event_id, seq desc) where published` | the reconcile tick's latest published location (`limit 1`); the export with `publishedOnly=true` |
| `sponsor_pkey` | pk `(id)` | |
| `sponsor_logo_media` | `(logo_media_id) where logo_media_id is not null` | media usage and the orphan chore's referenced set; `409 media_in_use` |
| `sponsor_year_pkey` | pk `(id)` | |
| `sponsor_year_sponsor_id_event_year_key` | unique `(sponsor_id, event_year)` | the year upsert (`on conflict`); the snapshot builder's year join; cascade from `sponsor` |
| `person_pkey` | pk `(id)` | the `for update` in the cookie transaction; every person join |
| `person_cognito_sub_key` | unique `(cognito_sub)` | the per-request upsert (`on conflict (cognito_sub)`) |
| `subscriber_pkey` | pk `(id)` | |
| `subscriber_channel_address_key` | unique `(channel, address)` | `409 address_taken` (pre-check plus `23505` mapping) |
| `subscriber_unsubscribe_token_key` | unique `(unsubscribe_token)` | `POST /subscriptions/unsubscribe` |
| `subscriber_person` | `(person_id)` | `GET /me/subscriptions`; cascade from `person` |
| `subscriber_verify_token_hash` | `(verify_token_hash) where verify_token_hash is not null` | `POST /subscriptions/verify` lookup by hash |
| `cookie_type_pkey` | pk `(id)` | the type check in the cookie transaction |
| `cookie_pkey` | pk `(id)` | moderation |
| `cookie_event_person` | `(event_id, person_id)` | the limit count in the cookie transaction; `GET /me/cookies`; cascade from `event` |
| `cookie_event_type_visible` | `(event_id, cookie_type_id) where hidden_at is null` | the tally (`group by cookie_type_id`), read by every node every tick: an index-only pass over the event's visible cookies |
| `cookie_person` | `(person_id)` | `cookieCount` on `GET /admin/people` (one aggregate per listed person); cascade from `person` |
| `contact_message_pkey` | pk `(id)` | the admin list (keyset on `id`) and delete |
| `app_setting_pkey` | pk `(key)` | `PUT /admin/settings/{key}` |
| `media_asset_pkey` | pk `(id)` | every media read; the snapshot builder's map (`id = any($ids)`) |
| `media_asset_s3_key_key` | unique `(s3_key)` | never hit twice (the id is unique); documents the invariant |
| `media_asset_state_created` | `(state, created_at)` | the library list newest first with a state filter; the orphan chore's scans by state; the nightly pending cleanup |
| `page_pkey`, `page_slug_key` | pk `(id)`, unique `(slug)` | page reads; `409 slug_taken` (pre-check plus `23505` mapping) |
| `page_one_per_role` | unique `(role) where role <> 'none'` | the invariant of one page per role; the site's page choice reads the document, not this table |
| `section_pkey` | pk `(id)` | section writes |
| `section_page_position` | `(page_id, position, id)` | the page detail and the document builder in order; cascade from `page`; position compaction |
| `section_item_pkey` | pk `(id)` | item writes |
| `section_item_section_position` | `(section_id, position, id)` | items in order; cascade from `section` |
| `site_setting_draft_pkey`, `icon_library_state_pkey` | pk `(id)` | the single row |
| `content_version_pkey` | pk `(id)` | the newest row (`order by id desc limit 1`); the version list; restore; the prune (`id not in (select id ... order by id desc limit 50)`) |
| `preview_token_pkey`, `preview_token_token_hash_key` | pk `(id)`, unique `(token_hash)` | `GET /preview/document` lookup by hash |
| `snapshot_pkey`, `live_state_pkey` | pk `(id)` | the single row |
| `outbox_pkey` | pk `(id)` | mark published; cascade to `alert_delivery` |
| `outbox_unpublished` | `(id) where published_at is null` | the claim query: ordered scan of unpublished rows only; `attempts` and `claimed_at` are filtered on the heap |
| `alert_delivery_pkey` | pk `(id)` | mark sent |
| `alert_delivery_subscriber_id_outbox_id_key` | unique `(subscriber_id, outbox_id)` | fan-out `on conflict do nothing`; cascade from `subscriber` |
| `alert_delivery_unsent` | `(id) where sent_at is null` | the alert send chore: oldest unsent first |
| `alert_delivery_outbox` | `(outbox_id)` | cascade from `outbox`: the nightly cleanup deletes every outbox row published 30 days earlier, and after an event that is thousands of `subscription.verify` rows in one night, each of which would otherwise scan `alert_delivery`; per-alert progress queries |

### 4.2 Partial unique indexes and how the writers use them

Partial unique indexes are checked per statement, not at commit. The two "clear then set" writes therefore run as two statements in one transaction:

- Activate a beacon: `update beacon set is_active = false ... where is_active and id <> $1;` then `update beacon set is_active = true ... where id = $1;`. Two concurrent activations serialize on the second statement's row lock and on `beacon_one_active`; the loser receives `23505` and retries the transaction once.
- Set the current event: the same pattern on `event.is_current` with `event_one_current`.
- Set an event live: a single `update event set status_id = 3 ...`. The API pre-checks under the row lock (section 8.4); `event_one_live` is the concurrency guard, and a `23505` on it maps to `409 another_event_live`.

### 4.3 Unique violations the API maps to error codes

The API catches `PostgresException` with `SqlState = '23505'` and switches on `ConstraintName`:

| Constraint or index | Code |
|---|---|
| `event_year_key` | `409 year_taken` |
| `page_slug_key` | `409 slug_taken` |
| `page_one_per_role` | never raised (roles are set only by the seed and by restore, which keeps them) |
| `event_one_live` | `409 another_event_live` |
| `event_one_current` | retry the transaction once, then `500 internal_error` |
| `beacon_one_active` | retry the transaction once, then `500 internal_error` |
| `route_s3_key_key` | `200` with the existing row |
| `subscriber_channel_address_key` | `409 address_taken` |
| `alert_delivery_subscriber_id_outbox_id_key` | never raised (`on conflict do nothing`) |
| `person_cognito_sub_key` | never raised (`on conflict do update`) |
| `sponsor_year_sponsor_id_event_year_key` | never raised (`on conflict do update`) |

Every other `23505` and every `23503` (foreign key) or `23514` (check) is `500 internal_error`; the API validates inputs before they reach a constraint.

---

## 5. Foreign keys and on-delete behaviour

All foreign keys are `not deferrable` and are checked per statement. `no action` means the delete fails; the API refuses such deletes with a `409` before they reach the constraint.

| Column | References | On delete | Reason |
|---|---|---|---|
| `event.status_id` | `event_status.id` | no action | lookup rows are never deleted |
| `event.route_id` | `route.id` | no action | `DELETE /admin/routes/{id}` answers `409 route_in_use` while any event references the route |
| `event_status_history.event_id` | `event.id` | cascade | history goes with the event |
| `event_status_history.from_status_id`, `to_status_id` | `event_status.id` | no action | lookup |
| `event_message.event_id` | `event.id` | cascade | messages go with the event |
| `beacon_enrollment_token.beacon_id` | `beacon.id` | cascade | beacons are never deleted; the cascade keeps the schema self-consistent |
| `beacon_log.beacon_id` | `beacon.id` | cascade | same |
| `location.event_id` | `event.id` | no action | `DELETE /admin/events/{id}` answers `409 event_has_locations` when any location row exists |
| `location.beacon_id` | `beacon.id` | no action | beacons are never deleted |
| `sponsor.logo_media_id` | `media_asset.id` | no action | `DELETE /admin/media/{id}` answers `409 media_in_use` while any sponsor references the asset |
| `sponsor_year.sponsor_id` | `sponsor.id` | cascade | `DELETE /admin/sponsors/{id}` removes the years in the same statement |
| `section.page_id` | `page.id` | cascade | `DELETE /admin/pages/{id}` and restore remove sections |
| `section_item.section_id` | `section.id` | cascade | section delete, page delete, and restore remove items |
| `subscriber.person_id` | `person.id` | cascade | `DELETE /admin/people/{id}` removes subscriptions |
| `cookie.event_id` | `event.id` | cascade | `DELETE /admin/events/{id}` removes cookies |
| `cookie.person_id` | `person.id` | cascade | `DELETE /admin/people/{id}` removes cookies |
| `cookie.cookie_type_id` | `cookie_type.id` | no action | cookie types have no delete |
| `alert_delivery.outbox_id` | `outbox.id` | cascade | deliveries go with their outbox row (section 11) |
| `alert_delivery.subscriber_id` | `subscriber.id` | cascade | `DELETE /admin/subscribers/{id}` and the nightly unverified cleanup remove deliveries |

Every cascading foreign key has an index whose leading column is the referencing column (section 4.1), so a parent delete never scans a child table. `cookie_type.icon` and the content JSON reference media assets by id inside JSON, not by foreign key; the API and the orphan chore resolve those references (section 9.6). `beacon_enrollment_token.beacon_id` is the exception: the table holds a handful of rows and is deleted from by `beacon_id` only on rotate and revoke.

---

## 6. Seeds

| Table | Rows | When | Idempotency |
|---|---|---|---|
| `event_status` | the five statuses | initial migration | migration runs once |
| `live_state` | `(id = 1)`, every other column null | initial migration | migration runs once |
| `app_setting` | the five keys with the design defaults, `updated_by = 'seed'` | initial migration | `on conflict (key) do nothing` |
| `cookie_type` | Chocolate chip 10, Gingerbread 20, Snickerdoodle 30, Sugar 40, Happy 50; a library icon each, `active` true | initial migration | inserted only when the table is empty |
| `site_setting_draft`, `icon_library_state` | `(id = 1)`, every other column null or default | initial migration | migration runs once |
| `page`, `section`, `section_item`, `site_setting_draft.data` | the starter content: the six role pages (`no-event`, `planned`, `scheduled`, `live`, `ended`, `cancelled`) with a sensible section stack each, the ordinary pages `about`, `sponsors`, `route`, `donate`, `contact`, `alerts`, and the site settings, from `contracts/starter-content.json` (library icons only, no media) | first boot (section 8.16) | inserted only when `page` is empty |
| `content_version` | version 1: the starter content published | first boot (section 8.16) | inserted only when the table is empty |
| `snapshot` | version 1 | first boot (section 8.16) | inserted only when absent |

Sponsor special cookie types are admin-added rows, not seeds. The starter content is a placeholder site, not the real copy: editors replace it through the panel after cut-over (contracts 10 step 8). `GET /admin/settings` shows `updatedBy: "seed"` and the migration time for a setting no admin has touched; the compiled defaults stay in the API for a key whose row is missing.

---

## 7. The `snapshot` builder's reads

Every snapshot-affecting transaction (section 8.5) builds the snapshot object from these statements, run inside the transaction after the write so the object reflects the write.

```sql
-- current event, or no row
select e.id, e.year, e.name, e.status_id, e.scheduled_at, e.went_live_at, e.ended_at,
       e.funds_percent, r.url as route_url
from event e
left join route r on r.id = e.route_id
where e.is_current;

-- latest message of the current event
select id, body, event_time, created_at
from event_message
where event_id = $event_id
order by created_at desc, id desc
limit 1;

-- sponsors of the current event's year
select s.id, s.name, s.website_url, s.fb_url, s.ig_url, s.logo_media_id,
       y.amount_donated, y.can_advertise,
       agg.latest_year, agg.years_as_sponsor
from sponsor s
join sponsor_year y on y.sponsor_id = s.id and y.event_year = $year
join lateral (
  select max(event_year) as latest_year, count(distinct event_year) as years_as_sponsor
  from sponsor_year where sponsor_id = s.id
) agg on true
where y.active and not y.anonymous and y.can_advertise
order by y.amount_donated desc nulls last, s.name asc, s.id asc;

-- cookie types
select id, name, icon, sort from cookie_type where active order by sort, id;

-- settings (linger constants)
select key, value from app_setting;

-- the published content document
select id, document, media_ids from content_version order by id desc limit 1;

-- the media map: assets the document, the listed sponsors, and the cookie types reference
select id, s3_key, kind, width, height, alt, variants
from media_asset
where id = any($ids)          -- content_version.media_ids, plus sponsor.logo_media_id of the sponsors above, plus cookie_type.icon->>'id' where icon->>'source' = 'media'
  and state = 'ready'
order by id;
```

`lingerMs = max(sponsor_linger_min_ms, round(amount_donated * sponsor_linger_ms_per_dollar))`, or `sponsor_linger_min_ms` when `amount_donated` is null, computed in the API. `routeUrl`, `media[].url`, and `media[].variants` are `WMSFO_CDN_BASE_URL + '/' + key`; `icons` comes from the compiled library, not from a table. The content document is embedded verbatim. Every table here is tens of rows and the document is one row; plans are irrelevant.

---

## 8. Transaction recipes

Lock order, everywhere: `snapshot` row, then `event` row, then `beacon` row, then `person` row, then everything else. No recipe takes locks in another order, so no two recipes can deadlock. Reads of `snapshot` and `event` without `for update` never wait (MVCC).

Placeholders are `$name`. Every `update` on a table with `updated_at` sets it; shown once, implied afterwards.

### 8.1 Person upsert (every authenticated request)

Autocommit, one statement:

```sql
insert into person (cognito_sub, email) values ($sub, $email)
on conflict (cognito_sub) do update set email = excluded.email, last_seen_at = now()
returning id;
```

### 8.2 Location insert (REST door and hub door)

```sql
begin;
select id, status_id, next_seq from event where status_id = 3 for update;     -- none: rollback, 409 no_live_event
select is_active, revoked_at, key_version from beacon where id = $beacon;      -- revoked or stale key_version: rollback, 401 (REST) or 403 (message path)
insert into location (event_id, beacon_id, seq, recorded_at, received_at,
                      lat, lng, speed_mps, altitude_m, heading_deg, accuracy_m, published)
values ($event, $beacon, $next_seq, $recorded_at, now(),
        $lat, $lng, $speed_mps, $altitude_m, $heading_deg, $accuracy_m, $is_active)
returning seq, received_at;
update event  set next_seq = next_seq + 1, updated_at = now() where id = $event;
update beacon set last_seen_at = now(), last_location_at = now(), stale_since = null, updated_at = now()
  where id = $beacon;
select version, url from snapshot where id = 1;                               -- carried into the live object
commit;
```

The event row lock serializes seq assignment across nodes. The lock is held for the duration of the insert only; the CDN PUT and the hub publish happen after commit and outside any transaction. On the REST door the beacon was resolved before the transaction by `select id, role, is_active, revoked_at from beacon where key_hash = $hash` (revoked or unknown: `401`); the in-transaction re-read of `is_active` is what decides `published`.

### 8.3 Heartbeat

Autocommit:

```sql
update beacon
set last_heartbeat_at = now(), last_seen_at = now(), telemetry = $body, stale_since = null, updated_at = now()
where id = $beacon
returning is_active;
select id from event where status_id = 3;                                     -- liveEventId, or null
```

### 8.4 Status change (`POST /admin/events/{id}/status`)

```sql
begin;
select * from snapshot where id = 1 for update;                               -- serializes snapshot builders fleet-wide
select id, status_id, is_current, scheduled_at from event where id = $event for update;   -- none: 404
-- rules, evaluated in the API on the locked row:
--   same status                                     -> 409 event_status_unchanged
--   to 3 and not is_current                         -> 409 event_not_current
--   to 3 and exists (select 1 from event where status_id = 3 and id <> $event) -> 409 another_event_live
--   to 2 and scheduled_at is null                   -> 409 scheduled_at_required
update event
set status_id    = $to,
    went_live_at = case when $to = 3 then now() else went_live_at end,
    ended_at     = case when $to = 4 then now() else ended_at end,
    updated_at   = now()
where id = $event;
insert into event_status_history (event_id, from_status_id, to_status_id, changed_by)
values ($event, $from, $to, $admin_email);
insert into outbox (topic, payload)
values ('event.status_changed',
        jsonb_build_object('eventId', $event, 'fromStatusId', $from, 'toStatusId', $to, 'notify', $notify));
-- build the snapshot (section 7); canonicalize; hash; PUT snapshots/{sha256}.json (3 s, one attempt; failure: rollback, 502 snapshot_write_failed)
update snapshot
set version = version + 1, url = $cdn_base || '/' || $key, s3_key = $key, built_at = now()
where id = 1;
commit;
```

After commit: refresh memory from SQL (section 8.17), write the live object, publish, update `live_state`.

### 8.5 Generic snapshot-affecting admin write

Every write marked **[snapshot]** in the contracts runs this frame; the middle differs per endpoint.

```sql
begin;
select * from snapshot where id = 1 for update;
-- the write: one or more statements on event, event_message, sponsor, sponsor_year, cookie_type, or app_setting
-- example, PUT /admin/sponsors/{id}/years/{eventYear}:
insert into sponsor_year (sponsor_id, event_year, amount_donated, active, can_advertise, anonymous)
values ($sponsor, $year, $amount, $active, $can_advertise, $anonymous)
on conflict (sponsor_id, event_year) do update
  set amount_donated = excluded.amount_donated, active = excluded.active,
      can_advertise = excluded.can_advertise, anonymous = excluded.anonymous;
-- example, PUT /admin/settings/{key}:
insert into app_setting (key, value, updated_by, updated_at) values ($key, $value, $admin_email, now())
on conflict (key) do update set value = excluded.value, updated_by = excluded.updated_by, updated_at = now();
-- example, POST /admin/events/{id}/messages:
insert into event_message (event_id, body, event_time, created_by) values ($event, $body, $event_time, $admin_email) returning *;
insert into outbox (topic, payload) values ('event.message_posted', jsonb_build_object('eventId', $event, 'messageId', $message_id));   -- only when notify is true
-- build, hash, PUT (3 s, one attempt; failure: rollback, 502)
update snapshot set version = version + 1, url = $url, s3_key = $key, built_at = now() where id = 1;
commit;
```

`POST /admin/snapshot/rebuild` is this frame with no write. A content publish is this frame with the writes of section 8.19. A media `PATCH` (alt, title) is this frame with one `update media_asset` statement.

### 8.6 Set the current event (`POST /admin/events/{id}/current`)

```sql
begin;
select * from snapshot where id = 1 for update;
select id, is_current from event where id = $event for update;               -- none: 404; already current: rollback, 200 (no rebuild)
select id from event where is_current and status_id = 3 and id <> $event;     -- any row: rollback, 409 current_event_live
update event set is_current = false, updated_at = now() where is_current and id <> $event;
update event set is_current = true,  updated_at = now() where id = $event;
-- build, hash, PUT; update snapshot; commit
```

### 8.7 Create an event (`POST /admin/events`)

```sql
begin;
select * from snapshot where id = 1 for update;
-- inheritRoute true: route_id = (select route_id from event where route_id is not null order by year desc limit 1), or null
-- inheritRoute false: route_id = $route_id, checked with select 1 from route where id = $route_id (none: 404)
insert into event (year, name, status_id, scheduled_at, funds_percent, route_id, created_by)
values ($year, $name, 1, $scheduled_at, $funds_percent, $route_id, $admin_email)
returning *;                                                                  -- 23505 on event_year_key: 409 year_taken
-- build, hash, PUT; update snapshot; commit
```

### 8.8 Delete an event (`DELETE /admin/events/{id}`)

```sql
begin;
select * from snapshot where id = 1 for update;
select id, status_id from event where id = $event for update;                -- none: 404; status 3: 409 event_live
select 1 from location where event_id = $event limit 1;                       -- any: 409 event_has_locations
delete from event where id = $event;                                          -- cascades event_status_history, event_message, cookie
-- build, hash, PUT; update snapshot; commit
```

### 8.9 Cookie insert (`POST /cookies`)

```sql
begin;
select id from person where id = $person for update;                          -- serializes this person's inserts
select id from event where status_id = 3;                                     -- none: rollback, 409 no_live_event
select id from cookie_type where id = $type and active;                       -- none: rollback, 404 not_found
select value from app_setting where key = 'cookie_limit_per_person';          -- missing row: compiled default 10
select count(*) from cookie where event_id = $event and person_id = $person;  -- hidden cookies count; >= limit: rollback, 409 cookie_limit_reached
insert into cookie (event_id, person_id, cookie_type_id, note)
values ($event, $person, $type, $note)
returning id, left_at;
commit;
```

The person row lock makes the count-then-insert safe against the same person's concurrent requests. The event row is read without a lock; a status change committing between the read and the commit can admit one cookie on an event that ended a few milliseconds earlier, which is accepted. After commit the node increments its in-memory tally for `$type`.

### 8.10 Cookie moderation

Autocommit, one statement each:

```sql
update cookie set hidden_at = now(), hidden_by = $admin_email where id = $cookie and hidden_at is null returning *;   -- hide
update cookie set hidden_at = null,  hidden_by = null        where id = $cookie returning *;                          -- unhide
delete from cookie where id = $cookie;                                                                                 -- delete
```

Then, when the cookie's event has status 3, the node re-reads the tally (section 8.17) and writes the live object.

### 8.11 Beacon writes

Create:

```sql
begin;
insert into beacon (name, notes, role, key_hash, key_prefix, created_by)
values ($name, $notes, $role, $key_hash, $key_prefix, $admin_email)
returning *;
insert into beacon_enrollment_token (beacon_id, token_hash, key_ciphertext, expires_at)
values ($beacon, $token_hash, $key_ciphertext, now() + interval '15 minutes')
returning expires_at;
commit;
```

Rotate:

```sql
begin;
select revoked_at from beacon where id = $beacon for update;                  -- none: 404; revoked: 409 beacon_revoked
update beacon set key_hash = $key_hash, key_prefix = $key_prefix, key_version = key_version + 1, updated_at = now()
  where id = $beacon;
delete from beacon_enrollment_token where beacon_id = $beacon and consumed_at is null;
insert into beacon_enrollment_token (beacon_id, token_hash, key_ciphertext, expires_at)
values ($beacon, $token_hash, $key_ciphertext, now() + interval '15 minutes');
commit;
```

Revoke (idempotent):

```sql
begin;
update beacon set revoked_at = coalesce(revoked_at, now()), is_active = false, updated_at = now() where id = $beacon;   -- 0 rows: 404
delete from beacon_enrollment_token where beacon_id = $beacon and consumed_at is null;
commit;
```

Activate:

```sql
begin;
select revoked_at from beacon where id = $beacon for update;                  -- none: 404; revoked: 409 beacon_revoked
update beacon set is_active = false, updated_at = now() where is_active and id <> $beacon;
update beacon set is_active = true,  updated_at = now() where id = $beacon;
commit;
```

Deactivate (idempotent): `update beacon set is_active = false, updated_at = now() where id = $beacon;`

Enroll (`POST /beacons/enroll`):

```sql
begin;
select t.id, t.beacon_id, t.key_ciphertext, b.name, b.role, b.revoked_at
from beacon_enrollment_token t
join beacon b on b.id = t.beacon_id
where t.token_hash = $token_hash and t.consumed_at is null and t.expires_at > now()
for update of t;                                                              -- none, or b.revoked_at not null: 404 enrollment_token_invalid
update beacon_enrollment_token set consumed_at = now(), key_ciphertext = null where id = $token;
commit;
```

The key is decrypted in the API from `key_ciphertext` before the update nulls it.

Authorize callback, ingest branch (autocommit): `select id, key_version from beacon where key_hash = $hash and revoked_at is null;` then `update beacon set last_seen_at = now() where id = $beacon;`. Message path identity check (autocommit): `select revoked_at, key_version from beacon where id = $beacon;`.

### 8.12 Subscriptions

Create (`POST /me/subscriptions`):

```sql
begin;
select id, person_id, verified_at, unsubscribed_at
from subscriber where channel = 'email' and address = $address for update;
-- row exists, person_id <> $person                              -> 409 address_taken
-- row exists, same person, verified, not unsubscribed            -> 409 already_subscribed
-- row exists, same person, unverified, not unsubscribed          -> 200 existing row, no token, no outbox row
-- row exists, same person, unsubscribed:
update subscriber set unsubscribed_at = null, updated_at = now() where id = $subscriber;
-- no row:
insert into subscriber (person_id, channel, address, unsubscribe_token)
values ($person, 'email', $address, $unsubscribe_token) returning id;
-- when the row is not verified (new, or re-activated unverified):
update subscriber set verify_token_hash = $verify_hash, verify_expires_at = now() + interval '24 hours', updated_at = now()
  where id = $subscriber;
insert into outbox (topic, payload)
values ('subscription.verify', jsonb_build_object('subscriberId', $subscriber, 'verifyToken', $verify_token));
commit;
```

Resend verification: the last `update` and `insert` above on a row with `verified_at is null` (`409 already_verified` otherwise).

Verify (`POST /subscriptions/verify`), autocommit:

```sql
select id, verified_at, verify_expires_at from subscriber where verify_token_hash = $hash;   -- none: 404
-- verified_at not null: 200 with the existing value (expiry ignored)
-- verify_expires_at <= now(): 404
update subscriber set verified_at = now(), updated_at = now() where id = $subscriber and verified_at is null returning verified_at;
```

Unsubscribe (`POST /subscriptions/unsubscribe`, `DELETE /me/subscriptions/{id}`), autocommit:

```sql
update subscriber set unsubscribed_at = coalesce(unsubscribed_at, now()), updated_at = now()
where unsubscribe_token = $token;                                             -- or: where id = $id and person_id = $person; 0 rows: 404
```

### 8.13 Contact (`POST /contact`)

```sql
begin;
insert into contact_message (name, email, body, client_ip) values ($name, $email, $body, $client_ip) returning id, created_at;
insert into outbox (topic, payload) values ('contact.received', jsonb_build_object('contactMessageId', $id));
commit;
```

### 8.14 Route upload (`POST /admin/routes`)

```sql
select * from route where s3_key = $key;                                      -- row: 200 that row, nothing written
-- PUT routes/{sha256}.json (failure: 502 route_write_failed)
insert into route (name, s3_key, url, sha256, point_count, uploaded_by)
values ($name, $key, $url, $sha256, $point_count, $admin_email)
returning *;                                                                  -- 23505 on route_s3_key_key: select the existing row, 200
```

Delete: `select 1 from event where route_id = $route limit 1;` (any: `409 route_in_use`), `delete from route where id = $route;`, then delete the object.

### 8.15 People and admin deletes

```sql
delete from person where id = $person;                                        -- cascades subscriber (and its alert_delivery rows) and cookie
delete from subscriber where id = $subscriber;                                -- cascades alert_delivery
delete from contact_message where id = $id;
delete from sponsor where id = $sponsor;                                      -- inside the section 8.5 frame; cascades sponsor_year; objects deleted after commit
```

### 8.16 First boot: snapshot version 1

Run by the node holding the migration advisory lock (section 14), after migrations, in this order, each step skipped when its condition is already met:

1. **Starter content**, when `select 1 from page limit 1` returns nothing: insert the pages, sections, items, and `site_setting_draft.data` from `contracts/starter-content.json` with `created_by = updated_by = 'seed'` in one transaction.
2. **Icon library**, when `icon_library_state.library_sha256` differs from the compiled library's hash: PUT every `icons/{sha256}.svg` with the immutable header (one attempt, 3 s each; any failure aborts this step and the boot retries below), then `update icon_library_state set library_sha256 = $hash, written_at = now() where id = 1`.
3. **Content version 1**, when `select 1 from content_version limit 1` returns nothing: the publish recipe (8.19) with `published_by = 'seed'` and `label = 'Starter content'`, minus the snapshot rebuild, which the next step does.
4. **Snapshot version 1**, when `select 1 from snapshot where id = 1` returns nothing:

```sql
begin;
-- build the snapshot (section 7): on a fresh database event is null, sponsors is [], cookieTypes is the seed set, content is version 1
-- canonicalize; hash; PUT snapshots/{sha256}.json
insert into snapshot (id, version, url, s3_key, built_at) values (1, 1, $url, $key, now());
commit;
```

   On a later deploy that changed the icon library (step 2 ran and the snapshot row exists), run the rebuild frame (8.5 with no write) instead, so the new icon URLs reach the site.

A PUT failure in any step releases the lock, leaves that step's condition unmet, keeps `GET /api/health` at `503`, and retries every 5 s (re-taking the lock each time). After the snapshot commit the node writes the live object under the admin-path rule.

### 8.17 Memory refresh (after every commit that changes what the node publishes, and on every tick)

```sql
select version, url from snapshot where id = 1;
select id, status_id from event where is_current;
select id from beacon where is_active;
select seq, lat, lng, speed_mps, altitude_m, heading_deg, accuracy_m, recorded_at, received_at
from location where event_id = $current and published order by seq desc limit 1;
select cookie_type_id, count(*) from cookie where event_id = $current and hidden_at is null group by cookie_type_id;
select key, value from app_setting;                                           -- on version change, and at least every 5 s
```

### 8.18 `live_state` after every live-object write attempt

```sql
update live_state
set last_write_at = now(), last_write_seq = $seq, last_write_version = $version, last_write_node = $node, last_write_error = null
where id = 1;                                                                 -- success
update live_state set last_write_error = $error, last_write_node = $node where id = 1;   -- final failure
```

### 8.19 Content publish (`POST /admin/content/publish`)

The frame of 8.5 with the version insert as the write. Validation runs first and reads only.

```sql
begin;
select * from snapshot where id = 1 for update;
-- read the working set in document order
select id, slug, title, nav_label, nav_position, role from page where not is_hidden
order by case role when 'no_event' then 0 when 'planned' then 1 when 'scheduled' then 2 when 'live' then 3 when 'ended' then 4 when 'cancelled' then 5 else 6 end, nav_position, id;
select id, page_id, kind, presentation, data from section where not is_hidden and page_id = any($page_ids) order by page_id, position, id;
select id, section_id, data from section_item where not is_hidden and section_id = any($section_ids) order by section_id, position, id;
select data from site_setting_draft where id = 1;
-- build the document; validate at the publish level; on problems: rollback, 422 content_invalid
-- referenced media must be ready
select id from media_asset where id = any($referenced_media_ids) and state = 'ready';   -- every id must come back
-- canonicalize; hash
select sha256 from content_version order by id desc limit 1;                             -- equal: rollback, 409 content_unchanged
insert into content_version (document, sha256, media_ids, label, published_by)
values ($document, $sha256, $referenced_media_ids, $label, $admin_email) returning id, published_at;
delete from content_version where id not in (select id from content_version order by id desc limit 50);
-- build the snapshot (section 7), hash, PUT (3 s, one attempt; failure: rollback, 502)
update snapshot set version = version + 1, url = $url, s3_key = $key, built_at = now() where id = 1;
commit;
```

Hidden pages, sections, and items are read but excluded (`not is_hidden`); a hidden page's sections are excluded with it. Publish-level problems are reported with the page, section, and item ids so the panel can link to them.

### 8.20 Restore (`POST /admin/content/versions/{id}/restore`)

```sql
begin;
select document from content_version where id = $id;                     -- none: 404
delete from page;                                                          -- sections and items cascade
insert into page (slug, title, nav_label, nav_position, role, created_by, updated_by) values (...) returning id;   -- per page in the document, role kept
insert into section (page_id, kind, position, data, presentation, updated_by) values (...) returning id;           -- per section, position = index
insert into section_item (section_id, position, data, updated_by) values (...);                                   -- per item
update site_setting_draft set data = $settings, updated_by = $admin_email, updated_at = now() where id = 1;
commit;
```

The document was published, so it contains exactly one page per role; the restore therefore recreates all six. Rows get new ids. Nothing touches `content_version` or `snapshot`.

### 8.21 Working-set writes

Every page, section, and item write is one short transaction with the row locked, draft validation done in the API before the statement, and position compaction where order changes:

```sql
-- insert a section at position p
begin;
select id from page where id = $page for update;
update section set position = position + 1 where page_id = $page and position >= $p;
insert into section (page_id, kind, position, data, presentation, updated_by) values ($page, $kind, $p, $data, $presentation, $admin_email) returning *;
commit;

-- delete a section
begin;
delete from section where id = $id returning page_id;
update section s set position = r.rn - 1
from (select id, row_number() over (order by position, id) rn from section where page_id = $page_id) r
where s.id = r.id and s.position <> r.rn - 1;
commit;

-- reorder (PUT .../sections/order): the API checks the id set equals the page's set, then
update section set position = v.pos from (values ($id1, 0), ($id2, 1), ...) as v (id, pos) where section.id = v.id and section.page_id = $page;

-- move to another page
begin;
select id from page where id in ($from, $to) order by id for update;
update section set page_id = $to, position = $p, updated_by = $admin_email, updated_at = now() where id = $id;
-- compact both pages as in the delete
commit;

-- duplicate
insert into section (page_id, kind, position, is_hidden, data, presentation, updated_by)
select page_id, kind, position + 1, is_hidden, data, presentation, $admin_email from section where id = $id returning id;
insert into section_item (section_id, position, is_hidden, data, updated_by)
select $new_id, position, is_hidden, data, $admin_email from section_item where section_id = $id;
-- then compact the page
```

`PUT /admin/site-settings` is `update site_setting_draft set data = $data, updated_by = $admin_email, updated_at = now() where id = 1`. `PUT /admin/pages/order` sets `nav_position = index * 10` for the listed `none` pages.

### 8.22 Media

```sql
-- upload ticket (POST /admin/media/upload-url): the id is minted in the API
insert into media_asset (id, filename, content_type, kind, state, s3_key, size_bytes, alt, title, uploaded_by)
values ($id, $filename, $content_type, $kind, 'pending', 'media/' || $id || '/' || $filename, $declared_size, $alt, $title, $admin_email);

-- confirm (POST /admin/media/{id}/confirm): after the object was read, validated, and the variants were PUT
update media_asset
set state = 'ready', size_bytes = $size, width = $w, height = $h, sha256 = $sha, variants = $variants, confirmed_at = now()
where id = $id and state = 'pending';       -- 0 rows: 409 media_not_pending

-- confirm failure on validation: the object is deleted, then
delete from media_asset where id = $id and state = 'pending';

-- usage (GET /admin/media/{id}/usage, and the pre-check of DELETE)
select id, slug, title from page p
where exists (select 1 from section s where s.page_id = p.id and (s.data::text like '%' || $id || '%' or s.presentation::text like '%' || $id || '%'))
   or exists (select 1 from section s join section_item i on i.section_id = s.id where s.page_id = p.id and i.data::text like '%' || $id || '%');
select count(*) from content_version where $id = any(media_ids);
select id, name from sponsor where logo_media_id = $id;
select id, name from cookie_type where icon->>'source' = 'media' and icon->>'id' = $id::text;
select data::text like '%' || $id || '%' from site_setting_draft where id = 1;

-- delete (DELETE /admin/media/{id}): after every object under media/{id}/ was deleted
delete from media_asset where id = $id;
```

The text-match usage queries are exact because the id is a UUID that cannot occur by accident; the working set is hundreds of rows, so a scan is fine. The reference set the orphan chore uses (9.6) is computed the same way over the whole working set in one pass.

### 8.23 Preview token

```sql
-- mint (POST /admin/content/preview-token)
insert into preview_token (token_hash, created_by, expires_at) values (sha256($token), $admin_email, now() + interval '15 minutes');
-- resolve (GET /preview/document?token=)
select 1 from preview_token where token_hash = sha256($token) and expires_at > now();   -- none: 404 preview_token_invalid
```

The document served is built exactly as in 8.19 (working set, hidden rows omitted) without validation, plus the media map for its references (ready or not; a pending reference renders as a missing image in the preview, which is what an editor wants to see) and the icon map.

---

## 9. Outbox and leader chores

All chores run on the leader only and are idempotent; leadership may overlap for one loop.

### 9.1 Claim (every 2 s)

```sql
update outbox
set claimed_at = now(), attempts = attempts + 1
where id in (
  select id from outbox
  where published_at is null
    and attempts < 5
    and (claimed_at is null or claimed_at < now() - interval '2 minutes')
  order by id
  limit 50
  for update skip locked
)
returning id, topic, payload, attempts;
```

`outbox_unpublished` yields unpublished rows in `id` order; `attempts` and `claimed_at` are heap filters on that short list. `for update skip locked` lets two overlapping leaders claim disjoint rows; the 2-minute reclaim covers a leader that died holding claims. Each claimed row is processed per topic, then:

```sql
update outbox set published_at = now(), last_error = null where id = $id;    -- success
update outbox set last_error = $error where id = $id;                        -- failure; attempts already counted by the claim
```

### 9.2 Topic processing

| Topic | SQL |
|---|---|
| `event.status_changed` with `notify` true and `toStatusId` 2 or 3; `event.message_posted` | fan-out below, then mark published |
| `event.status_changed` otherwise | mark published |
| `event.message_posted` whose message or event no longer exists (`select 1 from event_message where id = $message_id and event_id = $event_id`) | `update outbox set published_at = now(), last_error = 'source_deleted' where id = $id` |
| `subscription.verify` | `select address from subscriber where id = $subscriber_id and verified_at is null and unsubscribed_at is null;` send; mark published (a missing or verified row: mark published, send nothing) |
| `contact.received` | `select name, email, body from contact_message where id = $id;` send; mark published |

Fan-out:

```sql
insert into alert_delivery (outbox_id, subscriber_id)
select $outbox_id, id
from subscriber
where channel = 'email' and verified_at is not null and unsubscribed_at is null
on conflict (subscriber_id, outbox_id) do nothing;
```

One sequential scan of `subscriber` per alert (about 20k rows), a few times per year.

### 9.3 Alert send (every 5 s, at most `WMSFO_ALERT_SEND_PER_SEC` per second)

```sql
select d.id, d.outbox_id, d.attempts, s.address, s.unsubscribe_token, o.topic, o.payload
from alert_delivery d
join subscriber s on s.id = d.subscriber_id
join outbox o on o.id = d.outbox_id
where d.sent_at is null and d.attempts < 5
order by d.id
limit $batch
for update of d skip locked;
```

Per row: `update alert_delivery set sent_at = now(), ses_message_id = $mid, last_error = null where id = $id;` on success, `update alert_delivery set attempts = attempts + 1, last_error = $error where id = $id;` on failure. The leader holds the batch's row locks only while selecting; sends happen outside the transaction, which is why overlapping leaders can double-send a row within one loop at most, and the unique constraint bounds the damage to one email per subscriber per alert.

### 9.4 Stale beacon flag (every 15 s)

```sql
update beacon
set stale_since = now(), updated_at = now()
where revoked_at is null
  and stale_since is null
  and last_seen_at is not null
  and greatest(coalesce(last_heartbeat_at, '-infinity'), coalesce(last_location_at, '-infinity'))
      < now() - make_interval(secs => $beacon_stale_after_s);
```

A sequential scan of a table with tens of rows.

### 9.5 Nightly cleanup (09:00 UTC)

```sql
delete from beacon_enrollment_token
where (consumed_at is not null and consumed_at < now() - interval '24 hours')
   or (consumed_at is null and expires_at < now() - interval '24 hours');
delete from outbox where published_at < now() - interval '30 days';           -- cascades alert_delivery
delete from subscriber where verified_at is null and created_at < now() - interval '7 days';
delete from beacon_log where received_at < now() - interval '30 days';
delete from preview_token where expires_at < now() - interval '24 hours';
delete from media_asset where state = 'pending' and created_at < now() - interval '2 days';   -- the lifecycle rule removed the object after 1 day, if it ever arrived
```

Each statement is idempotent; two leaders running it in the same minute delete nothing twice.

### 9.6 Media orphan collection (every hour)

```sql
-- the referenced set, one pass over the working set, the retained versions, sponsors, cookie types
with refs as (
  select unnest(media_ids) as id from content_version
  union select logo_media_id from sponsor where logo_media_id is not null
  union select (icon->>'id')::uuid from cookie_type where icon->>'source' = 'media'
  union select m.id from media_asset m where exists (
    select 1 from section s where s.data::text like '%' || m.id || '%' or s.presentation::text like '%' || m.id || '%')
  union select m.id from media_asset m where exists (
    select 1 from section_item i where i.data::text like '%' || m.id || '%')
  union select m.id from media_asset m, site_setting_draft d where d.data::text like '%' || m.id || '%'
)
select id from refs;

update media_asset set unreferenced_since = now() where state = 'ready' and unreferenced_since is null and id <> all($refs);
update media_asset set unreferenced_since = null where state = 'ready' and unreferenced_since is not null and id = any($refs);
select id, s3_key, variants from media_asset where state = 'ready' and unreferenced_since < now() - interval '30 days';
  -- per row: PutObjectTagging state=orphaned on the original and every variant, then
  update media_asset set state = 'orphaned', orphaned_at = now() where id = $id and state = 'ready';
select id, s3_key, variants from media_asset where state = 'orphaned' and id = any($refs);
  -- per row: DeleteObjectTagging on every object, then
  update media_asset set state = 'ready', orphaned_at = null, unreferenced_since = null where id = $id and state = 'orphaned';
delete from media_asset where state = 'orphaned' and orphaned_at < now() - interval '8 days';
```

The library is hundreds of rows; the text matches scan the working set once per hour. A tag call that fails leaves the row as it was and is retried next hour; every statement is idempotent.

---

## 10. Hot-path queries and their plans

Plans below are what `explain` shows against a database holding one event of data (20k locations, 20k people, 60k cookies, 20k subscribers). Every hot path is an index lookup returning a bounded number of rows; nothing on a request path scans a table that grows with the event. On the tables that stay at tens of rows (`event`, `beacon`, `beacon_enrollment_token`, `event_message`, `outbox` between claims) the planner reads the single heap page directly instead of the index; the index is chosen the moment a second page exists, and its uniqueness guarantee holds either way.

| Path | Query | Index | Plan |
|---|---|---|---|
| Beacon REST auth (`POST /locations`, heartbeat, `GET /beacons/me`) | `select id, role, is_active, revoked_at, key_version from beacon where key_hash = $1` | `beacon_key_hash_key` | Index Scan, 1 row |
| Authorize callback, ingest topic | `select id, key_version from beacon where key_hash = $1 and revoked_at is null` | `beacon_key_hash_key` | Index Scan, 1 row, filter on `revoked_at` |
| Message path identity check | `select revoked_at, key_version from beacon where id = $1` | `beacon_pkey` | Index Scan, 1 row |
| Location transaction, live event lock | `select id, status_id, next_seq from event where status_id = 3 for update` | `event_one_live` | Index Scan on the partial index, 0 or 1 row, LockRows |
| Location transaction, insert | `insert into location ...` | maintains `location_pkey`, `location_event_id_seq_key`, `location_event_published_seq` (partial, only when `published`) | Insert, two or three index maintenances |
| Location transaction, counters | `update event ... where id = $1`; `update beacon ... where id = $1`; `select ... from snapshot where id = 1` | `event_pkey`, `beacon_pkey`, `snapshot_pkey` | Index Scan, 1 row each |
| Heartbeat | `update beacon ... where id = $1` | `beacon_pkey` | Index Scan, 1 row |
| Heartbeat `liveEventId` | `select id from event where status_id = 3` | `event_one_live` | Index Scan, 0 or 1 row |
| Person upsert | `insert ... on conflict (cognito_sub) do update` | `person_cognito_sub_key` | Insert with ON CONFLICT arbiter index |
| Cookie transaction, person lock | `select id from person where id = $1 for update` | `person_pkey` | Index Scan, LockRows |
| Cookie transaction, type | `select id from cookie_type where id = $1 and active` | `cookie_type_pkey` | Index Scan, 1 row |
| Cookie transaction, limit count | `select count(*) from cookie where event_id = $1 and person_id = $2` | `cookie_event_person` | Aggregate over Index Only Scan, at most `cookie_limit_per_person` rows |
| `GET /me/cookies` | `select ... from cookie where event_id = $1 and person_id = $2 order by id` | `cookie_event_person` | Index Scan plus Sort of at most the limit rows |
| Reconcile tick, current event | `select id, status_id from event where is_current` | `event_one_current` | Index Scan on the partial index, 0 or 1 row |
| Reconcile tick, active beacon | `select id from beacon where is_active` | `beacon_one_active` | Index Scan on the partial index, 0 or 1 row |
| Reconcile tick, latest published location | `select ... from location where event_id = $1 and published order by seq desc limit 1` | `location_event_published_seq`, or `location_event_id_seq_key` | Limit over Index Scan on the partial index (already `seq desc`) when unpublished rows are common; Limit over Index Scan Backward on the unique constraint's index with a `published` filter when nearly every row is published. Either reads a handful of entries and returns 1 row |
| Reconcile tick, tally | `select cookie_type_id, count(*) from cookie where event_id = $1 and hidden_at is null group by cookie_type_id` | `cookie_event_type_visible` | GroupAggregate over Index Only Scan of the event's visible cookies: 60k cookies read 50 index pages in about 2.5 ms with zero heap fetches; with several events in the table the same index is used through a Bitmap Heap Scan |
| Reconcile tick, snapshot and settings | `select version, url from snapshot where id = 1`; `select key, value from app_setting` | `snapshot_pkey`; none | Index Scan, 1 row; Seq Scan of 5 rows |
| Snapshot builder, latest message | `select ... from event_message where event_id = $1 order by created_at desc, id desc limit 1` | `event_message_event_created` | Limit over Incremental Sort (presorted on `created_at`, ties broken on `id`) over Index Scan |
| Outbox claim | section 9.1 | `outbox_unpublished` | LockRows over Limit over Index Scan in `id` order, heap filters on `attempts` and `claimed_at` |
| Alert send batch | section 9.3 | `alert_delivery_unsent`, `subscriber_pkey`, `outbox_pkey` | Limit over Nested Loop with Index Scan on the partial index |
| Verify | `select ... from subscriber where verify_token_hash = $1` | `subscriber_verify_token_hash` | Index Scan, 1 row |
| Unsubscribe | `update subscriber ... where unsubscribe_token = $1` | `subscriber_unsubscribe_token_key` | Index Scan, 1 row |
| Enroll | section 8.11 | `beacon_enrollment_token_token_hash_key`, `beacon_pkey` | Nested Loop of two Index Scans, LockRows |
| `GET /admin/events/{id}/cookies` newest first | `select ... from cookie where event_id = $1 and ($cursor is null or id < $cursor) [and hidden_at is null] order by id desc limit $limit + 1` | `cookie_pkey` | Limit over Index Scan Backward with `event_id` (and `hidden_at`) as filters; the live event's cookies are the newest ids, so the first page is reached immediately; paging an older event skips newer events' rows, acceptable for an admin history view |
| `GET /admin/events/{id}/locations` | `select ... from location where event_id = $1 and seq > $cursor [and beacon_id = $b] [and published] order by seq limit $limit + 1` | `location_event_id_seq_key` (or `location_event_published_seq` with `publishedOnly`) | Limit over Index Scan; the CSV export walks the same index in 5,000-row keyset batches |
| `GET /admin/people` | `select p.*, (select count(*) from cookie c where c.person_id = p.id) as cookie_count from person p where ($cursor is null or p.id < $cursor) order by p.id desc limit $limit + 1` | `person_pkey`, `cookie_person` | Limit over Index Scan Backward with a SubPlan Aggregate over Index Only Scan per row |
| `GET /admin/subscribers`, `/contact-messages` | keyset on `id desc` with the status filter as a heap filter | `subscriber_pkey`, `contact_message_pkey` | Limit over Index Scan Backward; a filtered page reads past non-matching rows of a 20k-row table, which is acceptable for an admin list |

Paging: every paged list uses keyset paging on `id` (descending for newest first, ascending by `seq` for locations); the opaque `cursor` is the boundary value encoded as URL-safe base64; the query asks for `limit + 1` rows and sets `nextCursor` from the last returned row when the extra row exists.

Subscriber status filter: `verified` is `verified_at is not null and unsubscribed_at is null`; `pending` is `verified_at is null and unsubscribed_at is null`; `unsubscribed` is `unsubscribed_at is not null`. `GET /admin/subscribers/summary` is one `select count(*) filter (where ...)` over the table with the same three predicates.

---

## 11. Retention

| Table | Rule | Enforced by |
|---|---|---|
| `location` | Forever | nothing deletes rows; an event with rows cannot be deleted |
| `event`, `event_status_history`, `event_message`, `route`, `sponsor`, `sponsor_year`, `cookie_type`, `person`, `beacon` | Until an admin deletes (beacons never) | admin endpoints |
| `cookie` | Until an admin deletes | moderation |
| `contact_message` | Until an admin deletes | admin endpoint |
| `beacon_enrollment_token` | 24 h after expiry or consumption; also deleted by rotate and revoke while pending | nightly cleanup; beacon writes |
| `beacon_log` | 30 days | nightly cleanup |
| `outbox` | 30 days after `published_at`; rows that never publish (5 attempts) stay | nightly cleanup |
| `alert_delivery` | With its outbox row, 30 days after that row was published, through the cascade. No statement of its own. |
| `subscriber` | Unverified rows 7 days after creation; verified and unsubscribed rows until an admin or the person deletes | nightly cleanup; endpoints |
| `page`, `section`, `section_item`, `site_setting_draft` | Until an editor deletes or a restore replaces | editor endpoints |
| `content_version` | Newest 50 rows | the publish transaction |
| `media_asset` | `pending` 2 days; `ready` until unreferenced for 30 days, then `orphaned` for 8 days; referenced assets forever | nightly cleanup; orphan chore; `DELETE /admin/media/{id}` |
| `preview_token` | 24 h after expiry | nightly cleanup |
| `app_setting`, `snapshot`, `live_state`, `event_status`, `icon_library_state` | Permanent | |

Autovacuum defaults are sufficient: the only tables that churn are `location` (insert only), `cookie` (insert, rare update), `beacon` (one update per heartbeat and per fix on a handful of rows), `outbox` and `alert_delivery` (claim and send updates). `beacon` is the one hot-update table; with tens of rows it stays within a few pages whatever the update rate.

---

## 12. Database and role bootstrap

Run once per environment by the RDS master user, before the first deploy. The master user creates the database and the roles and is never used by the API or the tool.

```sql
-- as the RDS master user, connected to the postgres database
create role wmsfo_migrate_dev login password '<migrate-password>' nosuperuser nocreatedb nocreaterole noinherit;
create role wmsfo_app_dev     login password '<app-password>'     nosuperuser nocreatedb nocreaterole noinherit;
grant wmsfo_migrate_dev to <db-master-user>;   -- the RDS master user is not a superuser and can only assign ownership to a role it is a member of
create database wmsfo_dev owner wmsfo_migrate_dev encoding 'UTF8' template template0;
revoke all on database wmsfo_dev from public;
grant connect on database wmsfo_dev to wmsfo_migrate_dev, wmsfo_app_dev;

alter role wmsfo_app_dev in database wmsfo_dev set statement_timeout = '10s';
alter role wmsfo_app_dev in database wmsfo_dev set lock_timeout = '5s';
alter role wmsfo_app_dev in database wmsfo_dev set idle_in_transaction_session_timeout = '15s';
alter role wmsfo_app_dev in database wmsfo_dev set timezone = 'UTC';
alter role wmsfo_migrate_dev in database wmsfo_dev set statement_timeout = 0;
alter role wmsfo_migrate_dev in database wmsfo_dev set lock_timeout = '60s';
alter role wmsfo_migrate_dev in database wmsfo_dev set timezone = 'UTC';
```

```sql
-- as wmsfo_migrate_dev, connected to wmsfo_dev
revoke create on schema public from public;
grant usage on schema public to wmsfo_app_dev;
alter default privileges for role wmsfo_migrate_dev in schema public
  grant select, insert, update, delete on tables to wmsfo_app_dev;
```

Prod is the same with `prod` in place of `dev`. The default privileges apply to every table the migrate role creates afterwards, so the initial migration and every later one need no grant statements. Identity columns draw from their sequences through the table's insert privilege; no sequence grant exists, and the app role cannot call `setval` or `nextval` directly. The app role cannot create, alter, drop, or truncate anything, and has no access to `__EFMigrationsHistory` beyond what the default privileges give every table (select, insert, update, delete), which the API never uses. The tool in section 15 runs as the migrate role.

Timeouts: `statement_timeout = 10s` bounds every API statement, including a location `for update` waiting behind a snapshot transaction (at most about 3 s). `idle_in_transaction_session_timeout = 15s` bounds a snapshot-affecting transaction that is idle while its 3 s S3 PUT runs and kills any transaction the API leaks. `lock_timeout = 5s` turns a lock convoy into an error instead of a stall. The migrate role has no statement timeout because index builds and the legacy copy run as single statements, and a 60 s lock timeout so a migration blocked by a stray session fails the boot with a clear error instead of hanging.

---

## 13. Connection settings

Two connection strings in the container env. The secret carries the contract's connection string values; the API sets the pool parameters below in code with `NpgsqlConnectionStringBuilder` on top of them, so the secret stays minimal and the settings cannot be forgotten.

| Env key | Role | Use |
|---|---|---|
| `WMSFO_DB_CONNECTION` | `wmsfo_app_<env>` | every request, tick, and chore |
| `WMSFO_DB_MIGRATION_CONNECTION` | `wmsfo_migrate_<env>` | the boot migrator only (section 14); also the connection the tool in section 15 takes on its command line |

```
Host=<db-host>;Port=5432;Database=wmsfo_dev;Username=wmsfo_app_dev;Password=<password>;SSL Mode=Require;Trust Server Certificate=false
```

```csharp
var app = new NpgsqlConnectionStringBuilder(config["WMSFO_DB_CONNECTION"])
{
    ApplicationName = config["WMSFO_SERVICE_NAME"],
    MaxPoolSize = 10,
    MinPoolSize = 1,
    ConnectionIdleLifetime = 300,
    ConnectionPruningInterval = 10,
    Timeout = 5,             // seconds to obtain a connection
    CommandTimeout = 10,     // client-side cancel; matches the role's statement_timeout
    KeepAlive = 30,
    MaxAutoPrepare = 0,      // <db-host> may be an RDS proxy; prepared statements pin sessions
};
var migrate = new NpgsqlConnectionStringBuilder(config["WMSFO_DB_MIGRATION_CONNECTION"])
{
    ApplicationName = config["WMSFO_SERVICE_NAME"] + "-migrate",
    MaxPoolSize = 2,
    MinPoolSize = 0,
    Timeout = 15,
    CommandTimeout = 300,
};
```

Pool sizing: one node's steady state during an event is one location transaction per second, one heartbeat per 15 s, one tick per second, one leader loop, a few cookie and person writes per second, and admin traffic; every statement completes in single-digit milliseconds, so 10 connections per node is headroom, not a limit. Fleet budget: `10 × nodes` app connections plus 2 per node for the seconds of a boot. At 12 nodes that is 120 connections on the shared instance; the RDS `max_connections` for the instance class must leave that much room after the other databases' users.

TLS: `SSL Mode=Require` with `Trust Server Certificate=false` validates the server certificate against the container's trust store, so the image installs the RDS certificate bundle for `<region>` into the system CA directory. The connection string is not extended with a certificate path.

The API opens no transaction outside the recipes in section 8; every other statement runs in autocommit. The `for update` lock on the `snapshot` row is held across an S3 PUT by design, with the 3 s PUT timeout and the 15 s idle-in-transaction timeout as the two bounds.

---

## 14. Migrations

### 14.1 Tooling

EF Core migrations with the Npgsql provider, in the API project under `Migrations/`, applied on boot by every node under a fleet-wide advisory lock. `GET /api/health` answers `503` until the node's migrator has finished and the `snapshot` row exists.

```csharp
public sealed class DatabaseMigrator
{
    public const string LockKey = "wmsfo_migrations";   // pg_advisory_lock(hashtext('wmsfo_migrations'))

    public async Task MigrateAsync(CancellationToken ct)
    {
        await using var lock_ = new NpgsqlConnection(migrateConnectionString);
        await lock_.OpenAsync(ct);
        await Exec(lock_, "select pg_advisory_lock(hashtext('wmsfo_migrations'))", ct);
        try
        {
            await using var db = dbFactory.CreateForMigration();           // WmsfoDbContext on migrateConnectionString
            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
            if (pending.Count > 0) await db.Database.MigrateAsync(ct);
            await snapshotBootstrap.EnsureVersionOneAsync(ct);             // section 8.16, on the app connection; throws on PUT failure
        }
        finally
        {
            await Exec(lock_, "select pg_advisory_unlock(hashtext('wmsfo_migrations'))", CancellationToken.None);
        }
    }
}
```

The lock is session-scoped on a dedicated connection, so it is held for the whole migration and the version 1 build, and a dropped connection releases it. Every node runs this on boot; one migrates while the others block, then find nothing pending and continue. A failed migration throws; the hosted service retries with backoff (5 s, 10 s, 30 s, then every 60 s) and the node stays at `503`. EF Core's own migration lock is redundant under the advisory lock and stays enabled.

The migrator uses the migrate connection (`WMSFO_DB_MIGRATION_CONNECTION`); everything after it uses the app connection.

### 14.2 The model

`WmsfoDbContext` maps one entity per table. Rules the model follows so the generated migration is the DDL in section 3:

| Rule | Configuration |
|---|---|
| Identity always | `modelBuilder.UseIdentityAlwaysColumns()` |
| snake_case tables and columns | `optionsBuilder.UseSnakeCaseNamingConvention()` (EFCore.NamingConventions); DbSets are singular (`Event`, `EventStatusHistory`), so tables come out `event`, `event_status_history` |
| Constraint and index names | set explicitly to the names in section 4.1: `HasKey(...).HasName("event_pkey")`, `HasAlternateKey(...).HasName("event_year_key")`, `HasIndex(...).HasDatabaseName("event_one_live")`, `HasOne(...).WithMany(...).HasConstraintName("location_event_id_fkey")`, `ToTable(t => t.HasCheckConstraint("event_funds_percent_check", "funds_percent between 0 and 100"))` |
| Partial indexes | `HasIndex(...).IsUnique().HasFilter("status_id = 3")` |
| Column types | `HasColumnType` for `smallint`, `char(64)`, `numeric(12,2)`, `jsonb`, `bytea`, `uuid`, `double precision`; `text` is the default string mapping |
| Defaults | `HasDefaultValueSql("now()")`, `HasDefaultValue(false)`, `HasDefaultValue(1L)` for `next_seq`, `HasDefaultValue("")` for `beacon.notes` |
| Comments | `HasComment(...)` on tables and columns with the texts in section 3 |
| On delete | `OnDelete(DeleteBehavior.Cascade)` or `OnDelete(DeleteBehavior.NoAction)` per section 5; `NoAction` emits no `on delete` clause, which is Postgres' default `no action` and matches the DDL |
| `jsonb` columns | mapped as `JsonDocument` (`beacon.telemetry`, `outbox.payload`, `app_setting.value`) |
| Single-row tables | `HasCheckConstraint("snapshot_id_check", "id = 1")` |

The API queries through EF for CRUD and through `ExecuteSqlRaw` / Dapper-style Npgsql commands for the recipes in sections 8 and 9 where `for update`, `skip locked`, `on conflict`, and `returning` matter; the SQL in this document is the SQL the API runs.

### 14.3 Generating the initial migration

1. Configure the model per 14.2.
2. `dotnet ef migrations add Initial --project src/Wmsfo.Api --startup-project src/Wmsfo.Api --output-dir Migrations`.
3. In the generated `Up()`, after the generated statements, append `migrationBuilder.Sql(...)` blocks with the seeds of section 6: the `event_status` rows, the `live_state` row, the `app_setting` rows, the guarded `cookie_type` insert.
4. Review the generated SQL with `dotnet ef migrations script --idempotent` against section 3; every table, column, type, default, constraint name, index name, and filter must match. Differences are model configuration mistakes and are fixed in the model, then the migration is regenerated (`migrations remove`, `migrations add`).
5. `Down()` drops everything; it is never run in an environment.

CI runs the migration against an empty Postgres service container and fails on `dotnet ef migrations has-pending-model-changes`, and the integration tests assert the partial unique indexes by provoking `23505` on `event_one_live`, `event_one_current`, and `beacon_one_active`.

### 14.4 Later migrations

- One migration per change; names describe the change (`AddFinalCookieTally`).
- Additive by default: add nullable columns or columns with defaults; drop columns in a later release after the code stopped reading them.
- `create index concurrently` cannot run inside a transaction: such a migration is generated with `[Migration]` on a class whose `Up()` uses `migrationBuilder.Sql(..., suppressTransaction: true)`; everything else runs in EF's per-migration transaction.
- Never a data backfill that infers state; a data change is an explicit `update` with a fixed value or none at all.
- The same migrations run in dev first (branch `dev`) and in prod on the `main` deploy.

---

## 15. Legacy migration (one-off tool)

The tool is a project in the API solution (`tools/migrate`), references the API's DTOs and serializer options so its route object is byte-identical to the API's, and takes on its command line: the legacy database connection string, the new database connection string (the migrate role), the bucket name, `--cdn-base-url`, and `--event-name-format` (default `Santa Flyover {year}`). It writes only the new database and the bucket. It is idempotent by natural key and can be rerun after a failure or to pick up rows added to the legacy database before the freeze.

Run order and unit of work:

| Step | Unit | Idempotency key |
|---|---|---|
| 1 | Legacy beacon row | `beacon where name = 'legacy' and created_by = 'migration'` |
| 2 | `sponsor` rows, then `setval`; per sponsor with a legacy full logo, one `media_asset` (section 15.10) and `logo_media_id` | `sponsor.id` (preserved); `media_asset` by `title = 'legacy:' || legacy key` |
| 3 | `sponsor_year` rows, then `setval` | `(sponsor_id, event_year)`; ids preserved |
| 4 | `app_setting sponsor_linger_ms_per_dollar` | key |
| 5 | Per `flight_history` year, one transaction: `event` row, all `location` rows, `next_seq` | `event.year` |
| 6 | 2025 route object and `route` row, `event.route_id` | `route.s3_key` |
| 7 | `event_message` rows, then `setval` | `event_message.id` (preserved) |
| 8 | `event.funds_percent` per year | `event.year` (update) |
| 9 | `contact_message` rows | the tuple `(name, email, body, created_at)` |
| 10 | Nothing; step 2 covers logos | |

Every migrated row has `created_by = 'migration'` where the column exists. Legacy `timestamp without time zone` values are read as UTC. Legacy epoch-millisecond values (`flight_history.time` bigint; `funds.created` and `messages.created` text) are converted with `to_timestamp(ms / 1000.0)`. A row's year, where it has no year column, is `extract(year from ts at time zone 'America/Denver')`.

### 15.1 Table mapping

| Legacy table (rows) | Disposition | New table |
|---|---|---|
| `flight_history` (8590) | transformed | `event` (one per year, 2020 to 2025), `location`, `route` (2025) |
| `event_updates` (31) | transformed | `event_message` |
| `sponsors` (7) | mapped | `sponsor` |
| `sponsor_years` (25) | mapped | `sponsor_year` |
| `sponsor_settings` (1) | transformed | `app_setting` |
| `funds` (11) | transformed | `event.funds_percent` |
| `messages` (45) | mapped | `contact_message` |
| `event_mode_override` (16) | retired | |
| `gps_location` (1467) | retired | |
| `liftoff` (3) | retired | |
| `tracki_device` (2), `tracki_device_data` (0) | retired | |
| `tracking_mode` (77), `tracking_mode_type` (3) | retired | |

Retired tables are not read. Their content (mode overrides, the last season's raw phone and tracker points, liftoff stamps, device and mode configuration) has no counterpart: modes are replaced by event status, devices by beacons, and live points by `location` rows that only exist from the first v2 event on.

### 15.2 Legacy beacon

```sql
insert into beacon (name, notes, role, key_hash, key_prefix, is_active, revoked_at, created_by)
values ('legacy', 'Synthetic sender for migrated flight_history rows.', 'beacon',
        $random_key_sha256, $random_key_first_12, false, now(), 'migration')
returning id;
```

The key is generated per the beacon key format and discarded; `revoked_at` is set so the row can never authenticate.

### 15.3 `flight_history` to `event`, `location`, `route`

| Legacy column | New | Rule |
|---|---|---|
| `year` integer | `event.year` | one event per distinct year |
| | `event.name` | `--event-name-format` with `{year}` replaced |
| | `event.status_id` | 4 |
| | `event.is_current` | false |
| | `event.scheduled_at` | null |
| | `event.went_live_at` | `to_timestamp(min(time) / 1000.0)` over the year |
| | `event.ended_at` | `to_timestamp(max(time) / 1000.0)` over the year |
| | `event.funds_percent` | step 8; 0 until then |
| | `event.route_id` | 2025 only, step 6 |
| | `event.next_seq` | `count(*) + 1` over the year |
| | `event.created_by` | `'migration'` |
| | `event.created_at`, `event.updated_at` | `now()` at run time |
| `seq` integer | `location.seq` | renumbered 1 to n in `order by seq, id`; the legacy value is not kept |
| `time` bigint (epoch ms) | `location.recorded_at`, `location.received_at` | `to_timestamp(time / 1000.0)` for both |
| `lat` numeric, `lng` numeric | `location.lat`, `location.lng` | cast to `double precision` |
| | `location.beacon_id` | the legacy beacon |
| | `location.published` | true |
| | `location.speed_mps`, `altitude_m`, `heading_deg`, `accuracy_m` | null |
| `id` integer | | not carried |

Per year, one transaction:

```sql
begin;
insert into event (year, name, status_id, is_current, went_live_at, ended_at, next_seq, created_by)
values ($year, $name, 4, false, $min_time, $max_time, $count + 1, 'migration')
returning id;
insert into location (event_id, beacon_id, seq, recorded_at, received_at, lat, lng, published)
select $event, $legacy_beacon,
       row_number() over (order by p.seq, p.id),
       to_timestamp(p.time / 1000.0), to_timestamp(p.time / 1000.0),
       p.lat, p.lng, true
from unnest($ids::int[], $seqs::int[], $times::bigint[], $lats::float8[], $lngs::float8[]) as p (id, seq, time, lat, lng);
commit;
```

The tool reads the year's `flight_history` rows from the legacy database into memory (8590 rows across all years) and passes them as array parameters, so the whole year is one insert statement. A year whose `event` row exists is skipped whole, so a crash between the two inserts leaves nothing behind.

Route (step 6): the 2025 points in `seq` order become `{ schemaVersion: 1, name: "2025 flight", points: [{ lat, lng, recordedAt }] }` with `recordedAt` from `time`; the tool canonicalizes and hashes with the API's serializer, PUTs `routes/{sha256}.json` with the immutable header, and:

```sql
insert into route (name, s3_key, url, sha256, point_count, uploaded_by)
values ('2025 flight', $key, $cdn_base || '/' || $key, $sha256, $count, 'migration')
on conflict (s3_key) do nothing;
update event set route_id = (select id from route where s3_key = $key), updated_at = now() where year = 2025;
```

### 15.4 `event_updates` to `event_message`

| Legacy column | New | Rule |
|---|---|---|
| `id` integer | `event_message.id` | preserved (`overriding system value`) |
| `message` text | `body` | as is |
| `time` timestamptz (nullable) | `event_time` | as is |
| `created_at` timestamptz | `created_at`, `updated_at` | as is |
| | `event_id` | the event whose `year` equals the Denver calendar year of `coalesce(time, created_at)`; no such event: logged and skipped |
| | `created_by` | `'migration'` |

```sql
insert into event_message (id, event_id, body, event_time, created_by, created_at, updated_at)
overriding system value
values ($id, $event, $message, $time, 'migration', $created_at, $created_at)
on conflict (id) do nothing;
select setval(pg_get_serial_sequence('event_message', 'id'), (select max(id) from event_message));
```

### 15.5 `sponsors` to `sponsor`

| Legacy column | New | Rule |
|---|---|---|
| `id` integer | `id` | preserved |
| `name` varchar | `name` | as is |
| `contact_person`, `email`, `phone`, `address`, `website_url`, `fb_url`, `ig_url` varchar | same names, `text` | as is |
| `logo_s3_key` varchar | `logo_media_id` | the object becomes a media asset (section 15.10); null when absent or invalid |
| `logo_small_s3_key` varchar | not migrated | the variants replace it |
| `created_at`, `updated_at` timestamp (no tz, nullable) | same names | `at time zone 'UTC'`; null becomes `now()` |

```sql
insert into sponsor (id, name, contact_person, email, phone, address, website_url, fb_url, ig_url,
                     logo_media_id, created_at, updated_at)
overriding system value
values (...)
on conflict (id) do nothing;
select setval(pg_get_serial_sequence('sponsor', 'id'), (select max(id) from sponsor));
```

### 15.6 `sponsor_years` to `sponsor_year`

| Legacy column | New | Rule |
|---|---|---|
| `id` integer | `id` | preserved |
| `sponsor_id` integer (nullable) | `sponsor_id` | null: logged and skipped |
| `event_year` integer | `event_year` | as is |
| `amount_donated` numeric (nullable, default 0) | `amount_donated numeric(12,2)` | rounded to two decimals; a negative value is logged and stored as null |
| `active`, `can_advertise`, `anonymous` boolean (nullable) | same names | null takes the column default (true, true, false) |
| `registered_at` timestamp (no tz, nullable) | `registered_at` | `at time zone 'UTC'`; null becomes `now()` |

Insert with `overriding system value ... on conflict (sponsor_id, event_year) do nothing`, then `setval` as above.

### 15.7 `sponsor_settings` to `app_setting`

```sql
insert into app_setting (key, value, updated_by, updated_at)
values ('sponsor_linger_ms_per_dollar', to_jsonb($linger_rate_ms_per_dollar), 'migration', coalesce($updated_at, now()))
on conflict (key) do update set value = excluded.value, updated_by = excluded.updated_by, updated_at = excluded.updated_at;
```

The other four settings keep their seed rows.

### 15.8 `funds` to `event.funds_percent`

| Legacy column | Rule |
|---|---|
| `created` text (epoch ms) | parsed with `created ~ '^\d+$'`, then `to_timestamp(created::bigint / 1000.0)`; year = Denver calendar year; unparseable: logged and skipped |
| `percent` (bigint in the schema reference; read as `percent::text`) | trimmed, must match `^-?\d+$`, parsed as integer, clamped to 0 to 100; unparseable: logged and skipped |
| `id` text (random hex) | not carried |

For each year, the row with the greatest `created` wins: `update event set funds_percent = $percent, updated_at = now() where year = $year;`. A year with no event is logged and skipped.

### 15.9 `messages` to `contact_message`

| Legacy column | New | Rule |
|---|---|---|
| `name` text | `name` | as is |
| `email` text | `email` | as is |
| `message` text | `body` | as is |
| `created` text (epoch ms) | `created_at` | same parse as `funds.created`; unparseable: logged and skipped |
| `id` text (client-generated) | | not carried; `contact_message.id` is assigned |
| | `client_ip` | `''` |

Rows are inserted in `created` order so `id` order follows time. Idempotency: `insert ... where not exists (select 1 from contact_message where name = $name and email = $email and body = $body and created_at = $created_at)`.

### 15.10 Legacy logo objects

For every sponsor whose legacy `logo_s3_key` names an object in the legacy bucket: read the bytes, run the API's confirm pipeline (sniff, SVG validation, decode, variants), mint a UUID, PUT `media/{uuid}/{filename}` (the legacy key's last segment sanitized to the media filename rule) and the variants to the new bucket with the immutable header, insert the `media_asset` row (`state = 'ready'`, `uploaded_by = 'migration'`, `alt` = the sponsor name, `title = 'legacy:' || legacy key`), and set `sponsor.logo_media_id`. Idempotent on `title`: a rerun finds the existing row and only relinks. Legacy small logos are not copied. A logo that fails validation is logged and skipped; the sponsor keeps a null logo and an editor uploads a fresh one.

### 15.11 After the tool

`POST /admin/snapshot/rebuild` (cut-over step 4) publishes the migrated sponsors and settings. The new year's event is created by the admin with `inheritRoute: true` and set current; it inherits the 2025 route through `event.route_id`.

---

## 16. Decisions made here

- `alert_delivery` rows live and die with their outbox row (30 days after publish, by cascade); there is no separate delivery retention.

- No `location_event_seq` index; the unique constraint `location_event_id_seq_key` and the partial `location_event_published_seq` index are the only indexes on `location`.

- `event.final_cookie_tally jsonb` is in the initial DDL: written in the status change transaction on entry into status 4 as `(select jsonb_object_agg(cookie_type_id::text, n) from (select cookie_type_id, count(*) n from cookie where event_id = $event and hidden_at is null group by 1) t)`, nulled on exit from 4, and read by the live-object builder in place of the memory tally while the event has status 4.

- Two roles per environment: `wmsfo_migrate_<env>` owns the database and runs migrations and the migration tool; `wmsfo_app_<env>` serves requests and cannot run DDL (section 12).

- Two roles per environment: `wmsfo_migrate_<env>` owns the database and runs migrations and the tool; `wmsfo_app_<env>` (the contracts' role name) is the API's runtime role with DML only. The container env gains `WMSFO_DB_MIGRATION_CONNECTION`.
- `app_setting` is seeded with the five defaults (`updated_by = 'seed'`); the compiled default for a missing row stays as the fallback.
- `cookie_type` is seeded with Chocolate chip, Gingerbread, Snickerdoodle, Sugar, Happy (sort 10 to 50) only when the table is empty; sponsor specials are admin-added.
- No triggers; the API writes `updated_at` in every update statement.
- Constraint and index names are the Postgres defaults, fixed explicitly in the EF model; the API maps `23505` to error codes by constraint name (section 4.3).
- Three indexes beyond the contracts' list: `subscriber_verify_token_hash`, `cookie_person`, `alert_delivery_outbox`.
- No sequence grants: identity columns are used through the table's insert privilege.
- Paged lists use keyset paging on `id` (or `seq` for locations) with `limit + 1`; the cursor is the boundary value in URL-safe base64.
- Subscriber `status` filter: verified = verified and not unsubscribed; pending = unverified and not unsubscribed; unsubscribed = `unsubscribed_at` set.
- Read committed everywhere; lock order `snapshot`, `event`, `beacon`, `person`; the cookie transaction reads the live event without locking it.
- Role-level timeouts: app 10 s statement, 5 s lock, 15 s idle in transaction; migrate no statement timeout, 60 s lock. Pool 10 per node (app), 2 (migrate); automatic statement preparation off.
- The image carries the RDS certificate bundle so the contract's connection string validates TLS unchanged.
- Migration bootstrap retries 5 s, 10 s, 30 s, then every 60 s while health stays `503`.
- Migration tool: `event_message`, `sponsor`, and `sponsor_year` ids are preserved; `messages.id` and `funds.id` (client-generated hex text) are not, and `contact_message` is idempotent on `(name, email, body, created_at)`; unparseable epochs are logged and skipped; negative `amount_donated` becomes null; legacy events get `scheduled_at` null and `created_at = now()`; each year is one transaction.
- Nightly cleanup has no run-once guard; every statement is idempotent.
- Postgres 13 or newer, no extensions.
- Section `data` and `presentation`, item `data`, and the site settings are `jsonb` validated by the API against the contract schemas; the database enforces only the `kind` and `role` domains and the one-page-per-role index.
- The published document is stored whole in `content_version.document` with its media ids alongside; the snapshot embeds it verbatim; the newest 50 rows are kept by a delete inside the publish transaction.
- Media usage and the orphan chore's referenced set are computed by UUID text matching over the JSON columns plus the two typed references (`sponsor.logo_media_id`, `cookie_type.icon`); no reference table is maintained.
- `media_asset.id` is minted by the API so the S3 key exists before the row is confirmed; `s3_key` is unique as documentation of the invariant.
- Restore recreates the working set with new ids and touches neither `content_version` nor `snapshot`.
- First boot seeds the starter content, writes the icon library, publishes content version 1, and builds snapshot version 1, in that order under the migration lock; a library change on a later deploy rewrites the icons and rebuilds the snapshot the same way.
- Preview tokens are rows, not memory, so any node answers the preview read.

## 17. Needs a decision

Nothing at the moment. Add here as it comes up.
