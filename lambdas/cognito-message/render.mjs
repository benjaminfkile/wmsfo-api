// The email renderer, with the rules of src/Wmsfo.Api/Email/EmailTemplates.cs:
// a body fragment is placed as-is at {{content}} of the shared layout (one
// trailing newline of the fragment dropped), and every {{token}} is replaced
// by its value, HTML-escaped in HTML bodies and raw in text bodies. A token
// without a value is an error.
import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { join } from "node:path";

export const CONTENT_MARKER = "{{content}}";
const TOKEN_PATTERN = /\{\{([a-zA-Z]+)\}\}/g;

// HTML-escapes the way .NET's WebUtility.HtmlEncode does: the five markup
// characters, U+00A0 through U+00FF, and characters outside the Basic
// Multilingual Plane as numeric references; a lone surrogate becomes U+FFFD.
export function htmlEncode(value) {
  let out = "";
  for (let i = 0; i < value.length; i++) {
    const ch = value[i];
    const code = value.charCodeAt(i);
    if (ch === "<") out += "&lt;";
    else if (ch === ">") out += "&gt;";
    else if (ch === "&") out += "&amp;";
    else if (ch === '"') out += "&quot;";
    else if (ch === "'") out += "&#39;";
    else if (code >= 0xa0 && code < 0x100) out += `&#${code};`;
    else if (code >= 0xd800 && code <= 0xdfff) {
      const next = value.charCodeAt(i + 1);
      if (code <= 0xdbff && next >= 0xdc00 && next <= 0xdfff) {
        out += `&#${(code - 0xd800) * 0x400 + (next - 0xdc00) + 0x10000};`;
        i++;
      } else {
        out += "&#65533;";
      }
    } else out += ch;
  }
  return out;
}

// Replaces every {{token}} of `body` with values[token]; throws on a token
// that has no value.
export function substitute(body, values, { htmlEscape }) {
  return body.replace(TOKEN_PATTERN, (_, key) => {
    if (!Object.hasOwn(values, key)) throw new Error(`Missing substitution value: ${key}`);
    const value = String(values[key]);
    return htmlEscape ? htmlEncode(value) : value;
  });
}

// The layout with the fragment at {{content}}.
export function compose(layout, fragment) {
  if (!layout.includes(CONTENT_MARKER)) throw new Error(`Email layout missing ${CONTENT_MARKER}.`);
  const body = fragment.endsWith("\n") ? fragment.slice(0, -1) : fragment;
  return layout.split(CONTENT_MARKER).join(body);
}

// <cdnBaseUrl>/email/<sha256 of logo.png>.png, the layout's {{logoUrl}}.
export function logoUrl(templatesDir, cdnBaseUrl) {
  const sha = createHash("sha256").update(readFileSync(join(templatesDir, "logo.png"))).digest("hex");
  return `${cdnBaseUrl.replace(/\/+$/, "")}/email/${sha}.png`;
}

// Renders one composed body. `spec` carries subject, preheader, and
// footerReason, each substituted raw before it reaches the layout; `layout`
// supplies logoUrl and siteUrl, and a siteUrl in `values` wins.
export function render(composed, spec, values, layout, { htmlEscape }) {
  const subject = substitute(spec.subject, values, { htmlEscape: false });
  const all = {
    siteUrl: layout.siteUrl,
    ...values,
    subject,
    preheader: substitute(spec.preheader, values, { htmlEscape: false }),
    logoUrl: layout.logoUrl,
    footerReason: substitute(spec.footerReason, values, { htmlEscape: false }),
  };
  return { subject, body: substitute(composed, all, { htmlEscape }) };
}
