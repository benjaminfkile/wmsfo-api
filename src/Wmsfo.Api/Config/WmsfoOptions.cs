using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Wmsfo.Api.Config;

// Contracts 8.1 environment keys bound to a single options object.
// Validation is api.md 4: fail fast, print key names never values, exit code 2.
// WMSFO_FORCE_LEADER, WMSFO_OBJECT_STORE_DIR, WMSFO_DEV_STATIC_TOKENS,
// WMSFO_SES_DRY_RUN are refused when WMSFO_ENV is "prod".
public sealed class WmsfoOptions
{
    public const string SectionName = "Wmsfo";

    public string AspNetCoreUrls { get; set; } = "";
    public string Env { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public string DbConnection { get; set; } = "";
    public string DbMigrationConnection { get; set; } = "";
    public string AwsRegion { get; set; } = "";
    public string S3Bucket { get; set; } = "";
    public string CdnBaseUrl { get; set; } = "";
    public string PublicApiBaseUrl { get; set; } = "";
    public string SiteBaseUrl { get; set; } = "";
    public string HubUrl { get; set; } = "";
    public string GatewayInternalUrl { get; set; } = "";
    public string? GatewayRealtimeToken { get; set; }
    public string CorsOrigins { get; set; } = "";
    public int TrustedProxyHops { get; set; } = 2;
    public string CognitoIssuer { get; set; } = "";
    public string CognitoClientIds { get; set; } = "";
    public string CognitoUserPoolId { get; set; } = "";
    // api.md 6.2: the admin pool (contracts 3.1). Empty means the people pool
    // also carries the admins (single-pool mode, refused in prod).
    public string CognitoAdminIssuer { get; set; } = "";
    public string CognitoAdminClientIds { get; set; } = "";
    public string CognitoAdminUserPoolId { get; set; } = "";
    public string AdminGroup { get; set; } = "admin";
    public string EditorGroup { get; set; } = "editor";
    public string SesFromAddress { get; set; } = "";
    public string SesConfigurationSet { get; set; } = "";
    public string ContactNotifyEmail { get; set; } = "";
    public int AlertSendPerSec { get; set; } = 10;
    public string EnrollmentEncryptionKey { get; set; } = "";
    public int ReconcileTickMs { get; set; } = 1000;
    public string LogLevel { get; set; } = "Information";
    public bool ForceLeader { get; set; }
    public string? ObjectStoreDir { get; set; }
    public bool DevStaticTokens { get; set; }
    public bool SesDryRun { get; set; }

    // The env keys, exactly as they appear in the secret / environment.
    public static class Keys
    {
        public const string AspNetCoreUrls = "ASPNETCORE_URLS";
        public const string Env = "WMSFO_ENV";
        public const string ServiceName = "WMSFO_SERVICE_NAME";
        public const string DbConnection = "WMSFO_DB_CONNECTION";
        public const string DbMigrationConnection = "WMSFO_DB_MIGRATION_CONNECTION";
        public const string AwsRegion = "AWS_REGION";
        public const string S3Bucket = "WMSFO_S3_BUCKET";
        public const string CdnBaseUrl = "WMSFO_CDN_BASE_URL";
        public const string PublicApiBaseUrl = "WMSFO_PUBLIC_API_BASE_URL";
        public const string SiteBaseUrl = "WMSFO_SITE_BASE_URL";
        public const string HubUrl = "WMSFO_HUB_URL";
        public const string GatewayInternalUrl = "WMSFO_GATEWAY_INTERNAL_URL";
        public const string GatewayRealtimeToken = "GATEWAY_REALTIME_TOKEN";
        public const string CorsOrigins = "WMSFO_CORS_ORIGINS";
        public const string TrustedProxyHops = "WMSFO_TRUSTED_PROXY_HOPS";
        public const string CognitoIssuer = "WMSFO_COGNITO_ISSUER";
        public const string CognitoClientIds = "WMSFO_COGNITO_CLIENT_IDS";
        public const string CognitoUserPoolId = "WMSFO_COGNITO_USER_POOL_ID";
        public const string CognitoAdminIssuer = "WMSFO_COGNITO_ADMIN_ISSUER";
        public const string CognitoAdminClientIds = "WMSFO_COGNITO_ADMIN_CLIENT_IDS";
        public const string CognitoAdminUserPoolId = "WMSFO_COGNITO_ADMIN_USER_POOL_ID";
        public const string AdminGroup = "WMSFO_ADMIN_GROUP";
        public const string EditorGroup = "WMSFO_EDITOR_GROUP";
        public const string SesFromAddress = "WMSFO_SES_FROM_ADDRESS";
        public const string SesConfigurationSet = "WMSFO_SES_CONFIGURATION_SET";
        public const string ContactNotifyEmail = "WMSFO_CONTACT_NOTIFY_EMAIL";
        public const string AlertSendPerSec = "WMSFO_ALERT_SEND_PER_SEC";
        public const string EnrollmentEncryptionKey = "WMSFO_ENROLLMENT_ENCRYPTION_KEY";
        public const string ReconcileTickMs = "WMSFO_RECONCILE_TICK_MS";
        public const string LogLevel = "WMSFO_LOG_LEVEL";
        public const string ForceLeader = "WMSFO_FORCE_LEADER";
        public const string ObjectStoreDir = "WMSFO_OBJECT_STORE_DIR";
        public const string DevStaticTokens = "WMSFO_DEV_STATIC_TOKENS";
        public const string SesDryRun = "WMSFO_SES_DRY_RUN";
    }

    public IReadOnlyList<string> CognitoClientIdList()
    {
        return SplitCsv(CognitoClientIds);
    }

    public bool HasSeparateAdminPool => !string.IsNullOrWhiteSpace(CognitoAdminIssuer);
    public string EffectiveAdminIssuer => HasSeparateAdminPool ? CognitoAdminIssuer : CognitoIssuer;
    public string EffectiveAdminUserPoolId => HasSeparateAdminPool ? CognitoAdminUserPoolId : CognitoUserPoolId;
    public IReadOnlyList<string> EffectiveAdminClientIdList()
    {
        return HasSeparateAdminPool ? SplitCsv(CognitoAdminClientIds) : SplitCsv(CognitoClientIds);
    }

    public IReadOnlyList<string> CorsOriginList()
    {
        return SplitCsv(CorsOrigins);
    }

    public static WmsfoOptions Bind(IConfiguration configuration)
    {
        // Environment variables come in flat, so pull them by key rather than by section.
        string Get(string key) => configuration[key] ?? "";
        bool GetBool(string key, bool @default)
        {
            var value = configuration[key];
            if (string.IsNullOrEmpty(value)) return @default;
            return value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value == "1"
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }
        int GetInt(string key, int @default)
        {
            var value = configuration[key];
            if (string.IsNullOrEmpty(value)) return @default;
            return int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                ? parsed : @default;
        }

        return new WmsfoOptions
        {
            AspNetCoreUrls = Get(Keys.AspNetCoreUrls),
            Env = Get(Keys.Env),
            ServiceName = Get(Keys.ServiceName),
            DbConnection = Get(Keys.DbConnection),
            DbMigrationConnection = Get(Keys.DbMigrationConnection),
            AwsRegion = Get(Keys.AwsRegion),
            S3Bucket = Get(Keys.S3Bucket),
            CdnBaseUrl = Get(Keys.CdnBaseUrl),
            PublicApiBaseUrl = Get(Keys.PublicApiBaseUrl),
            SiteBaseUrl = Get(Keys.SiteBaseUrl),
            HubUrl = Get(Keys.HubUrl),
            GatewayInternalUrl = Get(Keys.GatewayInternalUrl),
            GatewayRealtimeToken = string.IsNullOrEmpty(configuration[Keys.GatewayRealtimeToken]) ? null : configuration[Keys.GatewayRealtimeToken],
            CorsOrigins = Get(Keys.CorsOrigins),
            TrustedProxyHops = GetInt(Keys.TrustedProxyHops, 2),
            CognitoIssuer = Get(Keys.CognitoIssuer),
            CognitoClientIds = Get(Keys.CognitoClientIds),
            CognitoUserPoolId = Get(Keys.CognitoUserPoolId),
            CognitoAdminIssuer = Get(Keys.CognitoAdminIssuer),
            CognitoAdminClientIds = Get(Keys.CognitoAdminClientIds),
            CognitoAdminUserPoolId = Get(Keys.CognitoAdminUserPoolId),
            AdminGroup = string.IsNullOrEmpty(Get(Keys.AdminGroup)) ? "admin" : Get(Keys.AdminGroup),
            EditorGroup = string.IsNullOrEmpty(Get(Keys.EditorGroup)) ? "editor" : Get(Keys.EditorGroup),
            SesFromAddress = Get(Keys.SesFromAddress),
            SesConfigurationSet = Get(Keys.SesConfigurationSet),
            ContactNotifyEmail = Get(Keys.ContactNotifyEmail),
            AlertSendPerSec = GetInt(Keys.AlertSendPerSec, 10),
            EnrollmentEncryptionKey = Get(Keys.EnrollmentEncryptionKey),
            ReconcileTickMs = GetInt(Keys.ReconcileTickMs, 1000),
            LogLevel = string.IsNullOrEmpty(Get(Keys.LogLevel)) ? "Information" : Get(Keys.LogLevel),
            ForceLeader = GetBool(Keys.ForceLeader, false),
            ObjectStoreDir = string.IsNullOrEmpty(configuration[Keys.ObjectStoreDir]) ? null : configuration[Keys.ObjectStoreDir],
            DevStaticTokens = GetBool(Keys.DevStaticTokens, false),
            SesDryRun = GetBool(Keys.SesDryRun, false),
        };
    }

    private static IReadOnlyList<string> SplitCsv(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();
        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts;
    }
}

public sealed class WmsfoOptionsValidator : IValidateOptions<WmsfoOptions>
{
    public ValidateOptionsResult Validate(string? name, WmsfoOptions o)
    {
        var problems = new List<string>();

        void Require(string key, bool ok)
        {
            if (!ok) problems.Add(key);
        }

        Require(WmsfoOptions.Keys.Env, o.Env is "dev" or "prod");
        Require(WmsfoOptions.Keys.ServiceName, IsSlug(o.ServiceName));
        var requireSsl = o.Env == "prod";
        Require(WmsfoOptions.Keys.DbConnection, IsNpgsqlConnection(o.DbConnection, requireSsl));
        Require(WmsfoOptions.Keys.DbMigrationConnection, IsNpgsqlConnection(o.DbMigrationConnection, requireSsl));
        Require(WmsfoOptions.Keys.AwsRegion, !string.IsNullOrWhiteSpace(o.AwsRegion));
        Require(WmsfoOptions.Keys.S3Bucket, !string.IsNullOrWhiteSpace(o.S3Bucket));
        Require(WmsfoOptions.Keys.CdnBaseUrl, IsHttpsBaseUrl(o.CdnBaseUrl));
        Require(WmsfoOptions.Keys.PublicApiBaseUrl, IsHttpsBaseUrl(o.PublicApiBaseUrl));
        Require(WmsfoOptions.Keys.SiteBaseUrl, IsHttpsBaseUrl(o.SiteBaseUrl));
        Require(WmsfoOptions.Keys.HubUrl, IsAbsoluteUrlWithScheme(o.HubUrl, "wss"));
        Require(WmsfoOptions.Keys.GatewayInternalUrl, IsAbsoluteUrlWithScheme(o.GatewayInternalUrl, "http", "https"));
        Require(WmsfoOptions.Keys.CorsOrigins, HasAbsoluteOrigins(o.CorsOrigins));
        Require(WmsfoOptions.Keys.TrustedProxyHops, o.TrustedProxyHops is >= 0 and <= 5);
        Require(WmsfoOptions.Keys.CognitoIssuer, IsHttpsAbsolute(o.CognitoIssuer));
        Require(WmsfoOptions.Keys.CognitoClientIds, o.CognitoClientIdList().Count > 0);
        Require(WmsfoOptions.Keys.CognitoUserPoolId, !string.IsNullOrWhiteSpace(o.CognitoUserPoolId));
        if (o.HasSeparateAdminPool || o.Env == "prod")
        {
            Require(WmsfoOptions.Keys.CognitoAdminIssuer, IsHttpsAbsolute(o.CognitoAdminIssuer));
            Require(WmsfoOptions.Keys.CognitoAdminClientIds, o.EffectiveAdminClientIdList().Count > 0);
            Require(WmsfoOptions.Keys.CognitoAdminUserPoolId, !string.IsNullOrWhiteSpace(o.CognitoAdminUserPoolId));
        }
        Require(WmsfoOptions.Keys.SesFromAddress, IsMailbox(o.SesFromAddress));
        Require(WmsfoOptions.Keys.ContactNotifyEmail, IsEmail(o.ContactNotifyEmail));
        Require(WmsfoOptions.Keys.AlertSendPerSec, o.AlertSendPerSec is >= 1 and <= 50);
        Require(WmsfoOptions.Keys.EnrollmentEncryptionKey, IsBase64OfLength(o.EnrollmentEncryptionKey, 32));
        Require(WmsfoOptions.Keys.ReconcileTickMs, o.ReconcileTickMs is >= 250 and <= 10000);
        Require(WmsfoOptions.Keys.LogLevel, o.LogLevel is "Debug" or "Information" or "Warning");

        if (o.Env == "prod")
        {
            if (o.ForceLeader) problems.Add(WmsfoOptions.Keys.ForceLeader);
            if (!string.IsNullOrEmpty(o.ObjectStoreDir)) problems.Add(WmsfoOptions.Keys.ObjectStoreDir);
            if (o.DevStaticTokens) problems.Add(WmsfoOptions.Keys.DevStaticTokens);
            if (o.SesDryRun) problems.Add(WmsfoOptions.Keys.SesDryRun);
        }

        if (problems.Count == 0) return ValidateOptionsResult.Success;

        var sb = new StringBuilder();
        sb.Append("Invalid or missing configuration key(s): ");
        sb.AppendJoin(", ", problems);
        return ValidateOptionsResult.Fail(sb.ToString());
    }

    private static bool IsSlug(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        foreach (var c in value)
        {
            var ok = c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c == '-';
            if (!ok) return false;
        }
        return true;
    }

    private static bool IsNpgsqlConnection(string value, bool requireSsl)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(value);
            if (!requireSsl) return !string.IsNullOrWhiteSpace(builder.Host);
            return builder.SslMode == SslMode.Require || builder.SslMode == SslMode.VerifyFull || builder.SslMode == SslMode.VerifyCA;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsHttpsBaseUrl(string value)
    {
        return IsAbsoluteUrlWithScheme(value, "https")
            && !value.EndsWith('/')
            && Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.AbsolutePath == "/" || uri.AbsolutePath.Length == 0);
    }

    private static bool IsHttpsAbsolute(string value)
    {
        return IsAbsoluteUrlWithScheme(value, "https");
    }

    private static bool IsAbsoluteUrlWithScheme(string value, params string[] schemes)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        foreach (var scheme in schemes)
        {
            if (string.Equals(uri.Scheme, scheme, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool HasAbsoluteOrigins(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;
        foreach (var origin in parts)
        {
            if (origin == "*") return false;
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme is not ("http" or "https")) return false;
            if (uri.AbsolutePath != "/" && uri.AbsolutePath.Length != 0) return false;
        }
        return true;
    }

    private static bool IsEmail(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var at = value.IndexOf('@');
        return at > 0 && at < value.Length - 1 && !value.Contains(' ');
    }

    private static bool IsMailbox(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var trimmed = value.Trim();
        if (trimmed.EndsWith('>'))
        {
            var open = trimmed.LastIndexOf('<');
            if (open < 0) return false;
            var address = trimmed[(open + 1)..^1];
            return IsEmail(address);
        }
        return IsEmail(trimmed);
    }

    private static bool IsBase64OfLength(string value, int bytes)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            var buffer = Convert.FromBase64String(value);
            return buffer.Length == bytes;
        }
        catch
        {
            return false;
        }
    }
}
