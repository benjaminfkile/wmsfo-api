// The email renderer, with the rules of src/Wmsfo.Api/Email/EmailTemplates.cs:
// a body fragment is placed as-is at {{content}} of the shared layout (one
// trailing newline of the fragment dropped), the status pill and the footer
// link fill {{statusPill}} and {{footerLink}} (an empty footer link drops the
// line of its marker), and every {{token}} is replaced
// by its value, HTML-escaped in HTML bodies and raw in text bodies. A token
// without a value is an error.
import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { join } from "node:path";

export const CONTENT_MARKER = "{{content}}";
export const STATUS_PILL_MARKER = "{{statusPill}}";
export const FOOTER_LINK_MARKER = "{{footerLink}}";
const FOOTER_LINK_LINE = /^[ \t]*\{\{footerLink\}\}\n/gm;
const STATUS_PILL_STYLE =
  "display:inline-block;font-size:12px;font-weight:bold;letter-spacing:0.08em;" +
  "text-transform:uppercase;padding:3px 10px;border-radius:999px;";
// The layout's {{siteName}} when neither `layout` nor `values` carries one.
export const DEFAULT_SITE_NAME = "Santa Tracker";
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

// The header band's pill markup for { label, background, color, tone }, classed
// `pill` and its tone.
export function statusPillHtml({ label, background, color, tone }) {
  return `<span class="pill ${tone}" style="${STATUS_PILL_STYLE}background-color:${background};color:${color};">${htmlEncode(label)}</span>`;
}

// The layout with the fragment at {{content}}, `statusPill` markup at
// {{statusPill}}, and `footerLink` at {{footerLink}}; both default to none.
export function compose(layout, fragment, { statusPill = "", footerLink = "" } = {}) {
  if (!layout.includes(CONTENT_MARKER)) throw new Error(`Email layout missing ${CONTENT_MARKER}.`);
  const body = fragment.endsWith("\n") ? fragment.slice(0, -1) : fragment;
  const frame = (footerLink === ""
    ? layout.replace(FOOTER_LINK_LINE, "")
    : layout.split(FOOTER_LINK_MARKER).join(footerLink)
  ).split(STATUS_PILL_MARKER).join(statusPill);
  return frame.split(CONTENT_MARKER).join(body);
}

// <cdnBaseUrl>/email/<sha256 of the bundled image>.png.
export function imageUrl(templatesDir, cdnBaseUrl, fileName) {
  const sha = createHash("sha256").update(readFileSync(join(templatesDir, fileName))).digest("hex");
  return `${cdnBaseUrl.replace(/\/+$/, "")}/email/${sha}.png`;
}

// The layout's {{logoUrl}}, the URL of logo.png.
export function logoUrl(templatesDir, cdnBaseUrl) {
  return imageUrl(templatesDir, cdnBaseUrl, "logo.png");
}

// The layout's {{ornamentsUrl}} and {{lightsUrl}}, the URLs of ornaments.png
// and lights.png.
export function decorationUrls(templatesDir, cdnBaseUrl) {
  return {
    ornamentsUrl: imageUrl(templatesDir, cdnBaseUrl, "ornaments.png"),
    lightsUrl: imageUrl(templatesDir, cdnBaseUrl, "lights.png"),
  };
}

// Renders one composed body. `spec` carries subject, preheader, and
// footerReason, each substituted raw before it reaches the layout; `layout`
// supplies logoUrl, ornamentsUrl, lightsUrl, siteUrl, and siteName
// (DEFAULT_SITE_NAME when absent), and a siteUrl or siteName in `values` wins.
export function render(composed, spec, values, layout, { htmlEscape }) {
  const subject = substitute(spec.subject, values, { htmlEscape: false });
  const all = {
    siteUrl: layout.siteUrl,
    siteName: layout.siteName ?? DEFAULT_SITE_NAME,
    ...values,
    subject,
    preheader: substitute(spec.preheader, values, { htmlEscape: false }),
    logoUrl: layout.logoUrl,
    ornamentsUrl: layout.ornamentsUrl,
    lightsUrl: layout.lightsUrl,
    footerReason: substitute(spec.footerReason, values, { htmlEscape: false }),
  };
  return { subject, body: substitute(composed, all, { htmlEscape }) };
}
