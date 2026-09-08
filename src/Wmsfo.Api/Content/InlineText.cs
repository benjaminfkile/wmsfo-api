using System.Text;

namespace Wmsfo.Api.Content;

// Reference grammar for the site's inline renderer (contracts 1.3a "Inline markdown").
// Tokens: `**bold**`, `*italic*`, `` `code` ``, `[label](href)`, `{icon:<library-id>}`,
// `{icon:media:<uuid>}`, `{event:name|year|scheduledAt}`, and a newline. Everything
// else is literal text. Unbalanced markers are text, not errors; a malformed `{...}`
// construct or a malformed `[label](href)` is a publish-level problem.
//
// Parse returns InlineParseResult carrying every extracted link and icon (used by
// ReferenceChecker) and every syntax problem found. Callers pass level to control
// whether malformed constructs are reported: at both levels malformed `{icon:...}`
// and `{event:...}` are problems; a stray `[` without a matching `]( ... )` is text.
public static class InlineText
{
    public static readonly IReadOnlyList<string> EventPlaceholders = new[]
    {
        "name", "year", "scheduledAt",
    };

    // A single library or media icon extracted from an inline value.
    public sealed record InlineIcon(string Source, string Id);

    // A `[label](href)` extracted from an inline value. `Label` is the raw label text
    // (still constrained inline; not re-parsed).
    public sealed record InlineLink(string Label, string Href);

    public sealed class InlineParseResult
    {
        public List<InlineLink> Links { get; } = new();
        public List<InlineIcon> Icons { get; } = new();
        public List<string> Problems { get; } = new();
        public bool HasProblems => Problems.Count > 0;
    }

    // Parses `text` and returns the extracted references and problems. `text` is
    // any string; callers validate its length elsewhere (the Inline primitive
    // caps at 5000 characters).
    public static InlineParseResult Parse(string? text)
    {
        var result = new InlineParseResult();
        if (string.IsNullOrEmpty(text)) return result;

        var i = 0;
        var length = text!.Length;
        while (i < length)
        {
            var ch = text[i];
            switch (ch)
            {
                case '\\':
                    // Escape the next character verbatim (does not affect the grammar).
                    i += 2;
                    break;
                case '`':
                    i = SkipCode(text, i);
                    break;
                case '*':
                    // Markers are consumed one at a time; unbalanced markers are text.
                    i++;
                    break;
                case '[':
                    i = TryParseLink(text, i, result);
                    break;
                case '{':
                    i = TryParseBrace(text, i, result);
                    break;
                default:
                    i++;
                    break;
            }
        }

        return result;
    }

    // Skip characters between two backticks. If unclosed, treat the rest as text.
    private static int SkipCode(string text, int start)
    {
        var length = text.Length;
        var j = start + 1;
        while (j < length)
        {
            if (text[j] == '`') return j + 1;
            j++;
        }
        return length;
    }

    // Try to parse `[label](href)` starting at `[`. If the construct is not
    // well-formed the leading `[` is text and parsing continues after it.
    private static int TryParseLink(string text, int start, InlineParseResult into)
    {
        var length = text.Length;
        var j = start + 1;
        var labelStart = j;
        var depth = 1;
        while (j < length)
        {
            var ch = text[j];
            if (ch == '\\' && j + 1 < length) { j += 2; continue; }
            if (ch == '[') depth++;
            else if (ch == ']')
            {
                depth--;
                if (depth == 0) break;
            }
            j++;
        }
        if (j >= length || text[j] != ']')
        {
            // No closing bracket. Treat `[` as text.
            return start + 1;
        }
        var labelEnd = j;
        j++;
        if (j >= length || text[j] != '(')
        {
            // `[label]` with no `(href)` — text.
            return start + 1;
        }
        var hrefStart = j + 1;
        var hrefEnd = -1;
        var k = hrefStart;
        while (k < length)
        {
            var ch = text[k];
            if (ch == '\\' && k + 1 < length) { k += 2; continue; }
            if (ch == ')') { hrefEnd = k; break; }
            k++;
        }
        if (hrefEnd < 0)
        {
            // Unclosed `(`. Treat the whole construct as text.
            return start + 1;
        }
        var label = text.Substring(labelStart, labelEnd - labelStart);
        var href = text.Substring(hrefStart, hrefEnd - hrefStart);
        into.Links.Add(new InlineLink(label, href));
        return hrefEnd + 1;
    }

    // Try to parse `{icon:...}` or `{event:name|year|scheduledAt}`. A malformed
    // brace construct is a publish-level problem; a `{` that doesn't start one is
    // text (only when the leading identifier is not `icon` or `event`).
    private static int TryParseBrace(string text, int start, InlineParseResult into)
    {
        var length = text.Length;
        var end = text.IndexOf('}', start + 1);
        if (end < 0)
        {
            // No closing brace — treat the `{` as text.
            return start + 1;
        }
        var inner = text.Substring(start + 1, end - start - 1);
        var colonIdx = inner.IndexOf(':');
        if (colonIdx <= 0)
        {
            // No `<kind>:...` — treat as text.
            return start + 1;
        }
        var kind = inner.Substring(0, colonIdx);
        var body = inner.Substring(colonIdx + 1);

        switch (kind)
        {
            case "icon":
                if (TryParseIconBody(body, out var iconSource, out var iconId, out var iconError))
                {
                    into.Icons.Add(new InlineIcon(iconSource, iconId));
                }
                else
                {
                    into.Problems.Add($"malformed inline icon: {iconError}");
                }
                return end + 1;
            case "event":
                if (!EventPlaceholders.Contains(body))
                {
                    into.Problems.Add($"unknown event placeholder: {body}");
                }
                return end + 1;
            default:
                // Not one of ours — treat the whole `{...}` as text.
                return end + 1;
        }
    }

    private static bool TryParseIconBody(string body, out string source, out string id, out string error)
    {
        source = "";
        id = "";
        error = "";
        if (body.StartsWith("media:", StringComparison.Ordinal))
        {
            var mediaId = body.Substring("media:".Length);
            if (!IsGuidLike(mediaId))
            {
                error = $"media icon id `{mediaId}` is not a UUID";
                return false;
            }
            source = "media";
            id = mediaId;
            return true;
        }
        if (!IsLibraryIconId(body))
        {
            error = $"library icon id `{body}` does not match ^[a-z0-9]+(-[a-z0-9]+)*$";
            return false;
        }
        source = "library";
        id = body;
        return true;
    }

    private static bool IsGuidLike(string value)
    {
        return Guid.TryParseExact(value, "D", out _);
    }

    private static bool IsLibraryIconId(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 60) return false;
        var sawSegmentChar = false;
        var previousWasHyphen = false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '-')
            {
                if (!sawSegmentChar || previousWasHyphen) return false;
                previousWasHyphen = true;
                sawSegmentChar = false;
                continue;
            }
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
            {
                sawSegmentChar = true;
                previousWasHyphen = false;
                continue;
            }
            return false;
        }
        return sawSegmentChar && !previousWasHyphen;
    }
}
