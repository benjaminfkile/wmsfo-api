namespace Wmsfo.Api.Data.Sql;

// The SQL text of every recipe in sql.md 8 and 9, kept as string constants so
// the handlers of later tasks reference them by name and this file is the one
// place the SQL lives. Placeholders (@name) are Npgsql parameters; anything
// interpolated (interval literals, e.g.) is fixed text.

public static class TransactionRecipes
{
    // 8.1 Person upsert.
    public const string PersonUpsert = @"
insert into person (cognito_sub, email) values (@sub, @email)
on conflict (cognito_sub) do update
  set email = excluded.email,
      last_seen_at = now()
returning id;";

    // 8.2 Location insert - location and event/beacon reads, insert and stamps.
    public const string LocationLockLiveEvent = @"
select id, status_id, next_seq
from event
where status_id = 3
for update;";

    public const string LocationReadBeacon = @"
select is_active, revoked_at, key_version
from beacon
where id = @beacon_id;";

    public const string LocationInsert = @"
insert into location (event_id, beacon_id, seq, recorded_at, received_at,
                      lat, lng, speed_mps, altitude_m, heading_deg, accuracy_m, published)
values (@event_id, @beacon_id, @seq, @recorded_at, now(),
        @lat, @lng, @speed_mps, @altitude_m, @heading_deg, @accuracy_m, @published)
returning seq, received_at;";

    public const string LocationEventBumpSeq = @"
update event
set next_seq = next_seq + 1,
    updated_at = now()
where id = @event_id;";

    public const string LocationBeaconStamp = @"
update beacon
set last_seen_at = now(),
    last_location_at = now(),
    stale_since = null,
    updated_at = now()
where id = @beacon_id;";

    public const string SnapshotRead = @"
select version, url
from snapshot
where id = 1;";

    // 8.3 Heartbeat.
    public const string HeartbeatUpdate = @"
update beacon
set last_heartbeat_at = now(),
    last_seen_at = now(),
    telemetry = @telemetry,
    stale_since = null,
    updated_at = now()
where id = @beacon_id
returning is_active;";

    public const string HeartbeatLiveEventId = @"
select id
from event
where status_id = 3;";

    // 8.4 Status change.
    public const string SnapshotLock = @"
select id, version, url, s3_key, built_at
from snapshot
where id = 1
for update;";

    public const string EventLockById = @"
select id, status_id, is_current, scheduled_at
from event
where id = @event_id
for update;";

    public const string EventUpdateStatus = @"
update event
set status_id = @to_status,
    went_live_at = case when @to_status = 3 then now() else went_live_at end,
    ended_at     = case when @to_status = 4 then now() else ended_at end,
    updated_at   = now()
where id = @event_id;";

    public const string EventStatusHistoryInsert = @"
insert into event_status_history (event_id, from_status_id, to_status_id, changed_by)
values (@event_id, @from_status, @to_status, @admin_email);";

    public const string OutboxInsertStatusChanged = @"
insert into outbox (topic, payload)
values ('event.status_changed',
        jsonb_build_object('eventId', @event_id, 'fromStatusId', @from_status,
                           'toStatusId', @to_status, 'notify', @notify));";

    public const string SnapshotUpdateAfterWrite = @"
update snapshot
set version = version + 1,
    url = @url,
    s3_key = @s3_key,
    built_at = now()
where id = 1;";

    // 8.5 Generic snapshot-affecting write examples.
    public const string SponsorYearUpsert = @"
insert into sponsor_year (sponsor_id, event_year, amount_donated, active, can_advertise, anonymous)
values (@sponsor_id, @event_year, @amount, @active, @can_advertise, @anonymous)
on conflict (sponsor_id, event_year) do update
  set amount_donated = excluded.amount_donated,
      active         = excluded.active,
      can_advertise  = excluded.can_advertise,
      anonymous      = excluded.anonymous;";

    public const string AppSettingUpsert = @"
insert into app_setting (key, value, updated_by, updated_at)
values (@key, @value, @admin_email, now())
on conflict (key) do update
  set value = excluded.value,
      updated_by = excluded.updated_by,
      updated_at = now();";

    public const string EventMessageInsert = @"
insert into event_message (event_id, body, event_time, created_by)
values (@event_id, @body, @event_time, @admin_email)
returning id, event_id, body, event_time, created_by, created_at, updated_at;";

    public const string OutboxInsertMessagePosted = @"
insert into outbox (topic, payload)
values ('event.message_posted',
        jsonb_build_object('eventId', @event_id, 'messageId', @message_id));";

    // 8.6 Set current event.
    public const string EventCurrentPeek = @"
select id, is_current
from event
where id = @event_id
for update;";

    public const string EventCurrentAnotherLive = @"
select id
from event
where is_current and status_id = 3 and id <> @event_id;";

    public const string EventCurrentClear = @"
update event
set is_current = false,
    updated_at = now()
where is_current and id <> @event_id;";

    public const string EventCurrentSet = @"
update event
set is_current = true,
    updated_at = now()
where id = @event_id;";

    // 8.7 Create event.
    public const string EventInheritRoute = @"
select route_id
from event
where route_id is not null
order by year desc
limit 1;";

    public const string EventCheckRoute = @"
select 1 from route where id = @route_id;";

    public const string EventInsert = @"
insert into event (year, name, status_id, scheduled_at, funds_percent, route_id, created_by)
values (@year, @name, 1, @scheduled_at, @funds_percent, @route_id, @admin_email)
returning id, year, name, status_id, is_current, scheduled_at, went_live_at, ended_at,
          funds_percent, route_id, final_cookie_tally, next_seq, created_by, created_at, updated_at;";

    // 8.8 Delete event.
    public const string EventLocationExists = @"
select 1 from location where event_id = @event_id limit 1;";

    public const string EventDelete = @"
delete from event where id = @event_id;";

    // 8.9 Cookie insert.
    public const string CookiePersonLock = @"
select id from person where id = @person_id for update;";

    public const string CookieLiveEvent = @"
select id from event where status_id = 3;";

    public const string CookieTypeActive = @"
select id from cookie_type where id = @type_id and active;";

    public const string CookieLimitRead = @"
select value from app_setting where key = 'cookie_limit_per_person';";

    public const string CookieCount = @"
select count(*) from cookie where event_id = @event_id and person_id = @person_id;";

    public const string CookieInsert = @"
insert into cookie (event_id, person_id, cookie_type_id, note)
values (@event_id, @person_id, @cookie_type_id, @note)
returning id, left_at;";

    // 8.10 Cookie moderation.
    public const string CookieHide = @"
update cookie
set hidden_at = now(),
    hidden_by = @admin_email
where id = @cookie_id and hidden_at is null
returning id, event_id, person_id, cookie_type_id, note, left_at, hidden_at, hidden_by;";

    public const string CookieUnhide = @"
update cookie
set hidden_at = null,
    hidden_by = null
where id = @cookie_id
returning id, event_id, person_id, cookie_type_id, note, left_at, hidden_at, hidden_by;";

    public const string CookieDelete = @"
delete from cookie where id = @cookie_id;";

    // 8.11 Beacon writes.
    public const string BeaconCreate = @"
insert into beacon (name, notes, role, key_hash, key_prefix, created_by)
values (@name, @notes, @role, @key_hash, @key_prefix, @admin_email)
returning id, name, notes, role, key_hash, key_prefix, key_version, is_active,
          revoked_at, last_seen_at, last_location_at, last_heartbeat_at, stale_since,
          telemetry, created_by, created_at, updated_at;";

    public const string BeaconEnrollmentTokenInsert = @"
insert into beacon_enrollment_token (beacon_id, token_hash, key_ciphertext, expires_at)
values (@beacon_id, @token_hash, @key_ciphertext, now() + interval '15 minutes')
returning expires_at;";

    public const string BeaconRotateLock = @"
select revoked_at from beacon where id = @beacon_id for update;";

    public const string BeaconRotateKey = @"
update beacon
set key_hash = @key_hash,
    key_prefix = @key_prefix,
    key_version = key_version + 1,
    updated_at = now()
where id = @beacon_id;";

    public const string BeaconRotateClearTokens = @"
delete from beacon_enrollment_token
where beacon_id = @beacon_id and consumed_at is null;";

    public const string BeaconRevoke = @"
update beacon
set revoked_at = coalesce(revoked_at, now()),
    is_active = false,
    updated_at = now()
where id = @beacon_id;";

    public const string BeaconActivateClear = @"
update beacon
set is_active = false,
    updated_at = now()
where is_active and id <> @beacon_id;";

    public const string BeaconActivateSet = @"
update beacon
set is_active = true,
    updated_at = now()
where id = @beacon_id;";

    public const string BeaconDeactivate = @"
update beacon
set is_active = false,
    updated_at = now()
where id = @beacon_id;";

    public const string BeaconEnrollLookup = @"
select t.id, t.beacon_id, t.key_ciphertext, b.name, b.role, b.revoked_at
from beacon_enrollment_token t
join beacon b on b.id = t.beacon_id
where t.token_hash = @token_hash
  and t.consumed_at is null
  and t.expires_at > now()
for update of t;";

    public const string BeaconEnrollConsume = @"
update beacon_enrollment_token
set consumed_at = now(),
    key_ciphertext = null
where id = @token_id;";

    public const string BeaconAuthorizeLookup = @"
select id, key_version
from beacon
where key_hash = @key_hash and revoked_at is null;";

    public const string BeaconMarkSeen = @"
update beacon set last_seen_at = now() where id = @beacon_id;";

    public const string BeaconMessagePathIdentity = @"
select revoked_at, key_version from beacon where id = @beacon_id;";

    // 8.12 Subscriptions.
    public const string SubscriberLookupForCreate = @"
select id, person_id, verified_at, unsubscribed_at
from subscriber
where channel = 'email' and address = @address
for update;";

    public const string SubscriberReactivate = @"
update subscriber
set unsubscribed_at = null,
    updated_at = now()
where id = @subscriber_id;";

    public const string SubscriberInsert = @"
insert into subscriber (person_id, channel, address, unsubscribe_token)
values (@person_id, 'email', @address, @unsubscribe_token)
returning id;";

    public const string SubscriberSetVerifyToken = @"
update subscriber
set verify_token_hash = @verify_hash,
    verify_expires_at = now() + interval '24 hours',
    updated_at = now()
where id = @subscriber_id;";

    public const string OutboxInsertSubscriptionVerify = @"
insert into outbox (topic, payload)
values ('subscription.verify',
        jsonb_build_object('subscriberId', @subscriber_id, 'verifyToken', @verify_token));";

    public const string SubscriberVerifyLookup = @"
select id, verified_at, verify_expires_at
from subscriber
where verify_token_hash = @hash;";

    public const string SubscriberVerify = @"
update subscriber
set verified_at = now(),
    updated_at = now()
where id = @subscriber_id and verified_at is null
returning verified_at;";

    public const string SubscriberUnsubscribeByToken = @"
update subscriber
set unsubscribed_at = coalesce(unsubscribed_at, now()),
    updated_at = now()
where unsubscribe_token = @token;";

    public const string SubscriberUnsubscribeById = @"
update subscriber
set unsubscribed_at = coalesce(unsubscribed_at, now()),
    updated_at = now()
where id = @subscriber_id and person_id = @person_id;";

    // 8.13 Contact.
    public const string ContactMessageInsert = @"
insert into contact_message (name, email, body, client_ip)
values (@name, @email, @body, @client_ip)
returning id, created_at;";

    public const string OutboxInsertContactReceived = @"
insert into outbox (topic, payload)
values ('contact.received',
        jsonb_build_object('contactMessageId', @contact_message_id));";

    // 8.14 Route upload.
    public const string RouteLookupByKey = @"
select id, name, s3_key, url, sha256, point_count, uploaded_by, created_at
from route where s3_key = @s3_key;";

    public const string RouteInsert = @"
insert into route (name, s3_key, url, sha256, point_count, uploaded_by)
values (@name, @s3_key, @url, @sha256, @point_count, @admin_email)
returning id, name, s3_key, url, sha256, point_count, uploaded_by, created_at;";

    public const string RouteInUse = @"
select 1 from event where route_id = @route_id limit 1;";

    public const string RouteDelete = @"
delete from route where id = @route_id;";

    // 8.15 People and admin deletes.
    public const string PersonDelete = @"delete from person where id = @person_id;";
    public const string SubscriberDelete = @"delete from subscriber where id = @subscriber_id;";
    public const string ContactMessageDelete = @"delete from contact_message where id = @contact_message_id;";
    public const string SponsorDelete = @"delete from sponsor where id = @sponsor_id;";

    // 8.16 First-boot snapshot version 1.
    public const string PageExistsCheck = @"select 1 from page limit 1;";
    public const string ContentVersionExistsCheck = @"select 1 from content_version limit 1;";
    public const string SnapshotExistsCheck = @"select 1 from snapshot where id = 1;";
    public const string SnapshotInsertVersionOne = @"
insert into snapshot (id, version, url, s3_key, built_at)
values (1, 1, @url, @s3_key, now());";

    // 8.17 Memory refresh.
    public const string RefreshSnapshot = @"select version, url from snapshot where id = 1;";
    public const string RefreshCurrentEvent = @"select id, status_id from event where is_current;";
    public const string RefreshActiveBeacon = @"select id from beacon where is_active;";
    public const string RefreshLatestPublishedLocation = @"
select seq, lat, lng, speed_mps, altitude_m, heading_deg, accuracy_m, recorded_at, received_at
from location
where event_id = @event_id and published
order by seq desc
limit 1;";
    public const string RefreshCookieTally = @"
select cookie_type_id, count(*)
from cookie
where event_id = @event_id and hidden_at is null
group by cookie_type_id;";
    public const string RefreshAppSettings = @"select key, value from app_setting;";

    // 8.18 live_state updates.
    public const string LiveStateSuccess = @"
update live_state
set last_write_at = now(),
    last_write_seq = @seq,
    last_write_version = @version,
    last_write_node = @node,
    last_write_error = null
where id = 1;";

    public const string LiveStateFailure = @"
update live_state
set last_write_error = @error,
    last_write_node = @node
where id = 1;";

    // 8.19 Content publish reads and writes.
    public const string ContentReadPages = @"
select id, slug, title, nav_label, nav_position, role
from page
where not is_hidden
order by case role
           when 'no_event'  then 0
           when 'planned'   then 1
           when 'scheduled' then 2
           when 'live'      then 3
           when 'ended'     then 4
           when 'cancelled' then 5
           else 6 end,
         nav_position, id;";

    public const string ContentReadSections = @"
select id, page_id, kind, presentation, data
from section
where not is_hidden and page_id = any(@page_ids)
order by page_id, position, id;";

    public const string ContentReadItems = @"
select id, section_id, data
from section_item
where not is_hidden and section_id = any(@section_ids)
order by section_id, position, id;";

    public const string ContentReadSiteSettings = @"
select data from site_setting_draft where id = 1;";

    public const string ContentReferencedMediaReady = @"
select id from media_asset where id = any(@ids) and state = 'ready';";

    public const string ContentLatestHash = @"
select sha256 from content_version order by id desc limit 1;";

    public const string ContentVersionInsert = @"
insert into content_version (document, sha256, media_ids, label, published_by)
values (@document, @sha256, @media_ids, @label, @admin_email)
returning id, published_at;";

    public const string ContentVersionPrune = @"
delete from content_version
where id not in (select id from content_version order by id desc limit 50);";

    // 8.20 Restore.
    public const string ContentVersionRead = @"
select document from content_version where id = @version_id;";

    public const string PagesDeleteAll = @"delete from page;";

    public const string PageInsert = @"
insert into page (slug, title, nav_label, nav_position, role, created_by, updated_by)
values (@slug, @title, @nav_label, @nav_position, @role, @admin_email, @admin_email)
returning id;";

    public const string SectionInsert = @"
insert into section (page_id, kind, position, data, presentation, updated_by)
values (@page_id, @kind, @position, @data, @presentation, @admin_email)
returning id;";

    public const string SectionItemInsert = @"
insert into section_item (section_id, position, data, updated_by)
values (@section_id, @position, @data, @admin_email);";

    public const string SiteSettingDraftUpdate = @"
update site_setting_draft
set data = @data,
    updated_by = @admin_email,
    updated_at = now()
where id = 1;";

    // 8.21 Working-set writes.
    public const string PageLock = @"select id from page where id = @page_id for update;";

    public const string SectionBumpPositions = @"
update section set position = position + 1
where page_id = @page_id and position >= @position;";

    public const string SectionInsertAtPosition = @"
insert into section (page_id, kind, position, data, presentation, updated_by)
values (@page_id, @kind, @position, @data, @presentation, @admin_email)
returning id, page_id, kind, position, is_hidden, data, presentation, created_at, updated_by, updated_at;";

    public const string SectionDelete = @"
delete from section where id = @section_id returning page_id;";

    public const string SectionCompact = @"
update section s
set position = r.rn - 1
from (
  select id, row_number() over (order by position, id) rn
  from section
  where page_id = @page_id
) r
where s.id = r.id and s.position <> r.rn - 1;";

    public const string SectionReorderApply = @"
update section
set position = v.pos
from (values @rows) as v (id, pos)
where section.id = v.id and section.page_id = @page_id;";

    public const string SectionMove = @"
update section
set page_id = @to_page,
    position = @position,
    updated_by = @admin_email,
    updated_at = now()
where id = @section_id;";

    public const string SectionDuplicate = @"
insert into section (page_id, kind, position, is_hidden, data, presentation, updated_by)
select page_id, kind, position + 1, is_hidden, data, presentation, @admin_email
from section where id = @section_id
returning id;";

    public const string SectionItemDuplicate = @"
insert into section_item (section_id, position, is_hidden, data, updated_by)
select @new_section_id, position, is_hidden, data, @admin_email
from section_item where section_id = @source_section_id;";

    public const string SiteSettingDraftUpdatePut = @"
update site_setting_draft
set data = @data,
    updated_by = @admin_email,
    updated_at = now()
where id = 1;";

    public const string PagesSetNavPositions = @"
update page set nav_position = v.pos * 10
from (values @rows) as v (id, pos)
where page.id = v.id and page.role = 'none';";

    // 8.22 Media.
    public const string MediaAssetInsertPending = @"
insert into media_asset (id, filename, content_type, kind, state, s3_key, size_bytes, alt, title, uploaded_by)
values (@id, @filename, @content_type, @kind, 'pending', @s3_key, @size_bytes, @alt, @title, @admin_email);";

    public const string MediaAssetConfirm = @"
update media_asset
set state = 'ready',
    size_bytes = @size,
    width = @width,
    height = @height,
    sha256 = @sha,
    variants = @variants,
    confirmed_at = now()
where id = @id and state = 'pending';";

    public const string MediaAssetDeletePending = @"
delete from media_asset where id = @id and state = 'pending';";

    public const string MediaUsagePages = @"
select id, slug, title
from page p
where exists (
  select 1 from section s
  where s.page_id = p.id
    and (s.data::text like '%' || @id::text || '%' or s.presentation::text like '%' || @id::text || '%')
) or exists (
  select 1 from section s
  join section_item i on i.section_id = s.id
  where s.page_id = p.id and i.data::text like '%' || @id::text || '%'
);";

    public const string MediaUsageContentVersions = @"
select count(*) from content_version where @id = any(media_ids);";

    public const string MediaUsageSponsors = @"
select id, name from sponsor where logo_media_id = @id;";

    public const string MediaUsageCookieTypes = @"
select id, name from cookie_type
where icon->>'source' = 'media' and icon->>'id' = @id::text;";

    public const string MediaUsageSiteSettings = @"
select data::text like '%' || @id::text || '%' from site_setting_draft where id = 1;";

    public const string MediaAssetDelete = @"delete from media_asset where id = @id;";

    // 8.23 Preview token.
    public const string PreviewTokenInsert = @"
insert into preview_token (token_hash, created_by, expires_at)
values (sha256(@token::bytea), @admin_email, now() + interval '15 minutes');";

    public const string PreviewTokenResolve = @"
select 1 from preview_token
where token_hash = sha256(@token::bytea) and expires_at > now();";
}

public static class ChoreRecipes
{
    // 9.1 Outbox claim.
    public const string OutboxClaim = @"
update outbox
set claimed_at = now(),
    attempts = attempts + 1
where id in (
  select id from outbox
  where published_at is null
    and attempts < 5
    and (claimed_at is null or claimed_at < now() - interval '2 minutes')
  order by id
  limit 50
  for update skip locked
)
returning id, topic, payload, attempts;";

    public const string OutboxPublished = @"
update outbox set published_at = now(), last_error = null where id = @id;";

    public const string OutboxFailed = @"
update outbox set last_error = @error where id = @id;";

    // 9.2 Topic processing.
    public const string EventMessageExists = @"
select 1 from event_message where id = @message_id and event_id = @event_id;";

    public const string OutboxMarkSourceDeleted = @"
update outbox set published_at = now(), last_error = 'source_deleted' where id = @id;";

    public const string SubscriberFetchForVerify = @"
select address from subscriber
where id = @subscriber_id and verified_at is null and unsubscribed_at is null;";

    public const string ContactMessageFetchForOutbox = @"
select name, email, body from contact_message where id = @contact_message_id;";

    public const string AlertDeliveryFanOut = @"
insert into alert_delivery (outbox_id, subscriber_id)
select @outbox_id, id
from subscriber
where channel = 'email' and verified_at is not null and unsubscribed_at is null
on conflict (subscriber_id, outbox_id) do nothing;";

    // 9.3 Alert send.
    public const string AlertDeliveryClaim = @"
select d.id, d.outbox_id, d.attempts, s.address, s.unsubscribe_token, o.topic, o.payload
from alert_delivery d
join subscriber s on s.id = d.subscriber_id
join outbox o on o.id = d.outbox_id
where d.sent_at is null and d.attempts < 5
order by d.id
limit @batch
for update of d skip locked;";

    public const string AlertDeliverySuccess = @"
update alert_delivery
set sent_at = now(),
    ses_message_id = @ses_message_id,
    last_error = null
where id = @id;";

    public const string AlertDeliveryFailure = @"
update alert_delivery
set attempts = attempts + 1,
    last_error = @error
where id = @id;";

    // 9.4 Stale beacon flag.
    public const string StaleBeaconFlag = @"
update beacon
set stale_since = now(),
    updated_at = now()
where revoked_at is null
  and stale_since is null
  and last_seen_at is not null
  and greatest(coalesce(last_heartbeat_at, '-infinity'), coalesce(last_location_at, '-infinity'))
      < now() - make_interval(secs => @beacon_stale_after_s);";

    // 9.5 Nightly cleanup.
    public const string NightlyEnrollmentTokens = @"
delete from beacon_enrollment_token
where (consumed_at is not null and consumed_at < now() - interval '24 hours')
   or (consumed_at is null and expires_at < now() - interval '24 hours');";

    public const string NightlyOutbox = @"
delete from outbox where published_at < now() - interval '30 days';";

    public const string NightlyUnverifiedSubscribers = @"
delete from subscriber where verified_at is null and created_at < now() - interval '7 days';";

    public const string NightlyBeaconLogs = @"
delete from beacon_log where received_at < now() - interval '30 days';";

    public const string NightlyPreviewTokens = @"
delete from preview_token where expires_at < now() - interval '24 hours';";

    public const string NightlyStalePendingMedia = @"
delete from media_asset
where state = 'pending' and created_at < now() - interval '2 days';";

    // 9.6 Media orphan collection.
    public const string MediaOrphanRefs = @"
with refs as (
  select unnest(media_ids) as id from content_version
  union select logo_media_id from sponsor where logo_media_id is not null
  union select (icon->>'id')::uuid from cookie_type where icon->>'source' = 'media'
  union select m.id from media_asset m where exists (
    select 1 from section s
    where s.data::text like '%' || m.id::text || '%'
       or s.presentation::text like '%' || m.id::text || '%')
  union select m.id from media_asset m where exists (
    select 1 from section_item i where i.data::text like '%' || m.id::text || '%')
  union select m.id from media_asset m, site_setting_draft d
    where d.data::text like '%' || m.id::text || '%'
)
select id from refs;";

    public const string MediaOrphanStamp = @"
update media_asset
set unreferenced_since = now()
where state = 'ready' and unreferenced_since is null and id <> all(@refs);";

    public const string MediaOrphanClear = @"
update media_asset
set unreferenced_since = null
where state = 'ready' and unreferenced_since is not null and id = any(@refs);";

    public const string MediaOrphanReady = @"
select id, s3_key, variants from media_asset
where state = 'ready' and unreferenced_since < now() - interval '30 days';";

    public const string MediaOrphanMark = @"
update media_asset
set state = 'orphaned',
    orphaned_at = now()
where id = @id and state = 'ready';";

    public const string MediaOrphanReviveList = @"
select id, s3_key, variants from media_asset
where state = 'orphaned' and id = any(@refs);";

    public const string MediaOrphanRevive = @"
update media_asset
set state = 'ready',
    orphaned_at = null,
    unreferenced_since = null
where id = @id and state = 'orphaned';";

    public const string MediaOrphanDelete = @"
delete from media_asset
where state = 'orphaned' and orphaned_at < now() - interval '8 days';";
}
