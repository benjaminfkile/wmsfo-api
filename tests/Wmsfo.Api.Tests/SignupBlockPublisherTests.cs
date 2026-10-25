using Amazon.Runtime;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Microsoft.Extensions.Logging;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Config;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.Tests;

// A146: the block list publisher (api.md 4 and 13). It reads the secret and
// writes the JSON array of the patterns, lowercased and in the order given,
// only when the string differs; a failure is one Warning with
// `wmsfo_signup_block_publish_failed` and never throws; an empty key or no
// client turns publishing off with one Information line.
public class SignupBlockPublisherTests
{
    private const string SecretId = "signup-blocks-test";

    [Fact]
    public async Task A_different_value_is_written_as_the_lowercased_array_in_order()
    {
        var secrets = new FakeSecretsManagerClient { Value = "[]" };
        var publisher = Publisher(secrets, SecretId, new ListLogger<SecretsManagerSignupBlockPublisher>());

        var wrote = await publisher.PublishAsync(new[] { "Jane@Example.com", "*@spam.test" }, CancellationToken.None);

        Assert.True(wrote);
        Assert.Equal(new[] { "get:" + SecretId, "put:" + SecretId }, secrets.Calls.ToArray());
        Assert.Equal("[\"jane@example.com\",\"*@spam.test\"]", secrets.Value);
    }

    [Fact]
    public async Task An_identical_value_is_not_written()
    {
        var secrets = new FakeSecretsManagerClient { Value = "[\"jane@example.com\"]" };
        var publisher = Publisher(secrets, SecretId, new ListLogger<SecretsManagerSignupBlockPublisher>());

        var wrote = await publisher.PublishAsync(new[] { "jane@example.com" }, CancellationToken.None);

        Assert.False(wrote);
        Assert.Equal(new[] { "get:" + SecretId }, secrets.Calls.ToArray());
    }

    [Fact]
    public async Task An_empty_list_is_the_empty_array()
    {
        var secrets = new FakeSecretsManagerClient { Value = "[\"gone@example.com\"]" };
        var publisher = Publisher(secrets, SecretId, new ListLogger<SecretsManagerSignupBlockPublisher>());

        Assert.True(await publisher.PublishAsync(Array.Empty<string>(), CancellationToken.None));
        Assert.Equal("[]", secrets.Value);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_failed_call_is_swallowed_and_logged_with_the_marker(bool failGet, bool failPut)
    {
        var secrets = new FakeSecretsManagerClient { Value = "[]", FailGet = failGet, FailPut = failPut };
        var logs = new ListLogger<SecretsManagerSignupBlockPublisher>();
        var publisher = Publisher(secrets, SecretId, logs);

        var wrote = await publisher.PublishAsync(new[] { "jane@example.com" }, CancellationToken.None);

        Assert.False(wrote);
        Assert.Equal("[]", secrets.Value);
        var warning = Assert.Single(logs.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal(LogMarkers.SignupBlockPublishFailed, warning.Property("Marker"));
    }

    [Fact]
    public async Task An_empty_key_turns_publishing_off_with_one_information_line()
    {
        var secrets = new FakeSecretsManagerClient { Value = "[]" };
        var logs = new ListLogger<SecretsManagerSignupBlockPublisher>();
        var publisher = Publisher(secrets, "  ", logs);

        Assert.False(await publisher.PublishAsync(new[] { "jane@example.com" }, CancellationToken.None));
        Assert.False(await publisher.PublishAsync(new[] { "john@example.com" }, CancellationToken.None));

        Assert.False(publisher.Enabled);
        Assert.Empty(secrets.Calls);
        var line = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains(WmsfoOptions.Keys.SignupBlockSecretId, line.Message);
    }

    [Fact]
    public async Task No_client_turns_publishing_off_with_one_information_line()
    {
        var logs = new ListLogger<SecretsManagerSignupBlockPublisher>();
        var publisher = Publisher(null, SecretId, logs);

        Assert.False(await publisher.PublishAsync(new[] { "jane@example.com" }, CancellationToken.None));

        var line = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Information, line.Level);
    }

    private static SecretsManagerSignupBlockPublisher Publisher(IAmazonSecretsManager? secrets, string secretId,
        ILogger<SecretsManagerSignupBlockPublisher> logger) =>
        new(secrets, new WmsfoOptions { SignupBlockSecretId = secretId }, logger);
}

// A Secrets Manager client that keeps one secret string in memory and records
// every call; nothing leaves the process.
public sealed class FakeSecretsManagerClient : AmazonSecretsManagerClient
{
    public FakeSecretsManagerClient()
        : base(new AnonymousAWSCredentials(), new AmazonSecretsManagerConfig { ServiceURL = "http://127.0.0.1:1" })
    {
    }

    public string? Value { get; set; }
    public bool FailGet { get; set; }
    public bool FailPut { get; set; }
    public List<string> Calls { get; } = new();

    public override Task<GetSecretValueResponse> GetSecretValueAsync(GetSecretValueRequest request,
        CancellationToken cancellationToken = default)
    {
        Calls.Add("get:" + request.SecretId);
        if (FailGet) throw new AmazonSecretsManagerException("get failed");
        return Task.FromResult(new GetSecretValueResponse { SecretString = Value });
    }

    public override Task<PutSecretValueResponse> PutSecretValueAsync(PutSecretValueRequest request,
        CancellationToken cancellationToken = default)
    {
        Calls.Add("put:" + request.SecretId);
        if (FailPut) throw new AmazonSecretsManagerException("put failed");
        Value = request.SecretString;
        return Task.FromResult(new PutSecretValueResponse());
    }
}

// Keeps each log line's level, message, and structured properties.
public sealed class ListLogger<T> : ILogger<T>
{
    public sealed record Entry(LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> Properties)
    {
        public object? Property(string name) => Properties.FirstOrDefault(p => p.Key == name).Value;
    }

    public List<Entry> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var properties = state as IReadOnlyList<KeyValuePair<string, object?>> ?? Array.Empty<KeyValuePair<string, object?>>();
        Entries.Add(new Entry(logLevel, formatter(state, exception), properties));
    }
}
