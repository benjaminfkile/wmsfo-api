using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Wmsfo.Api.Objects;

// Contracts 1.6 canonical JSON serializer:
// camelCase, no indentation, nulls written, UTF-8 without BOM, no trailing newline,
// timestamps rfc3339 UTC with 3 fractional digits + Z, doubles shortest round-trippable,
// amountDonated decimal converter (contracts 0.2), request bodies disallow unknown fields.
public static class CanonicalJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Encoder = JavaScriptEncoder.Default,
            NumberHandling = JsonNumberHandling.Strict,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        options.Converters.Add(new Rfc3339MillisecondsConverter());
        options.Converters.Add(new NullableRfc3339MillisecondsConverter());
        options.Converters.Add(new AmountDonatedDecimalConverter());
        options.Converters.Add(new NullableAmountDonatedDecimalConverter());
        return options;
    }

    public static byte[] SerializeToUtf8Bytes<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string Sha256Hex(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return Convert.ToHexStringLower(hash);
    }
}

// RFC 3339 UTC timestamp converter. Reads any RFC 3339 offset, writes UTC with .fff and Z.
internal sealed class Rfc3339MillisecondsConverter : JsonConverter<DateTimeOffset>
{
    private const string OutputFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString() ?? throw new JsonException("expected rfc3339 string");
        if (!DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var dto))
            throw new JsonException("invalid rfc3339 timestamp");
        return dto.ToUniversalTime();
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToUniversalTime().ToString(OutputFormat, CultureInfo.InvariantCulture));
    }
}

internal sealed class NullableRfc3339MillisecondsConverter : JsonConverter<DateTimeOffset?>
{
    private const string OutputFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";

    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        var s = reader.GetString() ?? throw new JsonException();
        if (!DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var dto))
            throw new JsonException("invalid rfc3339 timestamp");
        return dto.ToUniversalTime();
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value.Value.ToUniversalTime().ToString(OutputFormat, CultureInfo.InvariantCulture));
    }
}

// amountDonated: read decimal directly from the number token; write it back with two decimals
// as a raw number token (contracts 0.2). Marker type so only [JsonConverter(typeof(...))] fields use it.
[AttributeUsage(AttributeTargets.Property)]
internal sealed class AmountDonatedAttribute : Attribute { }

internal sealed class AmountDonatedDecimalConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetDecimal();

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
    {
        var text = decimal.Round(value, 2).ToString("F2", CultureInfo.InvariantCulture);
        writer.WriteRawValue(text, skipInputValidation: true);
    }
}

internal sealed class NullableAmountDonatedDecimalConverter : JsonConverter<decimal?>
{
    public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        return reader.GetDecimal();
    }

    public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        var text = decimal.Round(value.Value, 2).ToString("F2", CultureInfo.InvariantCulture);
        writer.WriteRawValue(text, skipInputValidation: true);
    }
}
