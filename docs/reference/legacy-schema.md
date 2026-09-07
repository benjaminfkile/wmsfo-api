# Legacy schema (production database, read 2026-09-06)

Reference for the migration design. Column types are as stored; row counts are approximate.

## event_mode_override (16 rows)

| column | type | null | default |
|---|---|---|---|
| id | bigint | no | nextval('event_mode_override_id_seq'::regclass) |
| mode | smallint | no |  |
| expires_at | timestamp with time zone | no |  |
| created_at | timestamp with time zone | no | now() |

Indexes:
- CREATE UNIQUE INDEX event_mode_override_pkey ON event_mode_override USING btree (id)

## event_updates (31 rows)

| column | type | null | default |
|---|---|---|---|
| id | integer | no | nextval('"EventUpdates_Id_seq"'::regclass) |
| message | text | no |  |
| time | timestamp with time zone | yes |  |
| created_at | timestamp with time zone | no | now() |

Indexes:
- CREATE UNIQUE INDEX "EventUpdates_pkey" ON event_updates USING btree (id)

## flight_history (8590 rows)

| column | type | null | default |
|---|---|---|---|
| id | integer | no | nextval('historical_route_points_id_seq'::regclass) |
| year | integer | no |  |
| lat | numeric | no |  |
| lng | numeric | no |  |
| seq | integer | no |  |
| time | bigint | no |  |

Indexes:
- CREATE UNIQUE INDEX historical_route_points_pkey ON flight_history USING btree (id)
- CREATE INDEX idx_flight_history_lat_lon ON flight_history USING btree (lat, lng)
- CREATE INDEX idx_flight_history_time ON flight_history USING btree ("time")
- CREATE INDEX idx_flight_history_year_time ON flight_history USING btree (year, "time")
- CREATE INDEX idx_hrp_year ON flight_history USING btree (year)

## funds (11 rows)

| column | type | null | default |
|---|---|---|---|
| percent | bigint | no |  |
| id | text | no |  |
| created | text | no |  |

## gps_location (1467 rows)

| column | type | null | default |
|---|---|---|---|
| id | bigint | no | nextval('location_id_seq'::regclass) |
| lat | numeric | no |  |
| lng | numeric | no |  |
| speed | text | yes |  |
| temp | text | yes |  |
| alt | text | yes |  |
| bearing | text | yes |  |
| bearing_raw | text | yes |  |
| mode | text | yes |  |
| time | text | no |  |
| type | text | yes |  |
| status | text | yes |  |
| redirect | text | yes |  |
| count | text | yes |  |
| tracking_mode_type_id | integer | no |  |

Indexes:
- CREATE UNIQUE INDEX location_pkey ON gps_location USING btree (id)
- CREATE UNIQUE INDEX gps_location_unique_tuple ON gps_location USING btree ("time", lat, lng, tracking_mode_type_id)
- CREATE INDEX idx_gps_location_lat_lon ON gps_location USING btree (lat, lng)
- CREATE UNIQUE INDEX idx_gps_location_time ON gps_location USING btree ("time")
- CREATE INDEX idx_gps_location_tracking_mode_type ON gps_location USING btree (tracking_mode_type_id)

## liftoff (3 rows)

| column | type | null | default |
|---|---|---|---|
| id | bigint | no |  |
| liftoff_time | timestamp with time zone | yes |  |

Indexes:
- CREATE UNIQUE INDEX liftoff_pkey ON liftoff USING btree (id)

## messages (45 rows)

| column | type | null | default |
|---|---|---|---|
| email | text | no |  |
| name | text | no |  |
| message | text | no |  |
| created | text | no |  |
| id | text | no |  |

## sponsor_settings (1 rows)

| column | type | null | default |
|---|---|---|---|
| id | integer | no | nextval('sponsor_settings_id_seq'::regclass) |
| linger_rate_ms_per_dollar | integer | no | 40 |
| updated_at | timestamp with time zone | yes | CURRENT_TIMESTAMP |

Indexes:
- CREATE UNIQUE INDEX sponsor_settings_pkey ON sponsor_settings USING btree (id)

## sponsor_years (25 rows)

| column | type | null | default |
|---|---|---|---|
| id | integer | no | nextval('sponsor_years_id_seq'::regclass) |
| sponsor_id | integer | yes |  |
| event_year | integer | no |  |
| amount_donated | numeric | yes | 0 |
| active | boolean | yes | true |
| can_advertise | boolean | yes | true |
| anonymous | boolean | yes | false |
| registered_at | timestamp without time zone | yes | now() |

Indexes:
- CREATE UNIQUE INDEX sponsor_years_pkey ON sponsor_years USING btree (id)
- CREATE UNIQUE INDEX sponsor_years_sponsor_id_event_year_key ON sponsor_years USING btree (sponsor_id, event_year)

## sponsors (7 rows)

| column | type | null | default |
|---|---|---|---|
| id | integer | no | nextval('sponsors_id_seq'::regclass) |
| name | character varying | no |  |
| contact_person | character varying | yes |  |
| email | character varying | yes |  |
| phone | character varying | yes |  |
| address | character varying | yes |  |
| website_url | character varying | yes |  |
| fb_url | character varying | yes |  |
| ig_url | character varying | yes |  |
| logo_s3_key | character varying | yes |  |
| logo_small_s3_key | character varying | yes |  |
| created_at | timestamp without time zone | yes | now() |
| updated_at | timestamp without time zone | yes | now() |

Indexes:
- CREATE UNIQUE INDEX sponsors_pkey ON sponsors USING btree (id)

## tracki_device (2 rows)

| column | type | null | default |
|---|---|---|---|
| id | integer | no | nextval('tracki_device_id_seq'::regclass) |
| name | character varying | no |  |
| device_id | bigint | no |  |
| url_chunk | character varying | no |  |
| description | text | yes |  |
| is_active | boolean | no | false |
| created_at | timestamp with time zone | yes | now() |

Indexes:
- CREATE UNIQUE INDEX tracki_device_device_id_key ON tracki_device USING btree (device_id)
- CREATE UNIQUE INDEX tracki_device_device_identifier_key ON tracki_device USING btree (url_chunk)
- CREATE UNIQUE INDEX tracki_device_pkey ON tracki_device USING btree (id)
- CREATE UNIQUE INDEX one_active_tracki_device_idx ON tracki_device USING btree (is_active) WHERE (is_active = true)

## tracki_device_data (0 rows)

| column | type | null | default |
|---|---|---|---|
| id | bigint | no | nextval('tracki_device_data_id_seq'::regclass) |
| tracki_device_id | integer | no |  |
| latitude | double precision | no |  |
| longitude | double precision | no |  |
| location_type | character varying | yes | 'GPS'::character varying |
| accuracy_in_meter | double precision | yes | 0 |
| battery_level | smallint | yes |  |
| last_updated | timestamp with time zone | no |  |
| recorded_at | timestamp with time zone | yes | now() |

Indexes:
- CREATE UNIQUE INDEX tracki_device_data_pkey ON tracki_device_data USING btree (id)
- CREATE INDEX idx_tracki_device_data_device_id ON tracki_device_data USING btree (tracki_device_id)
- CREATE INDEX idx_tracki_device_data_last_updated ON tracki_device_data USING btree (last_updated)

## tracking_mode (77 rows)

| column | type | null | default |
|---|---|---|---|
| id | integer | no | nextval('tracking_mode_id_seq'::regclass) |
| tracking_mode_type_id | integer | no |  |
| is_active | boolean | no | false |
| created_at | timestamp with time zone | yes | now() |

Indexes:
- CREATE UNIQUE INDEX tracking_mode_pkey ON tracking_mode USING btree (id)
- CREATE UNIQUE INDEX one_active_tracking_mode_idx ON tracking_mode USING btree (is_active) WHERE (is_active = true)

## tracking_mode_type (3 rows)

| column | type | null | default |
|---|---|---|---|
| id | integer | no | nextval('tracking_mode_type_id_seq'::regclass) |
| name | character varying | no |  |
| description | text | yes |  |
| interval | integer | no |  |

Indexes:
- CREATE UNIQUE INDEX tracking_mode_type_name_key ON tracking_mode_type USING btree (name)
- CREATE UNIQUE INDEX tracking_mode_type_pkey ON tracking_mode_type USING btree (id)

## Lookup data

tracking_mode_type rows: [{"id":1,"name":"legacy-cell-phone","description":"Primary tracking, uses gpsLocationCache","interval":5000},{"id":2,"name":"TrackiPro","description":"Fallback, uses TrackiPro GPS tracker (15 second intervals) => trackiDeviceDataCache","interval":15000},{"id":3,"name":"Simulator","description":"Simulator","interval":5000}]

Mode semantics on gps_location.mode and event_mode_override.mode: 0 = pre-show, 1 = run-show (live), 2 = end-show.

flight_history.time is epoch milliseconds. funds.created and messages.created are epoch milliseconds stored as text. funds.percent is text.
