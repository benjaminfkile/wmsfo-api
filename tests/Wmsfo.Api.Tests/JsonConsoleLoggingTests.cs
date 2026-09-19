using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.Tests;

// A17: JSON console logging with the exact field set of api.md section 16.
// Each emitted line must be a single JSON object with:
//   ts, level, msg, service, env, node, category
// plus structured properties from the message template (including `marker`).
public class JsonConsoleLoggingTests
{
    [Fact]
    public void Formatter_writes_json_with_the_required_fields()
    {
        var fields = new WmsfoLoggingFields
        {
            Service = "wmsfo-api-test",
            Env = "dev",
            Node = "host-a",
        };
        var formatter = new WmsfoJsonConsoleFormatter(Options.Create(fields));
        var writer = new StringWriter();
        var entry = new LogEntry<string>(
            LogLevel.Warning,
            "Wmsfo.Api.Health",
            new EventId(0, ""),
            "health check failed; marker=wmsfo_health_unavailable",
            null,
            (s, _) => s);
        formatter.Write(entry, scopeProvider: null, writer);
        var line = writer.ToString().TrimEnd('\n', '\r');
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("wmsfo-api-test", doc.RootElement.GetProperty("service").GetString());
        Assert.Equal("dev", doc.RootElement.GetProperty("env").GetString());
        Assert.Equal("host-a", doc.RootElement.GetProperty("node").GetString());
        Assert.Equal("warn", doc.RootElement.GetProperty("level").GetString());
        Assert.Equal("Wmsfo.Api.Health", doc.RootElement.GetProperty("category").GetString());
        Assert.Contains("wmsfo_health_unavailable", doc.RootElement.GetProperty("msg").GetString()!);
        Assert.True(doc.RootElement.TryGetProperty("ts", out _));
    }

    [Fact]
    public void Formatter_prefers_the_node_accessor_over_the_static_node_field()
    {
        var fields = new WmsfoLoggingFields
        {
            Service = "wmsfo-api-test",
            Env = "prod",
            Node = "hostname-fallback",
            NodeAccessor = () => "gateway-instance-42",
        };
        var formatter = new WmsfoJsonConsoleFormatter(Options.Create(fields));
        var writer = new StringWriter();
        var entry = new LogEntry<string>(
            LogLevel.Information,
            "Wmsfo",
            new EventId(0, ""),
            "ready",
            null,
            (s, _) => s);
        formatter.Write(entry, scopeProvider: null, writer);
        using var doc = JsonDocument.Parse(writer.ToString().TrimEnd('\n', '\r'));
        Assert.Equal("gateway-instance-42", doc.RootElement.GetProperty("node").GetString());
    }

    [Fact]
    public void Formatter_writes_state_property_names_in_camel_case()
    {
        // api.md 16 / platform.md 10.2: the CloudWatch metric filters key on
        // camel case property names (`$.marker`, `$.outcome`, `$.published`),
        // so every template hole's first character is lowered as it lands.
        // A boolean value stays a JSON boolean; the template marker is dropped.
        var fields = new WmsfoLoggingFields { Service = "s", Env = "dev", Node = "n" };
        var formatter = new WmsfoJsonConsoleFormatter(Options.Create(fields));
        var writer = new StringWriter();
        IReadOnlyList<KeyValuePair<string, object?>> state = new[]
        {
            new KeyValuePair<string, object?>("Marker", "wmsfo_leader_gained"),
            new KeyValuePair<string, object?>("Outcome", "stored"),
            new KeyValuePair<string, object?>("Published", true),
            new KeyValuePair<string, object?>("Seq", 17L),
            new KeyValuePair<string, object?>("BeaconId", 5L),
            new KeyValuePair<string, object?>("{OriginalFormat}", "marker={Marker} outcome={Outcome} published={Published} seq={Seq} beaconId={BeaconId}"),
        };
        var entry = new LogEntry<IReadOnlyList<KeyValuePair<string, object?>>>(
            LogLevel.Information,
            "Wmsfo.Api.Endpoints.LocationIngest",
            new EventId(0, ""),
            state,
            null,
            (s, _) => "marker=wmsfo_leader_gained outcome=stored published=True seq=17 beaconId=5");
        formatter.Write(entry, scopeProvider: null, writer);
        using var doc = JsonDocument.Parse(writer.ToString().TrimEnd('\n', '\r'));
        Assert.Equal("wmsfo_leader_gained", doc.RootElement.GetProperty("marker").GetString());
        Assert.Equal("stored", doc.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.True, doc.RootElement.GetProperty("published").ValueKind);
        Assert.True(doc.RootElement.GetProperty("published").GetBoolean());
        Assert.Equal(17, doc.RootElement.GetProperty("seq").GetInt64());
        Assert.Equal(5, doc.RootElement.GetProperty("beaconId").GetInt64());
        Assert.False(doc.RootElement.TryGetProperty("Marker", out _));
        Assert.False(doc.RootElement.TryGetProperty("Published", out _));
        Assert.False(doc.RootElement.TryGetProperty("{OriginalFormat}", out _));
    }

    [Fact]
    public void Formatter_preserves_already_camel_case_scope_property_names()
    {
        // A `requestId` scope property (added in the request-id middleware) is
        // already camel case and reaches the log line unchanged.
        var fields = new WmsfoLoggingFields { Service = "s", Env = "dev", Node = "n" };
        var formatter = new WmsfoJsonConsoleFormatter(Options.Create(fields));
        var writer = new StringWriter();
        var scopes = new SingleScopeProvider(new Dictionary<string, object?>
        {
            ["requestId"] = "0HN2X7-abc",
        });
        var entry = new LogEntry<string>(
            LogLevel.Information,
            "Wmsfo.Api.Http",
            new EventId(0, ""),
            "hello",
            null,
            (s, _) => s);
        formatter.Write(entry, scopes, writer);
        using var doc = JsonDocument.Parse(writer.ToString().TrimEnd('\n', '\r'));
        Assert.Equal("0HN2X7-abc", doc.RootElement.GetProperty("requestId").GetString());
    }

    [Fact]
    public void Formatter_fixed_field_wins_a_camel_case_collision_with_state()
    {
        // A state property named `Level` would collide with the fixed `level`
        // field once lowered; the formatter keeps the fixed field's value.
        var fields = new WmsfoLoggingFields { Service = "s", Env = "dev", Node = "n" };
        var formatter = new WmsfoJsonConsoleFormatter(Options.Create(fields));
        var writer = new StringWriter();
        IReadOnlyList<KeyValuePair<string, object?>> state = new[]
        {
            new KeyValuePair<string, object?>("Level", "PLEASE_IGNORE"),
            new KeyValuePair<string, object?>("{OriginalFormat}", "clashing state"),
        };
        var entry = new LogEntry<IReadOnlyList<KeyValuePair<string, object?>>>(
            LogLevel.Warning,
            "Wmsfo.Api.Http",
            new EventId(0, ""),
            state,
            null,
            (s, _) => "clashing state");
        formatter.Write(entry, scopeProvider: null, writer);
        using var doc = JsonDocument.Parse(writer.ToString().TrimEnd('\n', '\r'));
        Assert.Equal("warn", doc.RootElement.GetProperty("level").GetString());
    }

    private sealed class SingleScopeProvider : IExternalScopeProvider
    {
        private readonly object _scope;
        public SingleScopeProvider(object scope) { _scope = scope; }
        public void ForEachScope<TState>(Action<object?, TState> callback, TState state) => callback(_scope, state);
        public IDisposable Push(object? state) => NoopDisposable.Instance;
        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();
            public void Dispose() { }
        }
    }
}
