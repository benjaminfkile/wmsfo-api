-- docs/sql.md section 12, dev values. Runs once on an empty data volume.
create role wmsfo_migrate_dev login password 'wmsfo_migrate_dev' nosuperuser nocreatedb nocreaterole noinherit;
create role wmsfo_app_dev     login password 'wmsfo_app_dev'     nosuperuser nocreatedb nocreaterole noinherit;
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

\connect wmsfo_dev wmsfo_migrate_dev
revoke create on schema public from public;
grant usage on schema public to wmsfo_app_dev;
alter default privileges for role wmsfo_migrate_dev in schema public
  grant select, insert, update, delete on tables to wmsfo_app_dev;
