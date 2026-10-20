using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Amazon.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Config;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.Tests;

// A121: the sign-ups switch replays the described pool into UpdateUserPool
// field for field, with only AllowAdminCreateUserOnly changed; Describe runs
// before Update; a Cognito failure is 502 upstream_failed and no client is
// 503 unavailable.
public class SignUpsSwitchTests
{
    private const string Issuer = "https://cognito-idp.region.example/region_pool";

    [Fact]
    public void The_update_replays_every_field_of_a_fully_described_pool()
    {
        var pool = FullyDescribedPool(temporaryPasswordDays: 7);

        var closed = SignUpsSwitch.BuildUpdateRequest(pool, open: false);

        // Every property of the request is set: a field the replay misses
        // would be reset by UpdateUserPool.
        foreach (var prop in typeof(UpdateUserPoolRequest).GetProperties())
            Assert.True(prop.GetValue(closed) is not null, $"UpdateUserPoolRequest.{prop.Name} is not replayed");

        Assert.Equal(pool.Id, closed.UserPoolId);
        Assert.Equal(pool.Name, closed.PoolName);
        // Every other property carries the described value, by the same name.
        foreach (var prop in typeof(UpdateUserPoolRequest).GetProperties())
        {
            if (prop.Name is nameof(UpdateUserPoolRequest.UserPoolId)
                or nameof(UpdateUserPoolRequest.PoolName)
                or nameof(UpdateUserPoolRequest.AdminCreateUserConfig))
                continue;
            var described = typeof(UserPoolType).GetProperty(prop.Name);
            Assert.True(described is not null, $"UserPoolType has no {prop.Name}");
            var expected = described!.GetValue(pool);
            Assert.NotNull(expected);
            Assert.Same(expected, prop.GetValue(closed));
        }

        Assert.True(closed.AdminCreateUserConfig.AllowAdminCreateUserOnly);
        Assert.Same(pool.AdminCreateUserConfig.InviteMessageTemplate, closed.AdminCreateUserConfig.InviteMessageTemplate);
        // TemporaryPasswordValidityDays in the policy supersedes it, and
        // Cognito refuses both together.
        Assert.Null(closed.AdminCreateUserConfig.UnusedAccountValidityDays);

        var open = SignUpsSwitch.BuildUpdateRequest(pool, open: true);
        Assert.False(open.AdminCreateUserConfig.AllowAdminCreateUserOnly);
    }

    [Fact]
    public void The_unused_account_days_ride_when_the_policy_has_no_temporary_password_days()
    {
        var pool = FullyDescribedPool(temporaryPasswordDays: null);
        var request = SignUpsSwitch.BuildUpdateRequest(pool, open: true);
        Assert.Equal(5, request.AdminCreateUserConfig.UnusedAccountValidityDays);
        Assert.False(request.AdminCreateUserConfig.AllowAdminCreateUserOnly);
    }

    [Fact]
    public void The_pool_id_is_the_issuers_last_path_segment()
    {
        Assert.Equal("region_pool", SignUpsSwitch.PoolIdFromIssuer(Issuer));
        Assert.Equal("region_pool", SignUpsSwitch.PoolIdFromIssuer(Issuer + "/"));
    }

    [Fact]
    public async Task Apply_describes_then_updates_the_issuers_pool()
    {
        var fake = new FakeCognitoClient { Pool = FullyDescribedPool(temporaryPasswordDays: 7) };
        var sw = MakeSwitch(fake);

        await sw.ApplyAsync(open: false, CancellationToken.None);

        Assert.Equal(new[] { "describe:region_pool", "update:region_pool" }, fake.Calls);
        Assert.True(fake.LastUpdate!.AdminCreateUserConfig.AllowAdminCreateUserOnly);
    }

    [Fact]
    public async Task A_describe_failure_is_502_and_nothing_is_updated()
    {
        var fake = new FakeCognitoClient { Pool = FullyDescribedPool(7), FailDescribe = true };
        var ex = await Assert.ThrowsAsync<ApiException>(() => MakeSwitch(fake).ApplyAsync(true, CancellationToken.None));
        Assert.Equal(502, ex.StatusCode);
        Assert.Equal(ApiErrorCodes.UpstreamFailed, ex.Code);
        Assert.Null(fake.LastUpdate);
    }

    [Fact]
    public async Task An_update_failure_is_502()
    {
        var fake = new FakeCognitoClient { Pool = FullyDescribedPool(7), FailUpdate = true };
        var ex = await Assert.ThrowsAsync<ApiException>(() => MakeSwitch(fake).ApplyAsync(true, CancellationToken.None));
        Assert.Equal(502, ex.StatusCode);
        Assert.Equal(ApiErrorCodes.UpstreamFailed, ex.Code);
    }

    [Fact]
    public async Task Without_a_client_the_write_is_503()
    {
        var sw = MakeSwitch(null);
        Assert.False(sw.HasClient);
        var ex = await Assert.ThrowsAsync<ApiException>(() => sw.ApplyAsync(true, CancellationToken.None));
        Assert.Equal(503, ex.StatusCode);
        Assert.Equal(ApiErrorCodes.Unavailable, ex.Code);
        Assert.False(string.IsNullOrEmpty(ex.Message));
    }

    private static SignUpsSwitch MakeSwitch(IAmazonCognitoIdentityProvider? client)
    {
        var services = new ServiceCollection();
        if (client is not null) services.AddSingleton(client);
        return new SignUpsSwitch(services.BuildServiceProvider(),
            new WmsfoOptions { CognitoIssuer = Issuer },
            WmsfoConnectionStrings.ForTests("Host=localhost;Database=x;Username=x;SSL Mode=Disable"),
            NullLogger<SignUpsSwitch>.Instance);
    }

    // A described pool with every field UpdateUserPool takes set.
    internal static UserPoolType FullyDescribedPool(int? temporaryPasswordDays) => new()
    {
        Id = "region_pool",
        Name = "people",
        Policies = new UserPoolPolicyType
        {
            PasswordPolicy = new PasswordPolicyType
            {
                MinimumLength = 12, RequireLowercase = true, RequireUppercase = true,
                RequireNumbers = true, RequireSymbols = false, PasswordHistorySize = 3,
                TemporaryPasswordValidityDays = temporaryPasswordDays,
            },
            SignInPolicy = new SignInPolicyType { AllowedFirstAuthFactors = new List<string> { "PASSWORD" } },
        },
        DeletionProtection = DeletionProtectionType.ACTIVE,
        LambdaConfig = new LambdaConfigType { CustomMessage = "arn:aws:lambda:region:0:function:custom-message" },
        AutoVerifiedAttributes = new List<string> { "email" },
        VerificationMessageTemplate = new VerificationMessageTemplateType
        {
            DefaultEmailOption = DefaultEmailOptionType.CONFIRM_WITH_CODE,
            EmailMessage = "Your code is {####}", EmailSubject = "Your code",
            SmsMessage = "Your code is {####}",
        },
        SmsVerificationMessage = "Your code is {####}",
        EmailVerificationMessage = "Your code is {####}",
        EmailVerificationSubject = "Your code",
        SmsAuthenticationMessage = "Your sign-in code is {####}",
        MfaConfiguration = UserPoolMfaType.OPTIONAL,
        DeviceConfiguration = new DeviceConfigurationType { ChallengeRequiredOnNewDevice = false, DeviceOnlyRememberedOnUserPrompt = true },
        EmailConfiguration = new EmailConfigurationType
        {
            EmailSendingAccount = EmailSendingAccountType.DEVELOPER,
            From = "Example <no-reply@example.com>",
            SourceArn = "arn:aws:ses:region:0:identity/example.com",
            ConfigurationSet = "example-set",
        },
        SmsConfiguration = new SmsConfigurationType { SnsCallerArn = "arn:aws:iam::0:role/sms-role", ExternalId = "external" },
        UserAttributeUpdateSettings = new UserAttributeUpdateSettingsType { AttributesRequireVerificationBeforeUpdate = new List<string> { "email" } },
        AccountRecoverySetting = new AccountRecoverySettingType
        {
            RecoveryMechanisms = new List<RecoveryOptionType> { new() { Name = RecoveryOptionNameType.Verified_email, Priority = 1 } },
        },
        UserPoolAddOns = new UserPoolAddOnsType { AdvancedSecurityMode = AdvancedSecurityModeType.OFF },
        UserPoolTags = new Dictionary<string, string> { ["app"] = "wmsfo" },
        UserPoolTier = UserPoolTierType.ESSENTIALS,
        IssuerConfiguration = new IssuerConfigurationType { Type = IssuerType.ORIGINAL },
        KeyConfiguration = new KeyConfigurationType { KeyType = EncryptionKeyType.AWS_OWNED_KEY },
        AdminCreateUserConfig = new AdminCreateUserConfigType
        {
            AllowAdminCreateUserOnly = false,
            UnusedAccountValidityDays = 5,
            InviteMessageTemplate = new MessageTemplateType { EmailMessage = "{username} {####}", EmailSubject = "Welcome", SMSMessage = "{username} {####}" },
        },
    };
}

// A Cognito client that answers DescribeUserPool and UpdateUserPool from
// memory; every other call is the SDK's and is never made here.
internal sealed class FakeCognitoClient : AmazonCognitoIdentityProviderClient
{
    public FakeCognitoClient()
        : base(new AnonymousAWSCredentials(), new AmazonCognitoIdentityProviderConfig { ServiceURL = "http://127.0.0.1:1" })
    {
    }

    public UserPoolType Pool { get; set; } = new();
    public bool FailDescribe { get; set; }
    public bool FailUpdate { get; set; }
    public List<string> Calls { get; } = new();
    public UpdateUserPoolRequest? LastUpdate { get; private set; }

    public override Task<DescribeUserPoolResponse> DescribeUserPoolAsync(DescribeUserPoolRequest request, CancellationToken cancellationToken = default)
    {
        Calls.Add("describe:" + request.UserPoolId);
        if (FailDescribe) throw new AmazonCognitoIdentityProviderException("describe failed");
        return Task.FromResult(new DescribeUserPoolResponse { UserPool = Pool });
    }

    public override Task<UpdateUserPoolResponse> UpdateUserPoolAsync(UpdateUserPoolRequest request, CancellationToken cancellationToken = default)
    {
        Calls.Add("update:" + request.UserPoolId);
        if (FailUpdate) throw new AmazonCognitoIdentityProviderException("update failed");
        LastUpdate = request;
        return Task.FromResult(new UpdateUserPoolResponse());
    }
}
