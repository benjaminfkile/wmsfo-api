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
    public void Formatter_flattens_state_properties_next_to_the_fixed_fields()
    {
        var fields = new WmsfoLoggingFields { Service = "s", Env = "dev", Node = "n" };
        var formatter = new WmsfoJsonConsoleFormatter(Options.Create(fields));
        var writer = new StringWriter();
        // Simulate the state Microsoft.Extensions.Logging builds for a message
        // template `location stored seq={Seq} beaconId={BeaconId}`.
        IReadOnlyList<KeyValuePair<string, object?>> state = new[]
        {
            new KeyValuePair<string, object?>("Seq", 17L),
            new KeyValuePair<string, object?>("BeaconId", 5L),
            new KeyValuePair<string, object?>("Published", true),
            new KeyValuePair<string, object?>("{OriginalFormat}", "location stored seq={Seq} beaconId={BeaconId} published={Published}"),
        };
        var entry = new LogEntry<IReadOnlyList<KeyValuePair<string, object?>>>(
            LogLevel.Information,
            "Wmsfo.Api.Endpoints.LocationIngest",
            new EventId(0, ""),
            state,
            null,
            (s, _) => "location stored seq=17 beaconId=5 published=True");
        formatter.Write(entry, scopeProvider: null, writer);
        using var doc = JsonDocument.Parse(writer.ToString().TrimEnd('\n', '\r'));
        Assert.Equal(17, doc.RootElement.GetProperty("Seq").GetInt64());
        Assert.Equal(5, doc.RootElement.GetProperty("BeaconId").GetInt64());
        Assert.True(doc.RootElement.GetProperty("Published").GetBoolean());
        // The template marker itself is never emitted as a property.
        Assert.False(doc.RootElement.TryGetProperty("{OriginalFormat}", out _));
    }
}
