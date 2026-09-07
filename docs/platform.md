# WMSFO v2 platform

Everything outside the four codebases: environments, storage and CDN, database, gateway manifest and container secret, identity, email, IAM, DNS, hosting, CI/CD, observability, runbooks, the legacy migration tool, and the cut-over. The design overview (`DESIGN.md`) is authoritative; the shared contracts (`docs/contracts.md`) fix every name and shape used here. Choices this document makes are listed in section 16; choices that need the owner are in section 17.

---

## 0. Placeholders and environments

### 0.1 Placeholders

Every environment-specific value is a placeholder with a dev value and a prod value. The contracts' placeholders apply unchanged; this document adds the ones below.

| Placeholder | Meaning |
|---|---|
| `<secret-name>` | Secrets Manager secret holding the API container env. One per environment. |
| `<oidc-role-arn>` | IAM role GitHub Actions assumes through OIDC to push images. |
| `<alarm-topic>` | SNS topic that CloudWatch alarms publish to (prod). |
| `<ses-events-topic>` | SNS topic that receives SES bounce, complaint, and reject events. |
| `<inbox-address>` | The team mailbox: contact-form notifications, SES event mail, alarm mail. |
| `<oncall-number>` | Phone number subscribed to `<alarm-topic>` by SMS on event night. |
| `<db-master-user>` | The RDS master user. Creates databases and roles; never used by the API or the migration tool. |
| `<legacy-db-name>` | The legacy database for the environment being migrated (source, read only). |
| `<legacy-db-user>` | A login that can read `<legacy-db-name>`. |
| `<docker-bridge-ip>` | Address of the instance's Docker bridge, where the gateway's internal listener answers on port 8080. |
| `<cognito-prefix>` | The hosted UI domain prefix; `<cognito-domain>` is `<cognito-prefix>.auth.<region>.amazoncognito.com`. |
| `<asg-name>` | The gateway fleet's Auto Scaling group. |
| `<distribution-arn>` | The CloudFront distribution's ARN, used by the bucket policy's origin access control condition. |

### 0.2 What differs between dev and prod

| Item | dev | prod |
|---|---|---|
| Manifest service, hub channel prefix, `WMSFO_SERVICE_NAME` | `wmsfo-api-dev` | `wmsfo-api` |
| Git branch that deploys it | `dev` | `main` |
| GitHub Actions environment | `dev` | `prod` |
| Image tag | `<sha>-dev` | `<sha>-prod` |
| Database, roles | `wmsfo_dev`; `wmsfo_app_dev`, `wmsfo_migrate_dev` | `wmsfo_prod`; `wmsfo_app_prod`, `wmsfo_migrate_prod` |
| Container secret | `<secret-name>` (dev) | `<secret-name>` (prod) |
| Bucket, distribution | dev `<bucket>`, dev `<cdn-domain>` | prod `<bucket>`, prod `<cdn-domain>` |
| Cognito pool, hosted UI domain, clients | `wmsfo-dev`, dev `<cognito-domain>`, dev client ids | `wmsfo-prod`, prod `<cognito-domain>`, prod client ids |
| Public site | `<preview-site-domain>` (Vercel preview from `dev`) | `<site-domain>` (Vercel production from `main`) |
| Admin panel | `<admin-dev-domain>` (Vercel, from `dev`) and `http://localhost:5174` | `<admin-domain>` (Vercel production from `main`) |
| `WMSFO_SITE_BASE_URL` | `https://<preview-site-domain>` | `https://<site-domain>` |
| `WMSFO_CORS_ORIGINS` | `https://<preview-site-domain>,https://<admin-dev-domain>,http://localhost:5173,http://localhost:5174` | `https://<site-domain>,https://<admin-domain>` |
| `realtimeAllowedOrigins` | `https://<preview-site-domain>,http://localhost:5173` | `https://<site-domain>` |
| SES configuration set | `wmsfo-dev` | `wmsfo-prod` |
| CloudWatch log group | `/gateway/services/wmsfo-api-dev` | `/gateway/services/wmsfo-api` |
| Metric namespace | `WMSFO/dev` | `WMSFO/prod` |
| Alarms | metric filters only, no alarms | full alarm set (section 10.3) |
| Fleet | shared, two small instances day to day | same fleet, scaled to 10 or more large instances before the event |

Shared by both environments: the AWS account, `<region>`, the gateway fleet and `<gateway-domain>`, the ALB, the RDS instance, the ECR repository `wmsfo-api`, the SES domain identity `<mail-domain>`, the GitHub repositories, `<inbox-address>`.

`<api-domain>` is one per environment: the ALB host rule for the prod host rewrites to `/wmsfo-api`, the one for the dev host rewrites to `/wmsfo-api-dev` (section 7).

---

## 1. Storage: S3 and CloudFront

### 1.1 Bucket settings

One bucket per environment. Settings, identical in both:

| Setting | Value |
|---|---|
| Object Ownership | Bucket owner enforced (ACLs disabled). |
| Block Public Access | All four settings true. The bucket is private; CloudFront reads it through origin access control (1.3, 1.6). |
| Versioning | Off. Every key except `live/location.json` is content-hashed and immutable; `live/location.json` is rewritten about once a second while live. |
| Default encryption | SSE-S3 (`AES256`). |
| Lifecycle rules | Two, both filtered by object tag and both expiring current versions: `wmsfo-media-pending` (tag `state=pending`, expire after 1 day) and `wmsfo-media-orphaned` (tag `state=orphaned`, expire after 7 days). Nothing untagged ever expires: the current snapshot may be months old, and every other object is deleted explicitly by the API (`DELETE /admin/routes/{id}`, `DELETE /admin/media/{id}`). |
| Request metrics | One filter, id `live`, prefix `live/`, so `AllRequests`, `PutRequests`, `GetRequests`, `4xxErrors`, `5xxErrors` exist for the live object (section 10). |
| Server access logging, event notifications, Object Lock, Transfer Acceleration, static website hosting | Off. |
| CORS | One rule for the admin panel's presigned `PUT` uploads (1.4). Reads never hit the bucket directly; the distribution answers CORS for them (section 1.6.2). |
| Policy | Section 1.3. |

### 1.2 Key layout, headers, and content types

Every object is written with exactly these `PutObject` parameters. `{sha256}` is the lowercase hex SHA-256 of the object bytes (contracts 1.6).

| Key | `Content-Type` | `Cache-Control` | Written by | Overwritten |
|---|---|---|---|---|
| `live/location.json` | `application/json; charset=utf-8` | `s-maxage=1, max-age=0` | API node (contracts 1.8) | Yes, in place, on every trigger |
| `snapshots/{sha256}.json` | `application/json; charset=utf-8` | `public, max-age=31536000, immutable` | API node, inside the admin transaction | Never (same content, same key) |
| `routes/{sha256}.json` | `application/json; charset=utf-8` | `public, max-age=31536000, immutable` | API node on `POST /admin/routes`; the migration tool | Never |
| `media/{mediaId}/{filename}` | the declared type: `image/png`, `image/jpeg`, `image/webp`, `image/gif`, `image/svg+xml` (verified against the bytes at confirm) | `public, max-age=31536000, immutable` | The admin panel through a presigned `PUT` that the API signs (1.5); the migration tool | Never; tagged `state=pending` until the API confirms, `state=orphaned` when unreferenced for 30 days |
| `media/{mediaId}/w{width}.webp` | `image/webp` | `public, max-age=31536000, immutable` | API node at confirm (widths 480, 960, 1600 below the source width) | Never; tagged with its original |
| `icons/{sha256}.svg` | `image/svg+xml` | `public, max-age=31536000, immutable` | The node that migrates on boot, once per icon library change | Never |

No other prefix is written. Every object is private to the bucket and public through the distribution. No `ACL` parameter is sent (ACLs are disabled). No `Expires` header. No object metadata beyond the two headers above; the only tags ever set are `state=pending` (by the presigned upload and the variant PUTs) and `state=orphaned` (by the orphan chore), and confirm removes the pending tag.

`PutObject` call shape the API uses for every JSON object:

```csharp
new PutObjectRequest
{
    BucketName = cfg.Bucket,                       // WMSFO_S3_BUCKET
    Key = key,
    InputStream = new MemoryStream(bytes),         // canonical bytes, contracts 1.6
    ContentType = "application/json; charset=utf-8",
    Headers = { CacheControl = cacheControl },      // one of the two values above
}
```

The live-object PUT uses a 3 s total timeout on the ingest path (the beacon's response never waits on it) and one attempt; the admin path retries three times one second apart (contracts 1.8). The snapshot and route PUTs inside an admin transaction, the variant PUTs at media confirm, and the icon PUTs at boot use one attempt with a 3 s timeout each (contracts 7.3, api.md 11.3 and 11a.7). A presigned upload URL is valid 15 minutes and fixes the key, the content type, and the pending tag.

### 1.3 Bucket policy

Read for the distribution only, through origin access control. Writes come only through IAM (the instance role, which also signs the browser's upload URLs, and operator identities), so the policy has no write statements.

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "CloudFrontRead",
      "Effect": "Allow",
      "Principal": { "Service": "cloudfront.amazonaws.com" },
      "Action": "s3:GetObject",
      "Resource": "arn:aws:s3:::<bucket>/*",
      "Condition": { "StringEquals": { "AWS:SourceArn": "<distribution-arn>" } }
    }
  ]
}
```

A direct request to the S3 endpoint answers `403`. Listing the bucket through the distribution answers `403` too (no `s3:ListBucket`), which CloudFront caches for 1 s (section 1.6).

### 1.4 Bucket CORS

One rule, for the admin panel's presigned uploads, which go to the bucket's S3 endpoint and not through the distribution:

```json
[
  {
    "AllowedOrigins": ["https://<admin-domain>"],
    "AllowedMethods": ["PUT"],
    "AllowedHeaders": ["Content-Type", "x-amz-tagging"],
    "ExposeHeaders": ["ETag"],
    "MaxAgeSeconds": 3600
  }
]
```

The dev bucket lists `https://<admin-dev-domain>` and `http://localhost:5174` instead. Reads never reach the bucket from a browser; the distribution's response headers policy (1.6.2) adds `Access-Control-Allow-Origin: *` to every response, so `Origin` stays out of the cache key.

### 1.5 How the API writes: instance role, no static keys

- The container obtains credentials from the instance role through the instance metadata service (IMDSv2). The AWS SDK default credential chain finds them with nothing configured. The secret (section 3.2) carries `AWS_REGION` and never carries `AWS_ACCESS_KEY_ID` or `AWS_SECRET_ACCESS_KEY`. No IAM user exists for the API.
- Requirement on the instances: metadata options `HttpTokens = required`, `HttpPutResponseHopLimit = 2`. A hop limit of 1 stops bridge-networked containers from obtaining an IMDSv2 token. Verify from inside any container on the fleet:

  ```sh
  TOKEN=$(curl -s -X PUT "http://169.254.169.254/latest/api/token" -H "X-aws-ec2-metadata-token-ttl-seconds: 60")
  curl -s -H "X-aws-ec2-metadata-token: $TOKEN" http://169.254.169.254/latest/meta-data/iam/security-credentials/
  ```

  The second command prints the instance role name. (The link-local metadata address is the standard one every instance has; it is not an identifier of this deployment.)
- Locally, developers use their own profile (`AWS_PROFILE`); the same code path runs.
- SDK packages: `AWSSDK.S3`, `AWSSDK.SimpleEmailV2`, `AWSSDK.SecurityToken` (transitively), and `AWSSDK.CognitoIdentityProvider` for the admin TOTP check. Clients are singletons; region from `AWS_REGION`.
- Deletes (`DeleteObjects` after `ListObjectsV2` under `media/{id}/` for a media delete, `DeleteObject` for a route), tagging (`PutObjectTagging`, `DeleteObjectTagging`, `GetObjectTagging` at confirm and in the orphan chore), reads (`HeadObject`, `GetObject` at confirm), and the presigned upload URLs (`GetPreSignedURL` for `PUT` with the content type and the `x-amz-tagging` header signed) all use the same credentials. A browser's presigned `PUT` runs under the instance role's permissions, so the role needs `s3:PutObject` and `s3:PutObjectTagging` on `media/*`; both are within the existing grant.

### 1.6 CloudFront distribution

One distribution per environment in front of its bucket. Settings:

| Setting | Value |
|---|---|
| Origin | The bucket's S3 REST endpoint `<bucket>.s3.<region>.amazonaws.com` (not the website endpoint) with an origin access control of type S3, signing behaviour "always sign" (SigV4). The bucket policy in 1.3 names this distribution. Origin protocol: HTTPS only (implicit for S3 REST origins). |
| Origin Shield | On, region `<region>` (the bucket's region). |
| Origin connection attempts, timeout | 3, 10 s (defaults). |
| Behaviour | The default behaviour only. Path pattern `*`. |
| Viewer protocol policy | `https-only`. |
| Allowed methods, cached methods | `GET, HEAD`; `GET, HEAD`. |
| Cache policy | Custom `wmsfo-<env>-cache`, section 1.6.1. |
| Origin request policy | None (nothing from the viewer request is forwarded to S3). |
| Response headers policy | Custom `wmsfo-<env>-headers`, section 1.6.2. |
| Compression | On (gzip and brotli). Applies to `application/json` and `image/svg+xml` automatically by content type; raster images are not compressed. |
| HTTP versions | HTTP/2 and HTTP/3. IPv6 on. |
| Custom error responses | `403`, `404`, `500`, `502`, `503`, `504`: error caching minimum TTL 1 s, no custom page, response code unchanged. |
| Price class | `PriceClass_100` (North America and Europe edges). |
| Alternate domain names and certificate | `<cdn-domain>` as configured for the distribution: either the CloudFront-assigned name or an alias with an ACM certificate in the N. Virginia region. Nothing in v2 depends on which. |
| Standard logging, real-time logs, WAF, geo restriction, default root object, field-level encryption | Off / none. |
| Invalidations | None, ever. `live/location.json` expires at the edge after 1 s by its own header; every other key is immutable and changes by getting a new URL. No IAM identity needs `cloudfront:CreateInvalidation`. |

#### 1.6.1 Cache policy `wmsfo-<env>-cache`

| Field | Value |
|---|---|
| Min TTL | 0 |
| Default TTL | 1 s (applies only to an object without `Cache-Control`; every v2 object has one) |
| Max TTL | 31,536,000 s |
| Headers in cache key | None |
| Query strings in cache key | None |
| Cookies in cache key | None |
| Gzip, brotli | Enabled, enabled |

The `s-maxage=1` on the live object is honoured (min 0, max 31,536,000 bracket it), so CloudFront and Origin Shield keep one copy per second and collapse concurrent misses into one origin read.

#### 1.6.2 Response headers policy `wmsfo-<env>-headers`

| Section | Value |
|---|---|
| Custom headers | `X-Content-Type-Options: nosniff`, override true |
| CORS | `Access-Control-Allow-Origin: *`; `Access-Control-Allow-Methods: GET, HEAD`; `Access-Control-Allow-Headers: *`; `Access-Control-Max-Age: 3600`; credentials off; override on |
| Security headers other than the above, server-timing, remove headers | None |


### 1.7 Verifying the CDN

Run from any machine after configuration and after every distribution change:

```sh
# live object: revalidates every second, CORS for the site origin, compressed
curl -sI -H "Origin: https://<site-domain>" -H "Accept-Encoding: br, gzip" https://<cdn-domain>/live/location.json
# expect: HTTP/2 200, content-type: application/json; charset=utf-8, cache-control: s-maxage=1, max-age=0,
#         access-control-allow-origin: *, content-encoding: br (or gzip),
#         x-content-type-options: nosniff, x-cache: Hit from cloudfront (on the second call within 1 s)

# an immutable object
curl -sI https://<cdn-domain>/snapshots/<sha256>.json
# expect: cache-control: public, max-age=31536000, immutable

# any origin gets the same header
curl -sI -H "Origin: https://example.org" https://<cdn-domain>/live/location.json | grep -i access-control-allow-origin
# expect: access-control-allow-origin: *

# http is refused
curl -sI http://<cdn-domain>/live/location.json | head -1
# expect: HTTP/1.1 403 Forbidden

# the bucket is not reachable directly
curl -sI https://<bucket>.s3.<region>.amazonaws.com/live/location.json | head -1
# expect: HTTP/1.1 403 Forbidden

# the upload CORS rule answers the panel's preflight
curl -si -X OPTIONS -H "Origin: https://<admin-domain>" -H "Access-Control-Request-Method: PUT" \
  -H "Access-Control-Request-Headers: content-type,x-amz-tagging" https://<bucket>.s3.<region>.amazonaws.com/media/test
# expect: access-control-allow-origin: https://<admin-domain>, access-control-allow-methods: PUT

# the lifecycle rules exist
aws s3api get-bucket-lifecycle-configuration --bucket <bucket>
# expect: wmsfo-media-pending (Tag state=pending, Expiration Days 1), wmsfo-media-orphaned (Tag state=orphaned, Expiration Days 7)
```

---

## 2. Database

### 2.1 Layout

One shared RDS PostgreSQL instance, reached at `<db-host>` (the instance endpoint or the RDS Proxy endpoint). Two databases, `wmsfo_dev` and `wmsfo_prod`. Two roles per database (sql.md 12): `wmsfo_migrate_<env>` owns it and runs migrations and the migration tool; `wmsfo_app_<env>` serves the API and can only select, insert, update, and delete. The master user is used once, to create the database and the roles.

### 2.2 Creating a database and roles

Run the statements of sql.md section 12 as `<db-master-user>` (roles, database, per-role timeouts), then the schema grants as `wmsfo_migrate_<env>`. No extension is required (plain `lat`/`lng`, no PostGIS). Dev and prod are identical apart from the `<env>` suffix.

### 2.3 Connection strings

Two values in the secret (sql.md 13); pool parameters are set in code, never in the string:

```
WMSFO_DB_CONNECTION            Host=<db-host>;Port=5432;Database=wmsfo_prod;Username=wmsfo_app_prod;Password=<app-password>;SSL Mode=Require;Trust Server Certificate=false
WMSFO_DB_MIGRATION_CONNECTION  Host=<db-host>;Port=5432;Database=wmsfo_prod;Username=wmsfo_migrate_prod;Password=<migrate-password>;SSL Mode=Require;Trust Server Certificate=false
```

- `Trust Server Certificate=false` means Npgsql validates the server certificate against the container's trust store. The API image installs the RDS certificate bundle into that store (section 9.1), so both the instance endpoint and the proxy endpoint validate.
- Pool sizing per sql.md 13: 10 app connections per node plus 2 migrate connections during boot. At 12 nodes that is about 144 connections; the RDS `max_connections` must leave that room after the other databases' users.
- The API applies migrations on boot over the migrate connection under `pg_advisory_lock(hashtext('wmsfo_migrations'))` (sql.md 14.1); the first node to boot creates the schema.

### 2.4 Backups and snapshots

Automated backups are an instance-level setting and already cover both databases. Before the prod cut-over and before event night an operator takes a manual snapshot of the instance (`aws rds create-db-snapshot`), section 13 and section 11.1. After the event, another. Nothing in the API depends on backups.

---

## 3. Gateway: manifest, secret, internal listener

### 3.1 Manifest entries

Both entries are created once from the gateway dashboard (`PUT /mgmt/services/{name}` with an `ops-admin` login). The upsert mints the publish token that the gateway injects into the container as `GATEWAY_REALTIME_TOKEN`. CI never upserts; it calls only `deploy` (section 9.1).

Prod, `PUT /mgmt/services/wmsfo-api`:

```json
{
  "image": "<account-id>.dkr.ecr.<region>.amazonaws.com/wmsfo-api",
  "tag": "<sha>-prod",
  "port": 5000,
  "desiredStatus": "running",
  "envSecretRef": "<secret-name>",
  "includeInHealth": true,
  "realtimeAuthPath": "/realtime/authorize",
  "realtimeMessagePath": "/realtime/message",
  "realtimeAllowedOrigins": "https://<site-domain>",
  "realtimePresence": false
}
```

Dev, `PUT /mgmt/services/wmsfo-api-dev`:

```json
{
  "image": "<account-id>.dkr.ecr.<region>.amazonaws.com/wmsfo-api",
  "tag": "<sha>-dev",
  "port": 5000,
  "desiredStatus": "running",
  "envSecretRef": "<secret-name>",
  "includeInHealth": true,
  "realtimeAuthPath": "/realtime/authorize",
  "realtimeMessagePath": "/realtime/message",
  "realtimeAllowedOrigins": "https://<preview-site-domain>,http://localhost:5173",
  "realtimePresence": false
}
```

What each field does on this fleet:

| Field | Effect |
|---|---|
| `image`, `tag` | The reconciler resolves the tag to a digest on `deploy` and blue-greens every instance onto it. The `tag` in the upsert is the first image to run; later tags come from CI. |
| `port` | Container-internal. The API binds `http://0.0.0.0:5000` (from `ASPNETCORE_URLS` in the secret). Docker assigns the host port; the gateway routes `/<service>/*` to it. |
| `envSecretRef` | Name of the Secrets Manager secret whose flat JSON `SecretString` becomes the container env (section 3.2). Resolved at container create; a changed value blue-greens the container within about 60 s plus one reconcile loop. |
| `includeInHealth` | The service appears in the gateway's aggregated `GET /api/health` document with its own probe result, and `stop` or `delete` on it require `?force=true`. The gateway's own health status is always `up`, so a down API never fails the load balancer check in either environment. |
| `realtimeAuthPath` | Makes every `<service>:*` channel private; the gateway POSTs joins to the API (contracts 2.4). |
| `realtimeMessagePath` | Lets joined clients `SendToChannel`; the gateway POSTs messages to the API (contracts 2.5). |
| `realtimeAllowedOrigins` | Exact browser origins allowed to open the WebSocket to `/hub`. Only the site joins the hub. |
| `realtimePresence` | Off in both environments: presence events would expose beacon connection ids and identities to the public channel. |

The three realtime string fields and `realtimePresence` are tri-state on upsert (absent preserves, empty string clears, value sets). Nothing but the dashboard ever sends them.

Deploy response and completion: `POST /mgmt/services/<service>/deploy { "tag" }` answers `202 { deployId, service, tag, digest, status: "in_progress" }`. `GET /mgmt/deploys/{deployId}` reports `status` `in_progress`, then `done`, `partial`, or `failed`, with per-instance progress. CI waits on it (section 9.1).

### 3.2 The container secret

One secret per environment. `SecretString` is a flat JSON object of strings, exactly the keys below, no nesting. The gateway turns it into the container env; the API reads it as configuration.

Dev value (contracts 8.1, with the platform's values filled in):

```json
{
  "ASPNETCORE_URLS": "http://0.0.0.0:5000",
  "WMSFO_ENV": "dev",
  "WMSFO_SERVICE_NAME": "wmsfo-api-dev",
  "WMSFO_DB_CONNECTION": "Host=<db-host>;Port=5432;Database=wmsfo_dev;Username=wmsfo_app_dev;Password=<app-password>;SSL Mode=Require;Trust Server Certificate=false",
  "WMSFO_DB_MIGRATION_CONNECTION": "Host=<db-host>;Port=5432;Database=wmsfo_dev;Username=wmsfo_migrate_dev;Password=<migrate-password>;SSL Mode=Require;Trust Server Certificate=false",
  "AWS_REGION": "<region>",
  "WMSFO_S3_BUCKET": "<bucket>",
  "WMSFO_CDN_BASE_URL": "https://<cdn-domain>",
  "WMSFO_PUBLIC_API_BASE_URL": "https://<api-domain>",
  "WMSFO_HUB_URL": "wss://<gateway-domain>/hub",
  "WMSFO_SITE_BASE_URL": "https://<preview-site-domain>",
  "WMSFO_GATEWAY_INTERNAL_URL": "http://<docker-bridge-ip>:8080",
  "WMSFO_CORS_ORIGINS": "https://<preview-site-domain>,https://<admin-dev-domain>,http://localhost:5173,http://localhost:5174",
  "WMSFO_TRUSTED_PROXY_HOPS": "2",
  "WMSFO_COGNITO_ISSUER": "https://cognito-idp.<region>.amazonaws.com/<pool-id>",
  "WMSFO_COGNITO_CLIENT_IDS": "<site-client-id>,<admin-client-id>",
  "WMSFO_COGNITO_USER_POOL_ID": "<pool-id>",
  "WMSFO_ADMIN_GROUP": "admin",
  "WMSFO_SES_FROM_ADDRESS": "Santa Tracker <alerts@<mail-domain>>",
  "WMSFO_SES_CONFIGURATION_SET": "wmsfo-dev",
  "WMSFO_CONTACT_NOTIFY_EMAIL": "<inbox-address>",
  "WMSFO_ALERT_SEND_PER_SEC": "10",
  "WMSFO_ENROLLMENT_ENCRYPTION_KEY": "<base64 of 32 random bytes>",
  "WMSFO_RECONCILE_TICK_MS": "1000",
  "WMSFO_LOG_LEVEL": "Information"
}
```

Prod differs in exactly these values:

| Key | prod value |
|---|---|
| `WMSFO_ENV` | `prod` |
| `WMSFO_SERVICE_NAME` | `wmsfo-api` |
| `WMSFO_DB_CONNECTION`, `WMSFO_DB_MIGRATION_CONNECTION` | `Database=wmsfo_prod` with `wmsfo_app_prod` and `wmsfo_migrate_prod` (rest identical) |
| `WMSFO_S3_BUCKET`, `WMSFO_CDN_BASE_URL`, `WMSFO_PUBLIC_API_BASE_URL` | the prod bucket, CDN, and API host |
| `WMSFO_SITE_BASE_URL` | `https://<site-domain>` |
| `WMSFO_CORS_ORIGINS` | `https://<site-domain>,https://<admin-domain>` |
| `WMSFO_COGNITO_ISSUER`, `WMSFO_COGNITO_CLIENT_IDS`, `WMSFO_COGNITO_USER_POOL_ID` | the prod pool and its two clients |
| `WMSFO_SES_CONFIGURATION_SET` | `wmsfo-prod` |
| `WMSFO_ENROLLMENT_ENCRYPTION_KEY` | a different random key |

Rules:

- `GATEWAY_REALTIME_TOKEN` is never in the secret; the gateway injects it.
- `WMSFO_FORCE_LEADER` is never in the secret; it exists only in local runs.
- No AWS access keys in the secret; credentials come from the instance role.
- Generating the enrollment key: `openssl rand -base64 32`.
- Finding `<docker-bridge-ip>` on an instance: `ip -4 -o addr show docker0 | awk '{print $4}' | cut -d/ -f1`. It is the same on every instance built from the same image; confirm on two instances before writing the secret.
- Creating the secret: `aws secretsmanager create-secret --name <secret-name> --secret-string file://secret.json` from an operator identity. The file never enters a repository.
- Changing any value: `aws secretsmanager put-secret-value --secret-id <secret-name> --secret-string file://secret.json`. The gateway sees the env drift within about 60 s and blue-green-replaces the container; no CI run is needed. A changed value therefore lands as a container restart, so change values outside the live window.
- The secret name must fall inside the resource scope of the instance role's existing `secretsmanager:GetSecretValue` grant; if that grant is scoped to a name prefix, the secret is created under that prefix (section 6.1).

### 3.3 Internal listener

The API reaches the gateway on its own host at `WMSFO_GATEWAY_INTERNAL_URL` (`http://<docker-bridge-ip>:8080`) for three things, all with header `X-Gateway-Realtime-Token: <GATEWAY_REALTIME_TOKEN>`:

| Call | Used for | Cadence |
|---|---|---|
| `POST /internal/publish` | the `location` event after every live-object write (contracts 2.6) | about 1/s while live; budget 50/s per instance |
| `GET /internal/leader` | leadership for chores (contracts 7.5) | every 2 s, 1 s timeout |
| `GET /internal/presence/<service>:ingest` | `hubConnected` on the beacon responses | once per `GET /admin/beacons` or `GET /admin/beacons/{id}` |

The hostname `gateway` does not resolve inside containers; the URL always comes from the secret. The listener is not routed by the load balancer and is unreachable from outside the instance.

### 3.4 The API's two public URLs

- `https://<gateway-domain>/<service>/<path>`: the gateway's YARP route strips `/<service>` and forwards to the container's host port. Every service on the fleet is reachable this way.
- `https://<api-domain>/<path>`: an ALB listener rule with a host-header condition on `<api-domain>` and a URL rewrite that prefixes the path with `/<service>` before forwarding to the gateway target group (section 7). The container sees the same bare path either way, and the same two proxy hops (`WMSFO_TRUSTED_PROXY_HOPS = 2`: the load balancer and the gateway proxy).

The callback paths `/realtime/authorize` and `/realtime/message` are reachable through both public URLs like every other route; the API's `X-Forwarded-*` check (contracts 3.5) answers `404` to anything that came through a proxy. Step 3 of the pre-event runbook (11.1) confirms whether the gateway refuses these paths on its public listener; a callback token is a later gateway feature.

### 3.5 Deploy semantics the runbooks rely on

- A deploy is a blue-green replacement on every instance: the old container keeps serving until the new one answers `200` on `GET /api/health`, which happens after migrations complete and the node's cache has loaded (contracts 4.1). Fleet capacity never dips.
- On a first create there is no old container; requests reaching the instance answer `503` until the new container is healthy.
- `POST /mgmt/services/<service>/rollback` redeploys the previous digest with the same mechanism.
- `POST /mgmt/services/<service>/restart` recreates the container fleet-wide (same digest); use it after a secret change if waiting for drift detection is not wanted.
- Container logs go to CloudWatch group `/gateway/services/<service>`, one stream per instance, 30-day retention set by the gateway.

---

## 4. Identity: Cognito

### 4.1 Pools

Two user pools, `wmsfo-dev` and `wmsfo-prod`, created once each, identical settings:

| Setting | Value |
|---|---|
| Sign-in | Email as username (`UsernameAttributes = email`), case-insensitive usernames |
| Self sign-up | On. The hosted UI offers sign-up; the site's sign-in link leads there |
| Verification | Email, code, sent by Cognito's default sender (no SES integration for pool mail in v1) |
| Required attributes | `email` |
| Password policy | 12 characters minimum, no composition rules, temporary passwords valid 7 days |
| MFA | Optional, TOTP only, SMS off. Members of `admin` and `editor` enrol TOTP; the API refuses panel requests from users without TOTP (`403 mfa_required`, api.md 6.3) |
| Account recovery | Email only |
| Deletion protection | On (prod) |
| Advanced security | Off (no plus tier features are used) |
| Hosted UI domain | `<cognito-prefix>-dev` and `<cognito-prefix>-prod` under `auth.<region>.amazoncognito.com` |
| Groups | `admin` (precedence 0, no role), `editor` (precedence 1, no role) |

### 4.2 App clients

Per contracts 3.1. Both public, no secret, authorization code grant with PKCE, scopes `openid email profile`, `ALLOW_REFRESH_TOKEN_AUTH` and `ALLOW_USER_SRP_AUTH`; the dev pool's `wmsfo-admin` client additionally has `ALLOW_USER_PASSWORD_AUTH` for the end-to-end test harness (a dedicated test admin user with TOTP, its secret in CI), never the prod clients; token revocation on, `PreventUserExistenceErrors = ENABLED`.

| Client | Callback URLs | Sign-out URLs | Refresh token |
|---|---|---|---|
| `wmsfo-site` | prod: `https://<site-domain>/auth/callback`; dev: `https://<preview-site-domain>/auth/callback`, `http://localhost:5173/auth/callback` | the origins of the callbacks with `/` | 30 days |
| `wmsfo-admin` | prod: `https://<admin-domain>/auth/callback`; dev: `https://<admin-dev-domain>/auth/callback`, `http://localhost:5174/auth/callback` | same pattern | 1 day |

ID and access tokens 60 minutes. The `wmsfo-admin` client also carries scope `aws.cognito.signin.user.admin` for the panel's in-place TOTP enrolment.

### 4.3 Operator procedure for admins

1. The admin signs up through the admin panel's hosted UI link (or is created in the console).
2. The admin enrols TOTP on the panel's setup page (shown whenever the API answers `403 mfa_required`).
3. An operator adds the user to group `editor` (content, media, sponsors, publish) or `admin` (everything) in the console. Until then the panel shows "this account has no role"; with a group but without TOTP the API answers `403 mfa_required` and the panel shows the TOTP setup page.

Removing or changing a role: edit the groups; the API sees the change on the next token (up to 60 minutes) and immediately if the token is revoked. A person in both groups is an admin.

---

## 5. Email: SES

| Item | Value |
|---|---|
| Identity | Domain identity `<mail-domain>` with Easy DKIM (2048-bit), three CNAME records at the DNS host; a custom MAIL FROM subdomain `mail.<mail-domain>` with its MX and SPF TXT records |
| Sender | `WMSFO_SES_FROM_ADDRESS` = `Santa Tracker <alerts@<mail-domain>>`; `contact_received` uses the same sender with `Reply-To` set to the contact's address |
| Configuration sets | `wmsfo-dev`, `wmsfo-prod`; event destination: SNS topic `<ses-events-topic>` for `BOUNCE`, `COMPLAINT`, `REJECT`, `RENDERING_FAILURE`, subscribed by `<inbox-address>` |
| Suppression | Account-level suppression list on for bounces and complaints. The API does nothing further in v1 (contracts 7.8) |
| Sandbox | A production access request is filed for the account with the expected volume (one alert to about 20,000 recipients, three times per December); the request is filed in October. Until approved, only verified recipients receive mail, which is what dev uses |
| Quota | After approval, the sending rate must be at or above `WMSFO_ALERT_SEND_PER_SEC` (10) and the daily quota above 100,000; check in the console the week before the event |
| Dev | The dev API sends real mail through the same identity to verified addresses only; every dev subscriber address is verified in the console first |

The instance role's SES permission is section 6.

---

## 6. IAM

### 6.1 Instance role

The fleet's instance role already grants S3 full access, Secrets Manager read, SNS full access, ECR read, and CloudWatch logs. Additions for v2, as one inline policy `wmsfo-api`:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    { "Effect": "Allow", "Action": ["ses:SendEmail", "ses:SendRawEmail"],
      "Resource": ["arn:aws:ses:<region>:<account-id>:identity/<mail-domain>",
                   "arn:aws:ses:<region>:<account-id>:configuration-set/wmsfo-dev",
                   "arn:aws:ses:<region>:<account-id>:configuration-set/wmsfo-prod"] },
    { "Effect": "Allow", "Action": "cognito-idp:AdminGetUser",
      "Resource": "arn:aws:cognito-idp:<region>:<account-id>:userpool/<pool-id>" }
  ]
}
```

The second statement is required (one pool ARN per environment): the API's admin TOTP check calls `AdminGetUser`. The existing `secretsmanager:GetSecretValue` grant is scoped to `secret:*`, so `<secret-name>` needs no prefix. The S3 grant is already broader than contracts 8.6 asks (it covers the object, tagging, list, and presign needs of the media pipeline); narrowing it is a fleet-wide change outside this design.

### 6.2 CI role

One IAM role `<oidc-role-arn>` trusted by GitHub's OIDC provider for repository `wmsfo-api`, branches `dev` and `main`, with `ecr:GetAuthorizationToken` on `*` and push permissions (`ecr:BatchCheckLayerAvailability`, `InitiateLayerUpload`, `UploadLayerPart`, `CompleteLayerUpload`, `PutImage`, `BatchGetImage`) on the `wmsfo-api` repository. No access keys in GitHub.

### 6.3 Operators

Operators use their own IAM identities for the console and the CLI steps in the runbooks. The RDS master credentials live in Secrets Manager under the instance's managed secret and are used only for section 2.2.

---

## 7. DNS, domains, load balancer

| Name | Record | Target |
|---|---|---|
| `<site-domain>`, `www.` variant | as Vercel instructs (A or CNAME) | Vercel production project |
| `<preview-site-domain>` | Vercel-assigned | Vercel preview project |
| `<admin-domain>`, `<admin-dev-domain>` | Vercel-assigned or CNAME | Vercel admin projects |
| `<api-domain>` (prod), dev API host | CNAME | the gateway load balancer |
| `<cdn-domain>` | CloudFront-assigned name, or CNAME to it with an ACM certificate | the distribution |
| `<cognito-domain>` | Cognito-managed | hosted UI |
| `<mail-domain>` DKIM, MAIL FROM | CNAME, MX, TXT | SES (section 5) |

Load balancer listener rules, one per API host, in this order before the default rule:

| Condition | Transform | Target |
|---|---|---|
| host-header `<api-domain>` (prod) | URL rewrite `^/(.*)$` to `/wmsfo-api/$1` | gateway target group |
| host-header dev API host | URL rewrite `^/(.*)$` to `/wmsfo-api-dev/$1` | gateway target group |

Each host has its own ACM certificate attached to the HTTPS listener. Idle timeout on the listener at or above 60 s (contracts 2.2). Nothing else changes on the load balancer.

---

## 8. Hosting: Vercel

| Project | Repository, branch | Framework | Env vars |
|---|---|---|---|
| Public site, production | `santa`, `main` | Vite | contracts 8.3 prod set |
| Public site, preview | `santa`, `dev` (a second project with `dev` as its production branch) | Vite | contracts 8.3 dev set |
| Admin panel | `wmsfo-admin-panel`, `main` | Vite | contracts 8.4 prod set |
| Admin panel, dev | `wmsfo-admin-panel`, `dev` (a second project with `dev` as its production branch) | Vite | contracts 8.4 dev set |

Settings on every project: framework preset Vite, output `dist`, SPA rewrite (`/(.*)` to `/index.html`) in `vercel.json`, headers `X-Content-Type-Options: nosniff` and `Referrer-Policy: strict-origin-when-cross-origin`, no serverless functions, no Vercel analytics injection. Preview deployments for pull requests are on; their origins are not in any allow list, so the hub and CDN CORS refuse them and the API refuses their `Authorization` origins. That is intended: pull request previews render from the CDN objects only.

---

## 9. CI/CD

### 9.1 API

`wmsfo-api/.github/workflows/deploy.yml`, per api.md 19: test, contract check, OIDC assume role, multi-arch build and push `wmsfo-api:<sha>-<env>`, deploy call, wait for `done`. Environment secrets (contracts 8.2): `AWS_ROLE_ARN`, `ECR_REPOSITORY`, `GATEWAY_BASE_URL`, `GATEWAY_TOKEN_URL`, `GATEWAY_CLIENT_ID`, `GATEWAY_CLIENT_SECRET`, `GATEWAY_SERVICE_NAME`. The image installs the RDS certificate bundle (api.md 18) so `Trust Server Certificate=false` validates.

The deploy client credential is the existing CI app client on the ops pool with scope `mgmt/deploy`; it cannot upsert the manifest, which is the intended limit.

### 9.2 Site and admin panel

Vercel git integration; no workflow file is required. Each repository has a `ci.yml` that runs type checks, unit tests, and the vendored-contracts check (contracts 13) on every push and pull request, so a red check blocks a merge before Vercel deploys it.

### 9.3 Red-Nose

`red-nose/.github/workflows/android.yml` per red-nose.md 16: tests, then per flavour a signed APK and the Magisk module zip that carries it, both uploaded as artifacts. The signing key and its password are repository secrets.

---

## 10. Observability

### 10.1 Logs

Container logs land in `/gateway/services/<service>` (one stream per instance, 30-day retention). Every API line is JSON with `requestId`, `service`, `env`, `node`, and the markers in api.md 16.

### 10.2 Metric filters (both environments)

| Filter name | Pattern | Metric (namespace `WMSFO/<env>`) |
|---|---|---|
| `live-put-failed` | `{ $.marker = "wmsfo_live_put_failed" }` | `LivePutFailed` |
| `publish-failed` | `{ $.marker = "wmsfo_publish_failed" }` | `PublishFailed` |
| `leader-lost` | `{ $.marker = "wmsfo_leader_lost" }` | `LeaderLost` |
| `leader-gained` | `{ $.marker = "wmsfo_leader_gained" }` | `LeaderGained` |
| `snapshot-write-failed` | `{ $.marker = "wmsfo_snapshot_write_failed" }` | `SnapshotWriteFailed` |
| `outbox-exhausted` | `{ $.marker = "wmsfo_outbox_exhausted" }` | `OutboxExhausted` |
| `alert-exhausted` | `{ $.marker = "wmsfo_alert_exhausted" }` | `AlertExhausted` |
| `health-unavailable` | `{ $.marker = "wmsfo_health_unavailable" }` | `HealthUnavailable` |
| `media-write-failed` | `{ $.marker = "wmsfo_media_write_failed" }` | `MediaWriteFailed` |
| `content-published` | `{ $.marker = "wmsfo_content_published" }` | `ContentPublished` |
| `location-stored` | `{ $.msg = "location stored" && $.published is true }` | `LocationPublished` |
| `http-5xx` | `{ $.status >= 500 }` | `Http5xx` |

S3 request metrics on prefix `live/` (section 1.1) give `PutRequests` and `5xxErrors` for the live object; CloudFront's default metrics give requests, error rate, and cache hit ratio per distribution.

### 10.3 Alarms (prod only), all to `<alarm-topic>`

| Alarm | Condition | Meaning on event night |
|---|---|---|
| `wmsfo-no-location-while-live` | `LocationPublished` sum over 60 s is 0 for 2 consecutive periods, enabled by the pre-event runbook while status is 3 and disabled after | the helicopter phone stopped delivering |
| `wmsfo-live-put-failed` | `LivePutFailed` sum over 60 s at or above 5 | CDN is falling behind; check S3 and the instance role |
| `wmsfo-publish-failed` | `PublishFailed` sum over 60 s at or above 20 | hub delivery failing; site is on polling |
| `wmsfo-no-leader` | `LeaderGained` and `LeaderLost` pattern: `LeaderLost` in the last 60 s with no `LeaderGained` after it, evaluated as `LeaderGained` sum over 120 s is 0 while `LeaderLost` sum is at least 1 | alerts and outbox stalled |
| `wmsfo-health-unavailable` | `HealthUnavailable` sum over 60 s at or above 3 | database or migration trouble on a node |
| `wmsfo-http-5xx` | `Http5xx` sum over 60 s at or above 10 | look at the logs |
| `wmsfo-snapshot-write-failed` | any occurrence | admin writes failing on S3 |
| `wmsfo-s3-live-5xx` | S3 `5xxErrors` on filter `live` at or above 1 per minute | S3 itself |
| `wmsfo-cdn-error-rate` | CloudFront `5xxErrorRate` above 1 percent over 5 minutes | CDN or origin |

`<alarm-topic>` has `<inbox-address>` subscribed by email always and `<oncall-number>` by SMS during the event window (subscribed in the pre-event runbook, unsubscribed after).

### 10.4 Dashboards

One CloudWatch dashboard `wmsfo-<env>` with: `LocationPublished` per minute, `LivePutFailed` and `PublishFailed`, S3 `PutRequests` on `live/`, CloudFront requests and cache hit ratio, `Http5xx`, `LeaderGained` and `LeaderLost`, and the gateway fleet's instance count. The admin panel's dashboard is the operator's live view; CloudWatch is the after-the-fact view.

---

## 11. Runbooks

### 11.1 Pre-event (the week before)

1. Manual RDS snapshot.
2. Confirm SES production access and quota (section 5); send a test alert to a verified address from prod with a test event.
3. Confirm the prod secret's values and that both containers report healthy on `GET /api/health` through `<api-domain>`; confirm `POST https://<api-domain>/realtime/authorize` with an empty JSON body answers `404` with no body (the forwarded-header guard) and note whether the gateway refuses the path before it reaches the container.
4. Confirm `GET /admin/live` shows `lastWriteError` null and the CDN object matches (dashboard card green).
5. Scale the fleet: set the Auto Scaling group `<asg-name>` desired capacity to 10 (or more) large instances the day before; confirm every instance runs both containers and the hub answers on each.
6. Subscribe `<oncall-number>` to `<alarm-topic>`; enable `wmsfo-no-location-while-live` when the event goes live (step 11.2).
7. Beacons: create or rotate the helicopter beacon and the spare; enrol both phones; run the Red-Nose checklist on each; confirm heartbeats and telemetry on the beacons page; set the helicopter phone active.
8. Dry run against dev: dev event set live, Red-Nose replay of the 2025 route, preview site showing the tracker, alert email to a verified address.
9. Create the year's event in prod (inherits the latest route), set current, set scheduled with `scheduledAt`, `notify` as decided; check the site shows the countdown.

### 11.2 Event night

| Step | Who | Action |
|---|---|---|
| T-60 min | operator | Fleet scaled and healthy; beacons page shows both phones green; `POST /admin/live/republish` and confirm the dashboard card |
| T-10 min | operator | Message posted with `notify: false` unless the admin wants the email |
| T-0 | admin | `POST /admin/events/{id}/status { 3, notify }` from the panel; confirm the site switches to the tracker; enable `wmsfo-no-location-while-live` |
| flight | operator | Watch the beacons page and the dashboard card; on `lastWriteError`, Republish; on a stale helicopter phone, activate the spare |
| landing | admin | status 4 with `notify: false`; the leaderboard freezes; disable `wmsfo-no-location-while-live` |
| T+1 h | operator | Scale the fleet back to 2; unsubscribe the SMS number; manual RDS snapshot; export the event's locations (`GET /admin/events/{id}/locations` as CSV) to the team share |

### 11.3 Incident quick reference

| Symptom | Check | Action |
|---|---|---|
| Site shows the old status | dashboard card red | `POST /admin/live/republish`; if it stays red, `GET /admin/live` `lastWriteError` and the S3 metrics |
| No location on the site, phone green | `LocationPublished` metric; `isActive` on the beacons page | activate the right beacon; check the phone's `liveEventId` on its status screen |
| Phone red (heartbeat old) | telemetry age, battery, socket state | swap to the spare; the phone keeps retrying on its own |
| Alerts not arriving | `OutboxExhausted`, `AlertExhausted`, leader alarms | check SES quota and the leader; rows retry on their own up to 5 attempts |
| API 503 on one instance | `HealthUnavailable`, gateway instances view | the gateway keeps routing to that instance; `POST /mgmt/services/<service>/restart` or terminate the instance so the ASG replaces it |

---

## 12. Legacy migration tool

`tools/Wmsfo.Migrate` in the API solution, per contracts 10 and sql.md 15.

### 12.1 Inputs

```
dotnet run --project tools/Wmsfo.Migrate -- \
  --legacy-db "<legacy connection string>" \
  --new-db    "<WMSFO_DB_MIGRATION_CONNECTION value>" \
  --legacy-bucket "<legacy bucket>" \
  --bucket    "<bucket>" \
  --cdn-base-url "https://<cdn-domain>" \
  [--event-name-format "Santa Flyover {year}"] \
  [--dry-run]
```

Run from an operator machine with an AWS profile that can read the legacy bucket and write the new one, reachable to both databases. `--dry-run` reads everything, reports counts per step, writes nothing.

### 12.2 Order and idempotency

The steps of sql.md 15 in order, each its own transaction on the new database, each idempotent by its natural key, each logging rows read, written, skipped. A rerun after a failure skips completed work. The tool reads the legacy bucket only for sponsor logo bytes (step 2) and writes them to the new bucket as media assets; it needs read on the legacy bucket and write on the new one.

### 12.3 Verification queries (printed at the end and checked by the operator)

| Check | Expectation |
|---|---|
| `select e.year, count(*) from location l join event e on e.id = l.event_id group by 1 order by 1` | matches `flight_history` counts per year |
| `select count(*) from event where status_id = 4` | 6 |
| `select count(*) from sponsor`, `sponsor_year` | 7, 25 |
| `select count(*) from event_message` | 31 minus skipped rows, listed |
| `select count(*) from contact_message` | 45 |
| `select route_id from event where year = 2025` | non-null; `GET <cdn>/routes/<sha256>.json` returns 200 with 1,065 points |
| `select key, value from app_setting` | `sponsor_linger_ms_per_dollar` = 30 |
| `select count(*) from media_asset where uploaded_by = 'migration' and state = 'ready'` | the number of legacy sponsors with a logo, minus any logged as failed validation; each has its object and variants under `media/{id}/` in the new bucket |
| `select count(*) from sponsor where logo_media_id is not null` | the same number |

### 12.4 Legacy sponsor logos

Each legacy full logo becomes a media asset in the new bucket (sql.md 15.10): the tool runs the API's confirm pipeline on the bytes, writes `media/{uuid}/{filename}` and the variants with the immutable header and no tag, and links the sponsor. Legacy small logos are not copied. The legacy bucket is left untouched and retired with the legacy stack.

### 12.5 What is not migrated

Everything in the retired list of sql.md 15.1. The legacy database is left untouched and kept for 90 days after cut-over, then dropped by the master user.

---

## 13. Cut-over

Dev first, then prod, same steps:

1. Rename the legacy API and site repositories to `<name>-legacy`; create `wmsfo-api`, `santa`, and `red-nose`; `wmsfo-admin-panel` keeps its repository and is migrated in place.
2. Create the Cognito pool and clients (section 4), the SES configuration set (section 5), the CloudFront settings (section 1.6), the IAM additions (6.1), the CI role (6.2).
3. Create the database and roles (section 2.2, sql.md 12); write the container secret (3.2).
4. From the gateway dashboard, upsert the manifest entry (3.1) with the first image tag CI produced; the upsert mints the publish token.
5. Deploy: the API boots, migrates, builds snapshot version 1, writes the live object. Verify `GET /api/health` on both hosts and `<cdn>/live/location.json`.
6. The legacy API is already stopped and its manifest entry deleted (the legacy site serves a static snapshot); the legacy database stays readable for the migration tool.
7. Run the migration tool (section 12); check the verification queries; `POST /admin/snapshot/rebuild`.
8. Set the Vercel env vars on the new site and admin projects; deploy; point `<site-domain>` and `<admin-domain>` at the new projects.
9. Create the new year's event (inherits the 2025 route), set current.
10. Leave the legacy bucket objects (the snapshot references the logo keys).

Rollback before step 8 is nothing: the static legacy site still runs. Rollback after step 8 is repointing the domains at the legacy Vercel projects; the new database is untouched by that.

---

## 14. Recovery

| Loss | Recovery |
|---|---|
| A node or instance | the ASG replaces it; the new node migrates (no-op), loads state, and serves |
| The live object | `POST /admin/live/republish` from the panel |
| The snapshot object | `POST /admin/snapshot/rebuild` |
| A route object | re-upload; the event's route row points at a key that no longer exists until then, and the site shows no route |
| A media object or variant | re-upload through the media library and re-select it where it was used (the old asset shows as a missing image until then); `GET /admin/media/{id}/usage` lists the places |
| The icon library objects | any node restart rewrites them when the library hash differs; to force it, clear `icon_library_state.library_sha256` |
| The database | restore the instance snapshot to a new instance, repoint `<db-host>` in the secret; every CDN object is a projection and is rebuilt by `rebuild` and `republish` |
| The bucket | the database is the truth for everything but media bytes; snapshot and live objects are rebuilt; the icon library is rewritten on the next boot; routes are re-uploaded from the team share (the exports in 11.2); media is re-uploaded from the originals the team keeps, which is why the media library's `title` field exists |
| The secret | recreate from the operator's copy; values are listed in 3.2 |

---

## 15. Cost (order of magnitude, per year)

| Item | Estimate |
|---|---|
| CloudFront, event night at 100k pollers | about $150 (contracts 1.7 pattern) |
| S3 requests and storage | under $5 |
| Cognito | free under 50,000 monthly active users |
| SES | about $6 for 60,000 messages |
| Fleet scale-up, 10 large instances for a day | tens of dollars |
| RDS | already paid; the databases add nothing |
| Vercel | free tier for static projects |

---

## 16. Decisions made here

- Two database roles per environment, per sql.md 12 and 13.
- Admin TOTP is enforced by the API through `AdminGetUser` (`403 mfa_required`); the operator procedure in 4.3 is the complement, not the guarantee.
- Bucket object ownership enforced, ACLs off, Block Public Access fully on, read only by the distribution through origin access control.
- Origin Shield on; CORS for reads from a CloudFront response headers policy allowing `*`; one bucket CORS rule for the admin panel's presigned `PUT`; nothing from the viewer request in the cache key.
- Media lifecycle by object tag: `state=pending` expires after 1 day, `state=orphaned` after 7; nothing untagged expires.
- Cognito groups `admin` and `editor`; the API decides what each may do.
- The migration tool imports legacy logos as media assets rather than keeping legacy keys; the legacy bucket is retired.
- Price class North America and Europe.
- Cognito pool mail through Cognito's default sender; SES is used only by the API.
- One inline IAM policy `wmsfo-api` on the instance role for SES and `AdminGetUser`; the existing broad S3 grant is left as is.
- CI pushes through an OIDC role; no access keys in GitHub.
- A second Vercel project per frontend with `dev` as its production branch, rather than relying on pull request previews (which are refused by every allow list).
- Alarms in prod only; dev has metric filters for the dashboard.
- The no-location alarm is toggled by the runbook rather than reading the event status.
- The legacy database is kept 90 days after cut-over.

## 17. Needs a decision

Nothing at the moment. Add here as it comes up.
