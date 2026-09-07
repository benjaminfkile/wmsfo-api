# wmsfo-api

The WMSFO v2 API: the only writer of the database and the bucket, the only publisher on the hub, and the only surface beacons, registered people, and admins talk to. The public site never calls it.

Everything about what this service does and how is in `docs/`:

- `docs/DESIGN.md`: the design overview for all of v2. Start here.
- `docs/contracts.md`: every name, shape, path, and rule the four components code against. Wins over any other document on a conflict.
- `docs/api.md`: this repository's technical design (layout, startup, pipeline, endpoints, node runtime, chores, tests).
- `docs/sql.md`: the schema, indexes, transaction recipes, migrations, and the one-off legacy migration.
- `docs/platform.md`: everything outside the code (S3, CloudFront, RDS, gateway, Cognito, SES, IAM, CI, runbooks, cut-over).
- `docs/reference/legacy-schema.md`: the legacy database as read before the rewrite.

The site, admin panel, and beacon app repositories carry copies of `DESIGN.md` and `contracts.md`; the originals are here.

## Layout

```
Wmsfo.sln
src/Wmsfo.Api/                    the API (api.md section 2)
tests/Wmsfo.Api.Tests/            unit and contract tests
tests/Wmsfo.Api.IntegrationTests/ Testcontainers Postgres, full pipeline
tools/Wmsfo.Migrate/              the one-off legacy migration tool
contracts/                        generated and hand-written contract artifacts (contracts.md section 13); not yet produced
icons/                            the icon library; not yet produced
templates/email/                  SES templates; not yet produced
```

## Build

```
dotnet build
dotnet test
```

Requires the .NET 10 SDK. Local development per `docs/api.md` section 20.
