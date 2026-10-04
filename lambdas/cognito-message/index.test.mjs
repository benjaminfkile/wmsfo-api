import assert from "node:assert/strict";
import { cpSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { COGNITO_CODE, COGNITO_USERNAME, MAX_MESSAGE_LENGTH, createHandler } from "./index.mjs";
import { compose, decorationUrls, logoUrl, render, statusPillHtml } from "./render.mjs";

const templatesDir = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "templates", "email");
const settings = {
  templatesDir,
  cdnBaseUrl: "https://cdn.example.com",
  siteBaseUrl: "https://site.example.com",
  panelBaseUrl: "https://admin.example.com",
};

function event(triggerSource) {
  return {
    version: "1",
    region: "us-east-1",
    userPoolId: "pool",
    userName: "someone",
    triggerSource,
    request: {
      userAttributes: { email: "person@example.com" },
      codeParameter: COGNITO_CODE,
      usernameParameter: COGNITO_USERNAME,
    },
    response: { smsMessage: null, emailMessage: null, emailSubject: null },
  };
}

// Runs fn with console.log captured; returns [result, lines].
async function captureLog(fn) {
  const original = console.log;
  const lines = [];
  console.log = (line) => lines.push(line);
  try {
    return [await fn(), lines];
  } finally {
    console.log = original;
  }
}

const CASES = [
  ["CustomMessage_SignUp", "account_signup_code", "Your Santa Tracker code"],
  ["CustomMessage_ResendCode", "account_signup_code", "Your Santa Tracker code"],
  ["CustomMessage_ForgotPassword", "account_reset_code", "Reset your Santa Tracker password"],
  ["CustomMessage_UpdateUserAttribute", "account_email_code", "Confirm your email address"],
  ["CustomMessage_VerifyUserAttribute", "account_email_code", "Confirm your email address"],
  ["CustomMessage_AdminCreateUser", "account_admin_invite", "Your Santa Tracker admin account"],
];

for (const [triggerSource, template, subject] of CASES) {
  test(`${triggerSource} renders ${template}`, async () => {
    const handler = createHandler(settings);
    const [result, lines] = await captureLog(() => handler(event(triggerSource)));
    assert.equal(result.response.emailSubject, subject);
    const body = result.response.emailMessage;
    assert.ok(body.includes(COGNITO_CODE), "the code placeholder is in the body unescaped");
    if (template === "account_admin_invite") {
      assert.ok(body.includes(COGNITO_USERNAME), "the username placeholder is in the body unescaped");
      assert.ok(body.includes('href="https://admin.example.com"'));
      assert.ok(body.includes(">Sign in</a>"));
    }
    assert.ok(body.startsWith("<!DOCTYPE html>"));
    assert.ok(body.includes(`<title>${subject}</title>`));
    assert.ok(body.includes(logoUrl(templatesDir, settings.cdnBaseUrl)));
    const { ornamentsUrl, lightsUrl } = decorationUrls(templatesDir, settings.cdnBaseUrl);
    assert.ok(body.includes(`<img src="${ornamentsUrl}" width="560" height="110" alt=""`));
    assert.ok(body.includes(`<img src="${lightsUrl}" width="560" height="4" alt=""`));
    assert.ok(!body.includes("{{"), "no token is left");
    assert.ok(body.length < MAX_MESSAGE_LENGTH);

    assert.equal(lines.length, 1);
    const line = JSON.parse(lines[0]);
    assert.equal(line.triggerSource, triggerSource);
    assert.equal(line.template, template);
    assert.ok(!lines[0].includes("person@example.com"));
    assert.ok(!lines[0].includes(COGNITO_CODE));
  });
}

test("the fragment text of each template is in its body", async () => {
  const handler = createHandler(settings);
  const expected = {
    CustomMessage_SignUp: [
      "Here is your code to finish signing up for the Santa Tracker.",
      "It expires in 24 hours. If you did not sign up, you can ignore this email.",
    ],
    CustomMessage_ForgotPassword: [
      "Use this code to reset your password.",
      "It expires in 1 hour. If you did not ask for this, you can ignore this email; your password has not changed.",
    ],
    CustomMessage_UpdateUserAttribute: [
      "Use this code to confirm this email address for your Santa Tracker account.",
      "It expires in 24 hours.",
    ],
    CustomMessage_AdminCreateUser: [
      "An account was created for you on the Santa Tracker admin panel.",
      "The temporary password expires in 7 days. You will choose your own password and set up an authenticator app when you first sign in.",
    ],
  };
  for (const [triggerSource, texts] of Object.entries(expected)) {
    const [result] = await captureLog(() => handler(event(triggerSource)));
    for (const text of texts) assert.ok(result.response.emailMessage.includes(text), `${triggerSource}: ${text}`);
  }
});

test("a code other than the placeholder is rendered unescaped when it has no markup", async () => {
  const handler = createHandler(settings);
  const input = event("CustomMessage_SignUp");
  input.request.codeParameter = "{##code##}";
  const [result] = await captureLog(() => handler(input));
  assert.ok(result.response.emailMessage.includes("{##code##}"));
});

test("an unknown trigger returns the event untouched", async () => {
  const handler = createHandler(settings);
  const input = event("CustomMessage_Authentication");
  const before = structuredClone(input);
  const [result, lines] = await captureLog(() => handler(input));
  assert.equal(result, input);
  assert.deepEqual(result, before);
  assert.equal(lines.length, 1);
});

test("a forced render error returns the event untouched without throwing", async () => {
  const dir = mkdtempSync(join(tmpdir(), "cognito-message-"));
  try {
    cpSync(templatesDir, dir, { recursive: true });
    writeFileSync(join(dir, "account_signup_code.html"), "<p>{{unknownToken}} {{code}}</p>\n");
    const handler = createHandler({ ...settings, templatesDir: dir });
    const input = event("CustomMessage_SignUp");
    const before = structuredClone(input);
    const [result, lines] = await captureLog(() => handler(input));
    assert.deepEqual(result, before);
    assert.equal(lines.length, 1);
    assert.match(JSON.parse(lines[0]).error, /unknownToken/);

    // The other templates still render.
    const [other] = await captureLog(() => handler(event("CustomMessage_ForgotPassword")));
    assert.equal(other.response.emailSubject, "Reset your Santa Tracker password");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("a missing templates directory returns the event untouched", async () => {
  const handler = createHandler({ ...settings, templatesDir: join(tmpdir(), "no-such-templates-dir") });
  const input = event("CustomMessage_SignUp");
  const before = structuredClone(input);
  const [result] = await captureLog(() => handler(input));
  assert.deepEqual(result, before);
});

test("a render without codeParameter returns the event untouched", async () => {
  const handler = createHandler(settings);
  const input = event("CustomMessage_ForgotPassword");
  delete input.request.codeParameter;
  const before = structuredClone(input);
  const [result] = await captureLog(() => handler(input));
  assert.deepEqual(result, before);
});

test("a body over the Cognito limit returns the event untouched", async () => {
  const handler = createHandler(settings);
  const input = event("CustomMessage_SignUp");
  input.request.codeParameter = "#".repeat(MAX_MESSAGE_LENGTH);
  const before = structuredClone(input);
  const [result, lines] = await captureLog(() => handler(input));
  assert.deepEqual(result, before);
  assert.match(JSON.parse(lines[0]).error, /over 20000/);
  assert.ok(!lines[0].includes(input.request.codeParameter));
});

test("the A56 golden files match this renderer byte for byte", () => {
  const goldenDir = join(templatesDir, "_golden");
  const inputs = JSON.parse(readFileSync(join(goldenDir, "inputs.json"), "utf8"));
  const layout = {
    logoUrl: logoUrl(templatesDir, inputs.cdnBaseUrl),
    ...decorationUrls(templatesDir, inputs.cdnBaseUrl),
    siteUrl: inputs.siteUrl,
  };
  assert.equal(layout.logoUrl, inputs.logoUrl);
  assert.equal(layout.ornamentsUrl, inputs.ornamentsUrl);
  assert.equal(layout.lightsUrl, inputs.lightsUrl);
  for (const [name, spec] of Object.entries(inputs.renders)) {
    for (const ext of ["html", "txt"]) {
      const composed = compose(
        readFileSync(join(templatesDir, `_layout.${ext}`), "utf8"),
        readFileSync(join(templatesDir, `${name}.${ext}`), "utf8"),
        {
          statusPill: spec.statusPill && ext === "html" ? statusPillHtml(spec.statusPill) : "",
          footerLink: spec.footerLink?.[ext] ?? "",
        },
      );
      const { subject, body } = render(composed, spec, spec.values, layout, { htmlEscape: ext === "html" });
      assert.equal(subject, spec.subject);
      assert.deepEqual(Buffer.from(body, "utf8"), readFileSync(join(goldenDir, `${name}.${ext}`)), `${name}.${ext}`);
    }
  }
});
