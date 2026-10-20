using System.Text.Json;
using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Wmsfo.Api.Config;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.Auth;

// contracts 6 `sign_ups_open`, api.md 6.5: the site pool's self sign-up follows
// the setting. The pool is the one WMSFO_COGNITO_ISSUER names (its id is the
// issuer's last path segment). A write describes the pool and replays every
// field into UpdateUserPool with AdminCreateUserConfig.AllowAdminCreateUserOnly
// set to the inverse of the value, because UpdateUserPool resets any field left
// out. The client is the optional IAmazonCognitoIdentityProvider of api.md 6.3;
// without one (WMSFO_DEV_STATIC_TOKENS) the write is `503 unavailable`.
public sealed class SignUpsSwitch
{
    public const string SettingKey = "sign_ups_open";

    private readonly IAmazonCognitoIdentityProvider? _cognito;
    private readonly WmsfoOptions _options;
    private readonly WmsfoConnectionStrings _connections;
    private readonly ILogger<SignUpsSwitch> _logger;

    public SignUpsSwitch(IServiceProvider services, WmsfoOptions options,
        WmsfoConnectionStrings connections, ILogger<SignUpsSwitch> logger)
    {
        _cognito = services.GetService<IAmazonCognitoIdentityProvider>();
        _options = options;
        _connections = connections;
        _logger = logger;
    }

    public bool HasClient => _cognito is not null;

    // The pool id is the issuer URL's last path segment.
    public static string PoolIdFromIssuer(string issuer)
    {
        var path = new Uri(issuer, UriKind.Absolute).AbsolutePath.TrimEnd('/');
        var slash = path.LastIndexOf('/');
        var id = slash < 0 ? path : path[(slash + 1)..];
        if (id.Length == 0)
            throw new InvalidOperationException("the Cognito issuer names no pool");
        return id;
    }

    // Describe, then UpdateUserPool with the full replay. Any failure of either
    // call is `502 upstream_failed`; the caller writes the row only after this
    // returns.
    public async Task ApplyAsync(bool open, CancellationToken ct)
    {
        if (_cognito is null)
        {
            throw new ApiException(StatusCodes.Status503ServiceUnavailable, ApiErrorCodes.Unavailable,
                "sign_ups_open needs the Cognito pool, and this environment has no Cognito client");
        }
        var poolId = PoolIdFromIssuer(_options.CognitoIssuer);
        try
        {
            var described = await _cognito.DescribeUserPoolAsync(
                new DescribeUserPoolRequest { UserPoolId = poolId }, ct).ConfigureAwait(false);
            if (described.UserPool is null)
                throw new InvalidOperationException("DescribeUserPool returned no pool");
            await _cognito.UpdateUserPoolAsync(BuildUpdateRequest(described.UserPool, open), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("sign-ups switch: the pool update failed ({ExceptionType}: {ExceptionMessage})",
                ex.GetType().Name, ex.Message);
            throw new ApiException(StatusCodes.Status502BadGateway, ApiErrorCodes.UpstreamFailed,
                "the sign-up pool could not be updated; the setting keeps its value");
        }
    }

    // Every field of the described pool, unchanged, with AllowAdminCreateUserOnly
    // the inverse of `open`. AdminCreateUserConfig.UnusedAccountValidityDays is
    // the one exception: Cognito refuses it beside
    // Policies.PasswordPolicy.TemporaryPasswordValidityDays, which supersedes it
    // and carries the same value, so it rides only when the policy has none.
    public static UpdateUserPoolRequest BuildUpdateRequest(UserPoolType pool, bool open)
    {
        var admin = pool.AdminCreateUserConfig;
        var temporaryDays = pool.Policies?.PasswordPolicy?.TemporaryPasswordValidityDays;
        return new UpdateUserPoolRequest
        {
            UserPoolId = pool.Id,
            PoolName = pool.Name,
            Policies = pool.Policies,
            DeletionProtection = pool.DeletionProtection,
            LambdaConfig = pool.LambdaConfig,
            AutoVerifiedAttributes = pool.AutoVerifiedAttributes,
            VerificationMessageTemplate = pool.VerificationMessageTemplate,
            SmsVerificationMessage = pool.SmsVerificationMessage,
            EmailVerificationMessage = pool.EmailVerificationMessage,
            EmailVerificationSubject = pool.EmailVerificationSubject,
            SmsAuthenticationMessage = pool.SmsAuthenticationMessage,
            MfaConfiguration = pool.MfaConfiguration,
            DeviceConfiguration = pool.DeviceConfiguration,
            EmailConfiguration = pool.EmailConfiguration,
            SmsConfiguration = pool.SmsConfiguration,
            UserAttributeUpdateSettings = pool.UserAttributeUpdateSettings,
            AccountRecoverySetting = pool.AccountRecoverySetting,
            UserPoolAddOns = pool.UserPoolAddOns,
            UserPoolTags = pool.UserPoolTags,
            UserPoolTier = pool.UserPoolTier,
            IssuerConfiguration = pool.IssuerConfiguration,
            KeyConfiguration = pool.KeyConfiguration,
            AdminCreateUserConfig = new AdminCreateUserConfigType
            {
                AllowAdminCreateUserOnly = !open,
                InviteMessageTemplate = admin?.InviteMessageTemplate,
                UnusedAccountValidityDays = temporaryDays is null ? admin?.UnusedAccountValidityDays : null,
            },
        };
    }

    // api.md 13: once, on the leader's first chore tick after start. Reads the
    // row (a missing row means true) and the pool, and logs at Warning with
    // `wmsfo_signups_switch_drift` when the pool's self sign-up disagrees. It
    // never writes either side. A failure is a Warning without the marker.
    public async Task CheckDriftAsync(CancellationToken ct)
    {
        if (_cognito is null)
        {
            _logger.LogInformation("sign-ups switch: no Cognito client, drift check skipped");
            return;
        }
        try
        {
            var rowOpen = true;
            await using (var conn = new NpgsqlConnection(_connections.App))
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
                await using var cmd = new NpgsqlCommand("select value from app_setting where key = $1;", conn);
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text, Value = SettingKey });
                if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is string raw)
                {
                    using var doc = JsonDocument.Parse(raw);
                    rowOpen = doc.RootElement.ValueKind != JsonValueKind.False;
                }
            }
            var described = await _cognito.DescribeUserPoolAsync(
                new DescribeUserPoolRequest { UserPoolId = PoolIdFromIssuer(_options.CognitoIssuer) }, ct).ConfigureAwait(false);
            var poolOpen = described.UserPool?.AdminCreateUserConfig?.AllowAdminCreateUserOnly != true;
            if (poolOpen != rowOpen)
            {
                _logger.LogWarning(
                    "sign-ups switch: the pool's self sign-up ({PoolOpen}) disagrees with sign_ups_open ({RowOpen}); marker={Marker}",
                    poolOpen, rowOpen, LogMarkers.SignupsSwitchDrift);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("sign-ups switch: drift check failed ({ExceptionType}: {ExceptionMessage})",
                ex.GetType().Name, ex.Message);
        }
    }
}
