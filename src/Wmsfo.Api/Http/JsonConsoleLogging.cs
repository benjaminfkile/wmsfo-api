using System.Collections;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Wmsfo.Api.Config;

namespace Wmsfo.Api.Http;

// api.md 16: JSON console logging with these fields on every line:
//   ts, level, msg, requestId (when in a request), service (WMSFO_SERVICE_NAME),
//   env, node (the gateway instance id once known, else the hostname), plus
//   event-specific properties. The marker constant lands on its own top-level
//   `marker` property so CloudWatch metric filters (platform.md 10) key on it
//   as JSON, not by substring.
public sealed class WmsfoJsonConsoleFormatter : ConsoleFormatter
{
    public const string FormatterName = "wmsfo-json";

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        SkipValidation = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly WmsfoLoggingFields _fields;

    public WmsfoJsonConsoleFormatter(IOptions<WmsfoLoggingFields> fields)
        : base(FormatterName)
    {
        _fields = fields.Value;
    }

    public override void Write<TState>(in LogEntry<TState> entry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = entry.Formatter?.Invoke(entry.State, entry.Exception) ?? "";

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("ts", DateTimeOffset.UtcNow.ToString("O"));
            writer.WriteString("level", LevelString(entry.LogLevel));
            writer.WriteString("msg", message);
            writer.WriteString("service", _fields.Service);
            writer.WriteString("env", _fields.Env);
            writer.WriteString("node", _fields.NodeAccessor?.Invoke() ?? _fields.Node);
            writer.WriteString("category", entry.Category);
            if (entry.EventId.Id != 0 || !string.IsNullOrEmpty(entry.EventId.Name))
            {
                writer.WriteString("event", entry.EventId.Name ?? entry.EventId.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            var seenProps = new HashSet<string>(StringComparer.Ordinal)
            {
                "ts", "level", "msg", "service", "env", "node", "category", "event", "{OriginalFormat}",
            };

            WriteScopes(writer, scopeProvider, seenProps);
            WriteState(writer, entry.State, seenProps);

            if (entry.Exception is not null)
            {
                writer.WritePropertyName("exception");
                writer.WriteStartObject();
                writer.WriteString("type", entry.Exception.GetType().FullName);
                writer.WriteString("message", entry.Exception.Message);
                var stack = entry.Exception.StackTrace;
                if (!string.IsNullOrEmpty(stack)) writer.WriteString("stack", stack);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.Flush();
        }

        buffer.WriteByte((byte)'\n');
        buffer.Position = 0;
        var raw = buffer.ToArray();
        // ConsoleFormatter contract: write the fully-formatted line to textWriter.
        textWriter.Write(System.Text.Encoding.UTF8.GetString(raw));
    }

    private static void WriteScopes(Utf8JsonWriter writer, IExternalScopeProvider? scopeProvider, HashSet<string> seenProps)
    {
        if (scopeProvider is null) return;
        scopeProvider.ForEachScope(static (scope, state) =>
        {
            var (w, seen) = state;
            if (scope is IEnumerable<KeyValuePair<string, object?>> kvps)
            {
                foreach (var kv in kvps)
                {
                    WriteProperty(w, seen, kv.Key, kv.Value);
                }
            }
        }, (writer, seenProps));
    }

    private static void WriteState<TState>(Utf8JsonWriter writer, TState state, HashSet<string> seenProps)
    {
        if (state is IEnumerable<KeyValuePair<string, object?>> kvps)
        {
            foreach (var kv in kvps)
            {
                if (kv.Key == "{OriginalFormat}") continue;
                WriteProperty(writer, seenProps, kv.Key, kv.Value);
            }
        }
    }

    private static void WriteProperty(Utf8JsonWriter writer, HashSet<string> seen, string key, object? value)
    {
        if (!seen.Add(key)) return;
        writer.WritePropertyName(key);
        WriteValue(writer, value);
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case float f:
                writer.WriteNumberValue(f);
                break;
            case decimal m:
                writer.WriteNumberValue(m);
                break;
            case DateTimeOffset dto:
                writer.WriteStringValue(dto.ToString("O"));
                break;
            case DateTime dt:
                writer.WriteStringValue(dt.ToString("O"));
                break;
            case Guid g:
                writer.WriteStringValue(g.ToString());
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    private static string LevelString(LogLevel level) => level switch
    {
        LogLevel.Trace => "trace",
        LogLevel.Debug => "debug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "error",
        LogLevel.Critical => "critical",
        LogLevel.None => "none",
        _ => level.ToString().ToLowerInvariant(),
    };
}

// The three constant fields every log line carries. `NodeAccessor` returns the
// gateway instance id once known, else the hostname (api.md 16).
public sealed class WmsfoLoggingFields
{
    public string Service { get; set; } = "";
    public string Env { get; set; } = "";
    public string Node { get; set; } = "";
    public Func<string?>? NodeAccessor { get; set; }
}

public static class WmsfoLoggingExtensions
{
    // Wires the JSON console formatter and the fields options. Called from
    // Program.cs before Build(); the NodeAccessor is attached after Build so
    // it can read the gateway instance id from IGatewayInternalClient.
    public static void AddWmsfoJsonConsoleLogging(this ILoggingBuilder logging, WmsfoOptions options)
    {
        logging.ClearProviders();
        logging.AddConsole(o => o.FormatterName = WmsfoJsonConsoleFormatter.FormatterName);
        logging.AddConsoleFormatter<WmsfoJsonConsoleFormatter, ConsoleFormatterOptions>();
        logging.Services.Configure<WmsfoLoggingFields>(f =>
        {
            f.Service = options.ServiceName;
            f.Env = options.Env;
            f.Node = Environment.MachineName;
        });
        // Honour WMSFO_LOG_LEVEL for the WMSFO code paths; framework noise stays
        // at Warning per appsettings.
        if (Enum.TryParse<LogLevel>(options.LogLevel, ignoreCase: true, out var level))
        {
            logging.SetMinimumLevel(level);
        }
    }
}
