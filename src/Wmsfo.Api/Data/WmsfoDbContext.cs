using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace Wmsfo.Api.Data;

// sql.md 14.2: one entity per table with explicit constraint/index names.
// Snake_case comes from EFCore.NamingConventions, identity-always from
// UseIdentityAlwaysColumns. All model tweaks live in OnModelCreating so the
// generated migration matches sql.md 3.
public sealed class WmsfoDbContext : DbContext
{
    public WmsfoDbContext(DbContextOptions<WmsfoDbContext> options) : base(options)
    {
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        // sql.md 4.1 lists every index. Foreign-key convention indexes are not
        // in that list, so the convention is removed and each explicit index is
        // configured below.
        configurationBuilder.Conventions.Remove<ForeignKeyIndexConvention>();
    }

    public DbSet<EventStatus> EventStatus => Set<EventStatus>();
    public DbSet<Route> Route => Set<Route>();
    public DbSet<Event> Event => Set<Event>();
    public DbSet<EventStatusHistory> EventStatusHistory => Set<EventStatusHistory>();
    public DbSet<EventMessage> EventMessage => Set<EventMessage>();
    public DbSet<Beacon> Beacon => Set<Beacon>();
    public DbSet<BeaconEnrollmentToken> BeaconEnrollmentToken => Set<BeaconEnrollmentToken>();
    public DbSet<BeaconLog> BeaconLog => Set<BeaconLog>();
    public DbSet<Location> Location => Set<Location>();
    public DbSet<Sponsor> Sponsor => Set<Sponsor>();
    public DbSet<SponsorYear> SponsorYear => Set<SponsorYear>();
    public DbSet<Person> Person => Set<Person>();
    public DbSet<Subscriber> Subscriber => Set<Subscriber>();
    public DbSet<CookieType> CookieType => Set<CookieType>();
    public DbSet<Cookie> Cookie => Set<Cookie>();
    public DbSet<ContactMessage> ContactMessage => Set<ContactMessage>();
    public DbSet<AppSetting> AppSetting => Set<AppSetting>();
    public DbSet<Snapshot> Snapshot => Set<Snapshot>();
    public DbSet<LiveState> LiveState => Set<LiveState>();
    public DbSet<Outbox> Outbox => Set<Outbox>();
    public DbSet<AlertDelivery> AlertDelivery => Set<AlertDelivery>();
    public DbSet<MediaAsset> MediaAsset => Set<MediaAsset>();
    public DbSet<Page> Page => Set<Page>();
    public DbSet<Section> Section => Set<Section>();
    public DbSet<SectionItem> SectionItem => Set<SectionItem>();
    public DbSet<SiteSettingDraft> SiteSettingDraft => Set<SiteSettingDraft>();
    public DbSet<ContentVersion> ContentVersion => Set<ContentVersion>();
    public DbSet<PreviewToken> PreviewToken => Set<PreviewToken>();
    public DbSet<IconLibraryState> IconLibraryState => Set<IconLibraryState>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        base.OnModelCreating(mb);

        mb.UseIdentityAlwaysColumns();

        // 3.1 event_status
        mb.Entity<EventStatus>(e =>
        {
            e.ToTable("event_status", t =>
                t.HasComment("Fixed lookup. Never edited through the API. Ids are the statusId values on every wire."));
            e.HasKey(x => x.Id).HasName("event_status_pkey");
            e.Property(x => x.Id).HasColumnType("smallint").ValueGeneratedNever();
            e.Property(x => x.Name).HasColumnType("text").IsRequired();
            e.HasAlternateKey(x => x.Name).HasName("event_status_name_key");
        });

        // 3.2 route
        mb.Entity<Route>(e =>
        {
            e.ToTable("route", t =>
                t.HasComment("Metadata for a route object on the CDN. The bytes live only in the bucket under s3_key; the row is never re-serialized from the database."));
            e.HasKey(x => x.Id).HasName("route_pkey");
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Name).HasColumnType("text").IsRequired();
            e.Property(x => x.S3Key).HasColumnName("s3_key").HasColumnType("text").IsRequired().HasComment("routes/{sha256}.json. Content-hashed, so an identical upload resolves to the existing row.");
            e.Property(x => x.Url).HasColumnType("text").IsRequired().HasComment("Absolute CDN URL of s3_key, carried into the snapshot as routeUrl.");
            e.Property(x => x.Sha256).HasColumnType("char(64)").IsRequired().HasComment("Lowercase hex SHA-256 of the canonical object bytes.");
            e.Property(x => x.PointCount).HasColumnType("integer").IsRequired();
            e.Property(x => x.UploadedBy).HasColumnType("text").IsRequired().HasComment("Admin email claim, or 'migration'.");
            e.Property(x => x.CreatedAt).HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
            e.HasAlternateKey(x => x.S3Key).HasName("route_s3_key_key");
        });

        // 3.3 event
        mb.Entity<Event>(e =>
        {
            e.ToTable("event", t =>
            {
                t.HasComment("One row per flyover. Status changes only when an admin changes them.");
                t.HasCheckConstraint("event_funds_percent_check", "funds_percent between 0 and 100");
            });
            e.HasKey(x => x.Id).HasName("event_pkey");
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Year).HasColumnType("integer").IsRequired();
            e.Property(x => x.Name).HasColumnType("text").IsRequired();
            e.Property(x => x.StatusId).HasColumnType("smallint").IsRequired()
                .HasComment("event_status.id. At most one row has 3 (event_one_live).");
            e.Property(x => x.IsCurrent).HasColumnType("boolean").IsRequired().HasDefaultValue(false)
                .HasComment("The event the public site shows. Set only by POST /admin/events/{id}/current. At most one true (event_one_current).");
            e.Property(x => x.ScheduledAt).HasColumnType("timestamptz")
                .HasComment("Admin-entered. Required while status_id = 2.");
            e.Property(x => x.WentLiveAt).HasColumnType("timestamptz")
                .HasComment("Stamped now() on every entry into status 3; admin-patchable.");
            e.Property(x => x.EndedAt).HasColumnType("timestamptz")
                .HasComment("Stamped now() on every entry into status 4; admin-patchable.");
            e.Property(x => x.FundsPercent).HasColumnType("integer").IsRequired().HasDefaultValue(0)
                .HasComment("Cheer meter, 0 to 100.");
            e.Property(x => x.RouteId).HasColumnType("bigint")
                .HasComment("Route shown for this event; null when unlinked.");
            e.Property(x => x.FinalCookieTally).HasColumnType("jsonb");
            e.Property(x => x.NextSeq).HasColumnType("bigint").IsRequired().HasDefaultValue(1L)
                .HasComment("Next location.seq for this event. Read and incremented under the row lock in the location transaction, so seq order is commit order.");
            e.Property(x => x.CreatedBy).HasColumnType("text").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
            e.HasAlternateKey(x => x.Year).HasName("event_year_key");

            e.HasOne<EventStatus>().WithMany().HasForeignKey(x => x.StatusId)
                .HasConstraintName("event_status_id_fkey").OnDelete(DeleteBehavior.NoAction);
            e.HasOne<Route>().WithMany().HasForeignKey(x => x.RouteId)
                .HasConstraintName("event_route_id_fkey").OnDelete(DeleteBehavior.NoAction);

            e.HasIndex(x => x.StatusId).HasDatabaseName("event_one_live").IsUnique().HasFilter("status_id = 3");
            e.HasIndex(x => x.IsCurrent).HasDatabaseName("event_one_current").IsUnique().HasFilter("is_current");
        });

        // 3.4 event_status_history
        mb.Entity<EventStatusHistory>(e =>
        {
            e.ToTable("event_status_history", t =>
                t.HasComment("One row per status change, written in the status change transaction."));
            e.HasKey(x => x.Id).HasName("event_status_history_pkey");
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.EventId).HasColumnType("bigint").IsRequired();
            e.Property(x => x.FromStatusId).HasColumnType("smallint")
                .HasComment("Null when there was no previous status.");
            e.Property(x => x.ToStatusId).HasColumnType("smallint").IsRequired();
            e.Property(x => x.ChangedBy).HasColumnType("text").IsRequired();
            e.Property(x => x.ChangedAt).HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
            e.HasOne<Event>().WithMany().HasForeignKey(x => x.EventId)
                .HasConstraintName("event_status_history_event_id_fkey").OnDelete(DeleteBehavior.Cascade);
            e.HasOne<EventStatus>().WithMany().HasForeignKey(x => x.FromStatusId)
                .HasConstraintName("event_status_history_from_status_id_fkey").OnDelete(DeleteBehavior.NoAction);
            e.HasOne<EventStatus>().WithMany().HasForeignKey(x => x.ToStatusId)
                .HasConstraintName("event_status_history_to_status_id_fkey").OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => new { x.EventId, x.ChangedAt })
                .HasDatabaseName("event_status_history_event")
                .IsDescending(false, true);
        });

        // 3.5 event_message
        mb.Entity<EventMessage>(e =>
        {
            e.ToTable("event_message", t =>
                t.HasComment("Messages shown on the site. The snapshot carries the one with the greatest created_at (ties: greatest id)."));
            e.HasKey(x => x.Id).HasName("event_message_pkey");
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.EventId).HasColumnType("bigint").IsRequired();
            e.Property(x => x.Body).HasColumnType("text").IsRequired();
            e.Property(x => x.EventTime).HasColumnType("timestamptz")
                .HasComment("The time the message is about, as entered by the admin. Display only.");
            e.Property(x => x.CreatedBy).HasColumnType("text").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
            e.HasOne<Event>().WithMany().HasForeignKey(x => x.EventId)
                .HasConstraintName("event_message_event_id_fkey").OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.EventId, x.CreatedAt })
                .HasDatabaseName("event_message_event_created")
                .IsDescending(false, true);
        });

        // 3.6 beacon
        mb.Entity<Beacon>(e =>
        {
            e.ToTable("beacon", t =>
            {
                t.HasComment("A trusted sender. Rows are never deleted; revoke is permanent.");
                t.HasCheckConstraint("beacon_role_check", "role in ('beacon', 'admin')");
            });
            e.HasKey(x => x.Id).HasName("beacon_pkey");
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Name).HasColumnType("text").IsRequired();
            e.Property(x => x.Notes).HasColumnType("text").IsRequired().HasDefaultValue("");
            e.Property(x => x.Role).HasColumnType("text").IsRequired()
                .HasComment("beacon or admin. Immutable after creation.");
            e.Property(x => x.KeyHash).HasColumnType("bytea").IsRequired()
                .HasComment("sha256 of the plaintext key, 32 bytes. The plaintext is never stored.");
            e.Property(x => x.KeyPrefix).HasColumnType("text").IsRequired()
                .HasComment("First 12 characters of the key, for display.");
            e.Property(x => x.KeyVersion).HasColumnType("integer").IsRequired().HasDefaultValue(1)
                .HasComment("Incremented by rotate. Carried in the hub identity as \"<id>:<key_version>\"; the message path rejects a stale version.");
            e.Property(x => x.IsActive).HasColumnType("boolean").IsRequired().HasDefaultValue(false)
                .HasComment("The one beacon whose updates fan out. At most one true (beacon_one_active). Zero is allowed.");
            e.Property(x => x.RevokedAt).HasColumnType("timestamptz")
                .HasComment("Set once by revoke. A revoked beacon never authenticates again.");
            e.Property(x => x.LastSeenAt).HasColumnType("timestamptz")
                .HasComment("Any authenticated contact: REST call, hub authorize, hub message.");
            e.Property(x => x.LastLocationAt).HasColumnType("timestamptz")
                .HasComment("Last stored location, published or not.");
            e.Property(x => x.LastHeartbeatAt).HasColumnType("timestamptz")
                .HasComment("Last stored heartbeat.");
            e.Property(x => x.StaleSince).HasColumnType("timestamptz")
                .HasComment("Set by the stale-beacon chore; cleared by a heartbeat or a stored location.");
            e.Property(x => x.Telemetry).HasColumnType("jsonb")
                .HasComment("The last heartbeat body, stored as received.");
            e.Property(x => x.CreatedBy).HasColumnType("text").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
            e.HasAlternateKey(x => x.KeyHash).HasName("beacon_key_hash_key");
            e.HasIndex(x => x.IsActive).HasDatabaseName("beacon_one_active").IsUnique().HasFilter("is_active");
        });

        // 3.7 beacon_enrollment_token
        mb.Entity<BeaconEnrollmentToken>(e =>
        {
            e.ToTable("beacon_enrollment_token", t =>
                t.HasComment("One-time tokens behind the enrollment QR code. 15-minute lifetime, single use."));
            e.HasKey(x => x.Id).HasName("beacon_enrollment_token_pkey");
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.BeaconId).HasColumnType("bigint").IsRequired();
            e.Property(x => x.TokenHash).HasColumnType("bytea").IsRequired()
                .HasComment("sha256 of the wet_ token.");
            e.Property(x => x.KeyCiphertext).HasColumnType("bytea")
                .HasComment("AES-256-GCM of the plaintext beacon key (12-byte nonce prepended, 16-byte tag appended). Nulled when consumed.");
            e.Property(x => x.ExpiresAt).HasColumnType("timestamptz").IsRequired();
            e.Property(x => x.ConsumedAt).HasColumnType("timestamptz");
            e.Property(x => x.CreatedAt).HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
            e.HasAlternateKey(x => x.TokenHash).HasName("beacon_enrollment_token_token_hash_key");
            e.HasOne<Beacon>().WithMany().HasForeignKey(x => x.BeaconId)
                .HasConstraintName("beacon_enrollment_token_beacon_id_fkey").OnDelete(DeleteBehavior.Cascade);
        });

        // 3.8 beacon_log
        mb.Entity<BeaconLog>(e =>
        {
            e.ToTable("beacon_log", t =>
                t.HasComment("Red-Nose debug log uploads (admin-role beacons). Up to 2 MB each. 30-day retention."));
            e.HasKey(x => x.Id).HasName("beacon_log_pkey");
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.BeaconId).HasColumnType("bigint").IsRequired();
            e.Property(x => x.ReceivedAt).HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
            e.Property(x => x.AppVersion).HasColumnType("text");
            e.Property(x => x.SizeBytes).HasColumnType("integer").IsRequired();
            e.Property(x => x.Body).HasColumnType("text").IsRequired();
            e.HasOne<Beacon>().WithMany().HasForeignKey(x => x.BeaconId)
                .HasConstraintName("beacon_log_beacon_id_fkey").OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.BeaconId, x.ReceivedAt })
                .HasDatabaseName("beacon_log_beacon")
                .IsDescending(false, true);
        });

        // 3.9 location
        mb.Entity<Location>(e =>
        {
            e.ToTable("location", t =>
                t.HasComment("Every stored fix. Kept forever; exported by admins; never read by the public site."));
            e.HasKey(x => x.Id).HasName("location_pkey");
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.EventId).HasColumnType("bigint").IsRequired();
            e.Property(x => x.BeaconId).HasColumnType("bigint").IsRequired();
            e.Property(x => x.Seq).HasColumnType("bigint").IsRequired()
                .HasComment("Arrival order within the event, from event.next_seq. The only order that exists.");
            e.Property(x => x.RecordedAt).HasColumnType("timestamptz").IsRequired()
                .HasComment("The fix time the beacon sent. Informational; it never decides anything.");
            e.Property(x => x.ReceivedAt).HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()")
                .HasComment("When the API stored the row.");
            e.Property(x => x.Lat).HasColumnType("double precision").IsRequired();
            e.Property(x => x.Lng).HasColumnType("double precision").IsRequired();
            e.Property(x => x.SpeedMps).HasColumnType("double precision");
            e.Property(x => x.AltitudeM).HasColumnType("double precision");
            e.Property(x => x.HeadingDeg).HasColumnType("double precision");
            e.Property(x => x.AccuracyM).HasColumnType("double precision");
            e.Property(x => x.Published).HasColumnType("boolean").IsRequired()
                .HasComment("beacon.is_active at the moment of the insert. Only published rows reach the live object.");
            e.HasOne<Event>().WithMany().HasForeignKey(x => x.EventId)
                .HasConstraintName("location_event_id_fkey").OnDelete(DeleteBehavior.NoAction);
            e.HasOne<Beacon>().WithMany().HasForeignKey(x => x.BeaconId)
                .HasConstraintName("location_beacon_id_fkey").OnDelete(DeleteBehavior.NoAction);
            e.HasAlternateKey(x => new { x.EventId, x.Seq }).HasName("location_event_id_seq_key");
            e.HasIndex(x => new { x.EventId, x.Seq })
                .HasDatabaseName("location_event_published_seq")
                .IsDescending(false, true)
                .HasFilter("published");
        });

        // 3.21 media_asset (before sponsor since sponsor references it)
        mb.Entity<MediaAsset>(e =>
        {
            e.ToTable("media_asset", t =>
            {
                t.HasComment("The media library. pending: ticket issued, bytes may or may not be in the bucket. ready: confirmed. orphaned: unreferenced for 30 days, objects tagged for lifecycle expiry; the row is deleted 8 days later.");
                t.HasCheckConstraint("media_asset_kind_check", "kind in ('raster', 'svg', 'gif')");
                t.HasCheckConstraint("media_asset_state_check", "state in ('pending', 'ready', 'orphaned')");
            });
            e.HasKey(x => x.Id).HasName("media_asset_pkey");
            e.Property(x => x.Id).HasColumnType("uuid").ValueGeneratedNever()
                .HasComment("Minted by the API (UUID v4) when the upload ticket is issued; it is the key segment media/{id}/.");
            e.Property(x => x.Filename).HasColumnType("text").IsRequired();
            e.Property(x => x.ContentType).HasColumnType("text").IsRequired();
            e.Property(x => x.Kind).HasColumnType("text").IsRequired()
                .HasComment("raster: png, jpeg, webp (variants derived). gif: stored as is. svg: validated, usable as an icon.");
            e.Property(x => x.State).HasColumnType("text").IsRequired();
            e.Property(x => x.S3Key).HasColumnName("s3_key").HasColumnType("text").IsRequired()
                .HasComment("media/{id}/{filename}; the original bytes.");
            e.Property(x => x.SizeBytes).HasColumnType("bigint");
            e.Property(x => x.Width).HasColumnType("integer");
            e.Property(x => x.Height).HasColumnType("integer");
            e.Property(x => x.Sha256).HasColumnType("char(64)");
            e.Property(x => x.Variants).HasColumnType("jsonb").IsRequired().HasDefaultValueSql("'{}'")
                .HasComment("{ \"480\": \"media/{id}/w480.webp\", \"960\": ..., \"1600\": ... }: the WebP width variants that exist (raster only, widths below the source width).");
            e.Property(x => x.Alt).HasColumnType("text").IsRequired().HasDefaultValue("");
            e.Property(x => x.Title).HasColumnType("text").IsRequired().HasDefaultValue("");
            e.Property(x => x.UploadedBy).HasColumnType("text").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
            e.Property(x => x.ConfirmedAt).HasColumnType("timestamptz");
            e.Property(x => x.UnreferencedSince).HasColumnType("timestamptz");
            e.Property(x => x.OrphanedAt).HasColumnType("timestamptz");
            e.HasAlternateKey(x => x.S3Key).HasName("media_asset_s3_key_key");
            e.HasIndex(x => new { x.State, x.CreatedAt }).HasDatabaseName("media_asset_state_created");
        });

        // 3.10 sponsor
        mb.Entity<Sponsor>(e =>
        {
            e.ToTable("sponsor", t =>
                t.HasComment("Sponsor master data. contact_person, email, phone, and address are admin-only and never reach the snapshot."));
            e.HasKey(x => x.Id).HasName("sponsor_pkey");
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Name).HasColumnType("text").IsRequired();
            e.Property(x => x.ContactPerson).HasColumnType("text");
            e.Property(x => x.Email).HasColumnType("text");
            e.Property(x => x.Phone).HasColumnType("text");
            e.Property(x => x.Address).HasColumnType("text");
            e.Property(x => x.WebsiteUrl).HasColumnType("text");
            e.Property(x => x.FbUrl).HasColumnType("text");
            e.Property(x => x.IgUrl).HasColumnType("text");
            e.Property(x => x.LogoMediaId).HasColumnType("uuid")
                .HasComment("A ready media_asset chosen from the library; null when no logo. The API refuses to delete a referenced asset (409 media_in_use).");
            e.Property(x => x.CreatedAt).HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
            e.HasOne<MediaAsset>().WithMany().HasForeignKey(x => x.LogoMediaId)
                .HasConstraintName("sponsor_logo_media_id_fkey").OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => x.LogoMediaId).HasDatabaseName("sponsor_logo_media").HasFilter("logo_media_id is not null");
        });

        // 3.11 sponsor_year
        mb.Entity<SponsorYear>(e =>
        {
            e.ToTable("sponsor_year", t =>
            {
                t.HasComment("A sponsor's participation in one year. Upserted on (sponsor_id, event_year).");
                t.HasCheckConstraint("sponsor_year_amount_donated_check", "amount_donated >= 0");
            });
            e.HasKey(x => x.Id).HasName("sponsor_year_pkey");
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.SponsorId).HasColumnType("bigint").IsRequired();
            e.Property(x => x.EventYear).HasColumnType("integer").IsRequired();
            e.Property(x => x.AmountDonated).HasColumnType("numeric(12,2)")
                .HasComment("Admin-only. Drives lingerMs; never appears in a public object.");
            e.Property(x => x.Active).HasColumnType("boolean").IsRequired().HasDefaultValue(true);
            e.Property(x => x.CanAdvertise).HasColumnType("boolean").IsRequired().HasDefaultValue(true);
            e.Property(x => x.Anonymous).HasColumnType("boolean").IsRequired().HasDefaultValue(false);
            e.Property(x => x.RegisteredAt).HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
            e.HasAlternateKey(x => new { x.SponsorId, x.EventYear }).HasName("sponsor_year_sponsor_id_event_year_key");
            e.HasOne<Sponsor>().WithMany().HasForeignKey(x => x.SponsorId)
                .HasConstraintName("sponsor_year_sponsor_id_fkey").OnDelete(DeleteBehavior.Cascade);
        });

        // 3.12 person
        mb.Entity<Person>(e =>
        {
            e.ToTable("person", t =>
                t.HasComment("A registered person. Upserted on every authenticated request from the ID token (sub, email)."));
            e.HasKey(x => x.Id).HasName("person_pkey");
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.CognitoSub).HasColumnType("uuid").IsRequired();
            e.Property(x => x.Email).HasColumnType("text").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
            e.Property(x => x.LastSeenAt).HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
            e.HasAlternateKey(x => x.CognitoSub).HasName("person_cognito_sub_key");
        });

        // 3.13 subscriber
        mb.Entity<Subscriber>(e =>
        {
            e.ToTable("subscriber", t =>
            {
                t.HasComment("Alert subscriptions. channel is email in v1; sms is reserved and rejected by the API.");
                t.HasCheckConstraint("subscriber_channel_check", "channel in ('email', 'sms')");
            });
            e.HasKey(x => x.Id).HasName("subscriber_pkey");
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.PersonId).HasColumnType("bigint").IsRequired();
            e.Property(x => x.Channel).HasColumnType("text").IsRequired();
            e.Property(x => x.Address).HasColumnType("text").IsRequired()
                .HasComment("Stored trimmed and lowercased.");
            e.Property(x => x.VerifyTokenHash).HasColumnType("bytea")
                .HasComment("sha256 of the last minted wsv_ token. Kept after verification until the next mint so the verify page is idempotent.");
            e.Property(x => x.VerifyExpiresAt).HasColumnType("timestamptz");
            e.Property(x => x.VerifiedAt).HasColumnType("timestamptz");
            e.Property(x => x.UnsubscribeToken).HasColumnType("text").IsRequired()
                .HasComment("Plaintext wsu_ token, stable for the life of the row.");
            e.Property(x => x.UnsubscribedAt).HasColumnType("timestamptz");
            e.Property(x => x.CreatedAt).HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
            e.HasAlternateKey(x => x.UnsubscribeToken).HasName("subscriber_unsubscribe_token_key");
            e.HasAlternateKey(x => new { x.Channel, x.Address }).HasName("subscriber_channel_address_key");
            e.HasOne<Person>().WithMany().HasForeignKey(x => x.PersonId)
                .HasConstraintName("subscriber_person_id_fkey").OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.PersonId).HasDatabaseName("subscriber_person");
            e.HasIndex(x => x.VerifyTokenHash).HasDatabaseName("subscriber_verify_token_hash").HasFilter("verify_token_hash is not null");
        });

        // 3.14 cookie_type
        mb.Entity<CookieType>(e =>
        {
            e.ToTable("cookie_type", t =>
                t.HasComment("Admin-managed. Locked (409 event_live) while any event has status 3. No delete; active = false removes a type from the snapshot."));
            e.HasKey(x => x.Id).HasName("cookie_type_pkey");
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Name).HasColumnType("text").IsRequired();
            e.Property(x => x.Icon).HasColumnType("jsonb")
                .HasComment("Icon value (contracts 1.3a): {\"source\":\"library\",\"id\":\"cookie\"} or {\"source\":\"media\",\"id\":\"<uuid of a ready svg media_asset>\"}; null until chosen.");
            e.Property(x => x.Sort).HasColumnType("integer").IsRequired().HasDefaultValue(0);
            e.Property(x => x.Active).HasColumnType("boolean").IsRequired().HasDefaultValue(true);
            e.Property(x => x.CreatedAt).HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
        });

        // 3.15 cookie
        mb.Entity<Cookie>(e =>
        {
            e.ToTable("cookie", t =>
                t.HasComment("A cookie left by a registered person during a live event. No location."));
            e.HasKey(x => x.Id).HasName("cookie_pkey");
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.EventId).HasColumnType("bigint").IsRequired();
            e.Property(x => x.PersonId).HasColumnType("bigint").IsRequired();
            e.Property(x => x.CookieTypeId).HasColumnType("bigint").IsRequired();
            e.Property(x => x.Note).HasColumnType("text")
                .HasComment("Never public. Visible to admins only.");
            e.Property(x => x.LeftAt).HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
            e.Property(x => x.HiddenAt).HasColumnType("timestamptz")
                .HasComment("Soft delete for moderation. Hidden cookies leave the tally but still count toward the per-person limit.");
            e.Property(x => x.HiddenBy).HasColumnType("text");
            e.HasOne<Event>().WithMany().HasForeignKey(x => x.EventId)
                .HasConstraintName("cookie_event_id_fkey").OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Person>().WithMany().HasForeignKey(x => x.PersonId)
                .HasConstraintName("cookie_person_id_fkey").OnDelete(DeleteBehavior.Cascade);
            e.HasOne<CookieType>().WithMany().HasForeignKey(x => x.CookieTypeId)
                .HasConstraintName("cookie_cookie_type_id_fkey").OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => new { x.EventId, x.PersonId }).HasDatabaseName("cookie_event_person");
            e.HasIndex(x => new { x.EventId, x.CookieTypeId })
                .HasDatabaseName("cookie_event_type_visible")
                .HasFilter("hidden_at is null");
            e.HasIndex(x => x.PersonId).HasDatabaseName("cookie_person");
        });

        // 3.16 contact_message
        mb.Entity<ContactMessage>(e =>
        {
            e.ToTable("contact_message", t =>
                t.HasComment("Contact form submissions. No automatic retention; admins delete."));
            e.HasKey(x => x.Id).HasName("contact_message_pkey");
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Name).HasColumnType("text").IsRequired();
            e.Property(x => x.Email).HasColumnType("text").IsRequired();
            e.Property(x => x.Body).HasColumnType("text").IsRequired();
            e.Property(x => x.ClientIp).HasColumnType("text").IsRequired()
                .HasComment("Client IP per the trusted-proxy rule; empty string for migrated rows.");
            e.Property(x => x.CreatedAt).HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
        });

        // 3.17 app_setting
        mb.Entity<AppSetting>(e =>
        {
            e.ToTable("app_setting", t =>
                t.HasComment("The admin knobs, one row per key. Only PUT /admin/settings/{key} changes a value. A missing row means the compiled default."));
            e.HasKey(x => x.Key).HasName("app_setting_pkey");
            e.Property(x => x.Key).HasColumnType("text").IsRequired();
            e.Property(x => x.Value).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.UpdatedBy).HasColumnType("text").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
        });

        // 3.18 snapshot
        mb.Entity<Snapshot>(e =>
        {
            e.ToTable("snapshot", t =>
            {
                t.HasComment("Single row (id = 1). The current snapshot object. Rewritten in the same transaction as every snapshot-affecting write. Inserted by the first boot of an environment; absent means the environment has never finished booting.");
                t.HasCheckConstraint("snapshot_id_check", "id = 1");
            });
            e.HasKey(x => x.Id).HasName("snapshot_pkey");
            e.Property(x => x.Id).HasColumnType("smallint").ValueGeneratedNever();
            e.Property(x => x.Version).HasColumnType("bigint").IsRequired()
                .HasComment("Increments on every rebuild, including rebuilds that produce identical bytes. Nodes watch it on the reconcile tick.");
            e.Property(x => x.Url).HasColumnType("text").IsRequired();
            e.Property(x => x.S3Key).HasColumnName("s3_key").HasColumnType("text").IsRequired();
            e.Property(x => x.BuiltAt).HasColumnType("timestamptz").IsRequired();
        });

        // 3.18 live_state
        mb.Entity<LiveState>(e =>
        {
            e.ToTable("live_state", t =>
            {
                t.HasComment("Single row (id = 1). Fleet-wide outcome of the last live-object write. Returned by GET /admin/live.");
                t.HasCheckConstraint("live_state_id_check", "id = 1");
            });
            e.HasKey(x => x.Id).HasName("live_state_pkey");
            e.Property(x => x.Id).HasColumnType("smallint").ValueGeneratedNever();
            e.Property(x => x.LastWriteAt).HasColumnType("timestamptz");
            e.Property(x => x.LastWriteSeq).HasColumnType("bigint");
            e.Property(x => x.LastWriteVersion).HasColumnType("bigint");
            e.Property(x => x.LastWriteError).HasColumnType("text");
            e.Property(x => x.LastWriteNode).HasColumnType("text");
        });

        // 3.19 outbox
        mb.Entity<Outbox>(e =>
        {
            e.ToTable("outbox", t =>
                t.HasComment("Work for the leader chores, written in the same transaction as the change it announces. Topics: event.status_changed, event.message_posted, subscription.verify, contact.received."));
            e.HasKey(x => x.Id).HasName("outbox_pkey");
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Topic).HasColumnType("text").IsRequired();
            e.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
            e.Property(x => x.ClaimedAt).HasColumnType("timestamptz")
                .HasComment("Set by the claim query; a claim older than 2 minutes is reclaimable.");
            e.Property(x => x.PublishedAt).HasColumnType("timestamptz");
            e.Property(x => x.Attempts).HasColumnType("integer").IsRequired().HasDefaultValue(0)
                .HasComment("Rows with 5 attempts stay unpublished and are visible in the logs.");
            e.Property(x => x.LastError).HasColumnType("text");
            e.HasIndex(x => x.Id).HasDatabaseName("outbox_unpublished").HasFilter("published_at is null");
        });

        // 3.20 alert_delivery
        mb.Entity<AlertDelivery>(e =>
        {
            e.ToTable("alert_delivery", t =>
                t.HasComment("One email per (subscriber, outbox row). The unique constraint is what makes overlapping leaders safe."));
            e.HasKey(x => x.Id).HasName("alert_delivery_pkey");
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.OutboxId).HasColumnType("bigint").IsRequired();
            e.Property(x => x.SubscriberId).HasColumnType("bigint").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
            e.Property(x => x.SentAt).HasColumnType("timestamptz");
            e.Property(x => x.SesMessageId).HasColumnType("text");
            e.Property(x => x.Attempts).HasColumnType("integer").IsRequired().HasDefaultValue(0);
            e.Property(x => x.LastError).HasColumnType("text");
            e.HasAlternateKey(x => new { x.SubscriberId, x.OutboxId }).HasName("alert_delivery_subscriber_id_outbox_id_key");
            e.HasOne<Outbox>().WithMany().HasForeignKey(x => x.OutboxId)
                .HasConstraintName("alert_delivery_outbox_id_fkey").OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Subscriber>().WithMany().HasForeignKey(x => x.SubscriberId)
                .HasConstraintName("alert_delivery_subscriber_id_fkey").OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.Id).HasDatabaseName("alert_delivery_unsent").HasFilter("sent_at is null");
            e.HasIndex(x => x.OutboxId).HasDatabaseName("alert_delivery_outbox");
        });

        // 3.22 page
        mb.Entity<Page>(e =>
        {
            e.ToTable("page", t =>
            {
                t.HasComment("Working set. The six role pages are seeded, undeletable, and role-immutable; none pages render at /<slug>. Hidden pages are omitted at publish.");
                t.HasCheckConstraint("page_role_check", "role in ('none', 'no_event', 'planned', 'scheduled', 'live', 'ended', 'cancelled')");
            });
            e.HasKey(x => x.Id).HasName("page_pkey");
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Slug).HasColumnType("text").IsRequired()
                .HasComment("One lowercase path segment, ^[a-z0-9]+(-[a-z0-9]+)*$, 1 to 60; never auth, preview, api, admin, assets. Validated by the API.");
            e.Property(x => x.Title).HasColumnType("text").IsRequired();
            e.Property(x => x.NavLabel).HasColumnType("text")
                .HasComment("Nav entry text; null keeps the page out of the nav. Always null on role pages.");
            e.Property(x => x.NavPosition).HasColumnType("integer").IsRequired().HasDefaultValue(0);
            e.Property(x => x.IsHidden).HasColumnType("boolean").IsRequired().HasDefaultValue(false);
            e.Property(x => x.Role).HasColumnType("text").IsRequired().HasDefaultValue("none");
            e.Property(x => x.CreatedBy).HasColumnType("text").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedBy).HasColumnType("text").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
            e.HasAlternateKey(x => x.Slug).HasName("page_slug_key");
            e.HasIndex(x => x.Role).HasDatabaseName("page_one_per_role").IsUnique().HasFilter("role <> 'none'");
        });

        // 3.23 section
        mb.Entity<Section>(e =>
        {
            e.ToTable("section", t =>
                t.HasComment("Working set. kind names a registry entry (contracts 1.3a); data is validated against the kind schema, presentation against the shared presentation schema, at the draft level on write and at the publish level on publish."));
            e.HasKey(x => x.Id).HasName("section_pkey");
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.PageId).HasColumnType("bigint").IsRequired();
            e.Property(x => x.Kind).HasColumnType("text").IsRequired();
            e.Property(x => x.Position).HasColumnType("integer").IsRequired()
                .HasComment("Compacted to 0, 1, 2, ... within the page after every reorder, move, insert, and delete.");
            e.Property(x => x.IsHidden).HasColumnType("boolean").IsRequired().HasDefaultValue(false);
            e.Property(x => x.Data).HasColumnType("jsonb").IsRequired().HasDefaultValueSql("'{}'");
            e.Property(x => x.Presentation).HasColumnType("jsonb").IsRequired().HasDefaultValueSql("'{}'");
            e.Property(x => x.CreatedAt).HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedBy).HasColumnType("text").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
            e.HasOne<Page>().WithMany().HasForeignKey(x => x.PageId)
                .HasConstraintName("section_page_id_fkey").OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.PageId, x.Position, x.Id }).HasDatabaseName("section_page_position");
        });

        // 3.24 section_item
        mb.Entity<SectionItem>(e =>
        {
            e.ToTable("section_item", t =>
                t.HasComment("Working set. Repeatable items of a section whose kind has an item schema (media, links, icon_row). position compacted like section.position."));
            e.HasKey(x => x.Id).HasName("section_item_pkey");
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.SectionId).HasColumnType("bigint").IsRequired();
            e.Property(x => x.Position).HasColumnType("integer").IsRequired();
            e.Property(x => x.IsHidden).HasColumnType("boolean").IsRequired().HasDefaultValue(false);
            e.Property(x => x.Data).HasColumnType("jsonb").IsRequired().HasDefaultValueSql("'{}'");
            e.Property(x => x.CreatedAt).HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedBy).HasColumnType("text").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
            e.HasOne<Section>().WithMany().HasForeignKey(x => x.SectionId)
                .HasConstraintName("section_item_section_id_fkey").OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.SectionId, x.Position, x.Id }).HasDatabaseName("section_item_section_position");
        });

        // 3.25 site_setting_draft
        mb.Entity<SiteSettingDraft>(e =>
        {
            e.ToTable("site_setting_draft", t =>
            {
                t.HasComment("Single row (id = 1). The site settings working set (contracts 1.3a SiteSettings), draft-validated on PUT, publish-validated on publish. Seeded from the starter content on first boot.");
                t.HasCheckConstraint("site_setting_draft_id_check", "id = 1");
            });
            e.HasKey(x => x.Id).HasName("site_setting_draft_pkey");
            e.Property(x => x.Id).HasColumnType("smallint").ValueGeneratedNever();
            e.Property(x => x.Data).HasColumnType("jsonb").IsRequired().HasDefaultValueSql("'{}'");
            e.Property(x => x.UpdatedBy).HasColumnType("text");
            e.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        });

        // 3.26 content_version
        mb.Entity<ContentVersion>(e =>
        {
            e.ToTable("content_version", t =>
                t.HasComment("Published content documents. The row with the greatest id is what the snapshot carries; the newest 50 are kept, older rows are deleted in the publish transaction. Restore copies a row back into the working set and publishes nothing."));
            e.HasKey(x => x.Id).HasName("content_version_pkey");
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Document).HasColumnType("jsonb").IsRequired()
                .HasComment("ContentDocument (contracts 1.3a) exactly as the snapshot embeds it: hidden rows omitted, canonical order.");
            e.Property(x => x.Sha256).HasColumnType("char(64)").IsRequired()
                .HasComment("SHA-256 of the canonical bytes of document; POST /admin/content/publish answers 409 content_unchanged when the working set hashes to the newest row.");
            e.Property(x => x.MediaIds).HasColumnType("uuid[]").IsRequired().HasDefaultValueSql("'{}'")
                .HasComment("Every media_asset id the document references (MediaRef, media icons, background media). The orphan chore treats these as referenced while the row exists.");
            e.Property(x => x.Label).HasColumnType("text");
            e.Property(x => x.PublishedBy).HasColumnType("text").IsRequired();
            e.Property(x => x.PublishedAt).HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
        });

        // 3.27 preview_token
        mb.Entity<PreviewToken>(e =>
        {
            e.ToTable("preview_token", t =>
                t.HasComment("wpv_ tokens minted by POST /admin/content/preview-token, sha256 at rest, 15 minutes, reusable until expiry, deleted by the nightly cleanup 24 h after expiry. Database-backed so any node can answer GET /preview/document."));
            e.HasKey(x => x.Id).HasName("preview_token_pkey");
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.TokenHash).HasColumnType("bytea").IsRequired();
            e.Property(x => x.CreatedBy).HasColumnType("text").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
            e.Property(x => x.ExpiresAt).HasColumnType("timestamptz").IsRequired();
            e.HasAlternateKey(x => x.TokenHash).HasName("preview_token_token_hash_key");
        });

        // 3.28 icon_library_state
        mb.Entity<IconLibraryState>(e =>
        {
            e.ToTable("icon_library_state", t =>
            {
                t.HasComment("Single row (id = 1). Hash of the icon library the fleet last wrote to the bucket. The boot migrator compares it with the compiled library and writes the icons when they differ (section 8.16).");
                t.HasCheckConstraint("icon_library_state_id_check", "id = 1");
            });
            e.HasKey(x => x.Id).HasName("icon_library_state_pkey");
            e.Property(x => x.Id).HasColumnType("smallint").ValueGeneratedNever();
            e.Property(x => x.LibrarySha256).HasColumnType("char(64)");
            e.Property(x => x.WrittenAt).HasColumnType("timestamptz");
        });
    }
}
