using System.Xml;

namespace Wmsfo.Api.Media;

// api.md 11.4: XmlReader with DtdProcessing.Prohibit, XmlResolver = null, 1 MB character
// limit. Walk every element and attribute; reject when: the root local name is not `svg`;
// any element local name is `script` or `foreignObject`; any attribute name starts with `on`
// (case-insensitive); any `href` or `xlink:href` value, trimmed, starts with `http:`,
// `https:`, or `javascript:` (case-insensitive). The stored bytes are the uploaded bytes,
// not a re-serialization — callers keep the original input.
public static class SvgValidator
{
    public const int MaxBytes = 1 * 1024 * 1024;

    public static SvgValidationResult Validate(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxBytes)
            return SvgValidationResult.Fail("too_large");
        return Validate(bytes.ToArray());
    }

    public static SvgValidationResult Validate(byte[] bytes)
    {
        if (bytes.Length > MaxBytes)
            return SvgValidationResult.Fail("too_large");

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxBytes,
            MaxCharactersFromEntities = 0,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = false,
            CloseInput = true,
        };

        using var stream = new MemoryStream(bytes, writable: false);
        try
        {
            using var reader = XmlReader.Create(stream, settings);
            var rootSeen = false;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;

                var local = reader.LocalName;
                if (!rootSeen)
                {
                    if (!string.Equals(local, "svg", StringComparison.Ordinal))
                        return SvgValidationResult.Fail("root_not_svg");
                    rootSeen = true;
                }

                if (string.Equals(local, "script", StringComparison.Ordinal))
                    return SvgValidationResult.Fail("element_not_allowed");
                if (string.Equals(local, "foreignObject", StringComparison.Ordinal))
                    return SvgValidationResult.Fail("element_not_allowed");

                if (reader.HasAttributes)
                {
                    while (reader.MoveToNextAttribute())
                    {
                        var attrLocal = reader.LocalName;
                        if (attrLocal.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                            return SvgValidationResult.Fail("event_handler_attribute");

                        if (string.Equals(attrLocal, "href", StringComparison.Ordinal))
                        {
                            var value = (reader.Value ?? string.Empty).Trim();
                            if (value.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
                                value.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ||
                                value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                                return SvgValidationResult.Fail("external_href");
                        }
                    }
                    reader.MoveToElement();
                }
            }

            if (!rootSeen)
                return SvgValidationResult.Fail("root_not_svg");

            return SvgValidationResult.Ok();
        }
        catch (XmlException ex)
        {
            return SvgValidationResult.Fail("malformed:" + ex.Message);
        }
    }
}

public readonly record struct SvgValidationResult(bool IsValid, string? Reason)
{
    public static SvgValidationResult Ok() => new(true, null);
    public static SvgValidationResult Fail(string reason) => new(false, reason);
}
