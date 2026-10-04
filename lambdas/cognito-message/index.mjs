// Cognito Custom Message trigger: the account emails of the people and admin
// pools in the shared branded layout of templates/email/. Templates are read,
// composed, and rendered with Cognito's placeholders at module load. The
// handler never throws: on any failure it logs one line and returns the event
// unchanged, so Cognito sends its default message.
import { existsSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { compose, decorationUrls, logoUrl, render } from "./render.mjs";

// Cognito rejects a custom message longer than this.
export const MAX_MESSAGE_LENGTH = 20000;

// The placeholders Cognito puts in codeParameter and usernameParameter.
export const COGNITO_CODE = "{####}";
export const COGNITO_USERNAME = "{username}";

const ACCOUNT_FOOTER = "You are receiving this because this address was used for a Santa Tracker account.";

export const SPECS = {
  account_signup_code: {
    subject: "Your Santa Tracker code",
    preheader: "Your code to finish signing up for the Santa Tracker.",
    footerReason: ACCOUNT_FOOTER,
  },
  account_reset_code: {
    subject: "Reset your Santa Tracker password",
    preheader: "Your code to reset your Santa Tracker password.",
    footerReason: ACCOUNT_FOOTER,
  },
  account_email_code: {
    subject: "Confirm your email address",
    preheader: "Your code to confirm this email address for your Santa Tracker account.",
    footerReason: ACCOUNT_FOOTER,
  },
  account_admin_invite: {
    subject: "Your Santa Tracker admin account",
    preheader: "Your username and temporary password for the Santa Tracker admin panel.",
    footerReason: "You are receiving this because an account was created for this address on the Santa Tracker admin panel.",
    needsUsername: true,
  },
};

export const TEMPLATE_FOR_TRIGGER = {
  CustomMessage_SignUp: "account_signup_code",
  CustomMessage_ResendCode: "account_signup_code",
  CustomMessage_ForgotPassword: "account_reset_code",
  CustomMessage_UpdateUserAttribute: "account_email_code",
  CustomMessage_VerifyUserAttribute: "account_email_code",
  CustomMessage_AdminCreateUser: "account_admin_invite",
};

// templates/email/ beside this file in the deployed zip, or at the
// repository root when run from lambdas/cognito-message/.
function defaultTemplatesDir() {
  const here = dirname(fileURLToPath(import.meta.url));
  const bundled = join(here, "templates", "email");
  return existsSync(bundled) ? bundled : join(here, "..", "..", "templates", "email");
}

function log(line) {
  console.log(JSON.stringify(line));
}

// Loads every template and returns the Cognito handler. A template that fails
// to load or to render is kept as its error, which each call using it logs.
export function createHandler({
  templatesDir = defaultTemplatesDir(),
  cdnBaseUrl = process.env.CDN_BASE_URL ?? "",
  siteBaseUrl = process.env.SITE_BASE_URL ?? "",
  panelBaseUrl = process.env.PANEL_BASE_URL ?? "",
} = {}) {
  const templates = new Map();
  let layout;
  try {
    layout = {
      html: readFileSync(join(templatesDir, "_layout.html"), "utf8"),
      logoUrl: logoUrl(templatesDir, cdnBaseUrl),
      ...decorationUrls(templatesDir, cdnBaseUrl),
      siteUrl: siteBaseUrl,
    };
  } catch (err) {
    layout = { error: err };
  }

  const renderOne = (tpl, spec, code, username) => {
    const values = { code, panelUrl: panelBaseUrl };
    if (spec.needsUsername) values.username = username;
    const { subject, body } = render(tpl.composed, spec, values, layout, { htmlEscape: true });
    if (!body.includes(code)) throw new Error("rendered body is missing the code");
    if (spec.needsUsername && !body.includes(username)) throw new Error("rendered body is missing the username");
    if (body.length > MAX_MESSAGE_LENGTH) throw new Error(`rendered body is ${body.length} characters, over ${MAX_MESSAGE_LENGTH}`);
    return { subject, body };
  };

  for (const [name, spec] of Object.entries(SPECS)) {
    if (layout.error) {
      templates.set(name, { error: layout.error });
      continue;
    }
    try {
      const tpl = { composed: compose(layout.html, readFileSync(join(templatesDir, `${name}.html`), "utf8")) };
      tpl.rendered = renderOne(tpl, spec, COGNITO_CODE, COGNITO_USERNAME);
      templates.set(name, tpl);
    } catch (err) {
      templates.set(name, { error: err });
    }
  }

  return async function handler(event) {
    const triggerSource = event?.triggerSource;
    const template = Object.hasOwn(TEMPLATE_FOR_TRIGGER, triggerSource ?? "") ? TEMPLATE_FOR_TRIGGER[triggerSource] : null;
    if (!template) {
      log({ msg: "cognito_message", triggerSource: triggerSource ?? null, template: null, outcome: "skipped" });
      return event;
    }
    try {
      const tpl = templates.get(template);
      if (tpl.error) throw tpl.error;
      const spec = SPECS[template];
      const code = event.request?.codeParameter;
      if (typeof code !== "string" || code === "") throw new Error("codeParameter is missing");
      const username = event.request?.usernameParameter;
      if (spec.needsUsername && (typeof username !== "string" || username === "")) {
        throw new Error("usernameParameter is missing");
      }
      const { subject, body } =
        code === COGNITO_CODE && (!spec.needsUsername || username === COGNITO_USERNAME)
          ? tpl.rendered
          : renderOne(tpl, spec, code, username);
      event.response = { ...event.response, emailSubject: subject, emailMessage: body };
      log({ msg: "cognito_message", triggerSource, template, outcome: "rendered" });
      return event;
    } catch (err) {
      log({ msg: "cognito_message", triggerSource, template, outcome: "default", error: String(err?.message ?? err) });
      return event;
    }
  };
}

export const handler = createHandler();
