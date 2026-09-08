# Runner smoke test

Date: 2026-09-08. Ran from the repository root on the grunt runner image (Debian 12, aarch64).

## Tool versions

| Tool | Version |
|---|---|
| dotnet | 10.0.400 |
| node | v22.23.2 |
| npm | 10.9.8 |
| psql / initdb / pg_ctl / createdb | 15.19 (Debian 15.19-0+deb12u1) |

`gradle` is not applicable to this repo. `dotnet ef` is captured under `dotnet tool restore` below.

## `dotnet --version`

Exit code: 0.

```
10.0.400
```

## `dotnet restore`

Exit code: 0.

```
An issue was encountered verifying workloads. For more information, run "dotnet workload update".
  Determining projects to restore...
  Restored /workspace/tools/Wmsfo.Migrate/Wmsfo.Migrate.csproj (in 4.3 sec).
  Restored /workspace/tests/Wmsfo.Api.Tests/Wmsfo.Api.Tests.csproj (in 4.3 sec).
  Restored /workspace/tests/Wmsfo.Api.IntegrationTests/Wmsfo.Api.IntegrationTests.csproj (in 4.31 sec).
  Restored /workspace/src/Wmsfo.Api/Wmsfo.Api.csproj (in 4.35 sec).
```

The "verifying workloads" line is a generic SDK notice, not a failure; the restore succeeded.

## `dotnet build -warnaserror`

Exit code: 0.

```
Determining projects to restore...
  All projects are up-to-date for restore.
  Wmsfo.Api -> /workspace/src/Wmsfo.Api/bin/Debug/net10.0/Wmsfo.Api.dll
  Wmsfo.Migrate -> /workspace/tools/Wmsfo.Migrate/bin/Debug/net10.0/Wmsfo.Migrate.dll
  Wmsfo.Api.Tests -> /workspace/tests/Wmsfo.Api.Tests/bin/Debug/net10.0/Wmsfo.Api.Tests.dll
  Wmsfo.Api.IntegrationTests -> /workspace/tests/Wmsfo.Api.IntegrationTests/bin/Debug/net10.0/Wmsfo.Api.IntegrationTests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:02.30
```

## `dotnet test`

Exit code: 0. Run with `--no-build` because the build step above already produced the assemblies.

```
Test run for /workspace/tests/Wmsfo.Api.Tests/bin/Debug/net10.0/Wmsfo.Api.Tests.dll (.NETCoreApp,Version=v10.0)
Test run for /workspace/tests/Wmsfo.Api.IntegrationTests/bin/Debug/net10.0/Wmsfo.Api.IntegrationTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1, Duration: 2 ms - Wmsfo.Api.Tests.dll (net10.0)

Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1, Duration: 2 ms - Wmsfo.Api.IntegrationTests.dll (net10.0)
```

## `dotnet tool restore`

Exit code: 0.

```
Tool 'dotnet-ef' (version '10.0.11') was restored. Available commands: dotnet-ef

Restore was successful.
```

## `dotnet ef --version`

Exit code: 0.

```
Entity Framework Core .NET Command-line Tools
10.0.11
```

## Postgres cluster (initdb / pg_ctl / createdb / psql)

Throwaway cluster started as documented for the runner image: `initdb` into `/tmp/pgdata`, `pg_ctl start` with `-k /tmp` and a spare port (55432), `createdb`, connect with `psql`, stop, then remove `/tmp/pgdata`.

### `initdb -D /tmp/pgdata -U postgres -A trust --no-locale --encoding=UTF8`

Exit code: 0.

```
The database cluster will be initialized with locale "C".
The default text search configuration will be set to "english".

Data page checksums are disabled.

creating directory /tmp/pgdata ... ok
creating subdirectories ... ok
selecting dynamic shared memory implementation ... posix
selecting default max_connections ... 100
selecting default shared_buffers ... 128MB
selecting default time zone ... Etc/UTC
creating configuration files ... ok
running bootstrap script ... ok
performing post-bootstrap initialization ... ok
syncing data to disk ... ok

Success. You can now start the database server using:

    pg_ctl -D /tmp/pgdata -l logfile start
```

### `pg_ctl -D /tmp/pgdata -l /tmp/pgdata/logfile -o "-k /tmp -p 55432 -h ''" start`

Exit code: 0. `-h ''` disables TCP listeners, so only the Unix socket in `/tmp` is exposed.

```
waiting for server to start.... done
server started
```

### `createdb -h /tmp -p 55432 -U postgres smoketest`

Exit code: 0. No output.

### `psql -h /tmp -p 55432 -U postgres -d smoketest -c "SELECT version();"`

Exit code: 0.

```
                                                              version                                                              
-----------------------------------------------------------------------------------------------------------------------------------
 PostgreSQL 15.19 (Debian 15.19-0+deb12u1) on aarch64-unknown-linux-gnu, compiled by gcc (Debian 12.2.0-14+deb12u1) 12.2.0, 64-bit
(1 row)
```

### `pg_ctl -D /tmp/pgdata stop`

Exit code: 0.

```
waiting for server to shut down.... done
server stopped
```

### `rm -rf /tmp/pgdata`

Exit code: 0. `ls /tmp/pgdata` afterwards reports `No such file or directory`, confirming cleanup.
