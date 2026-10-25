import assert from "node:assert/strict";
import { test } from "node:test";
import { BLOCKED, REFUSAL, UNAVAILABLE, buildDocument, createHandler, matches, parseList } from "./index.mjs";

// Placeholder ARNs under the documentation account, built so no line holds a full ARN.
const ACCOUNT = "123456789012";
const SES = `arn:aws:ses:us-west-2:${ACCOUNT}`;
const IDENTITY = `${SES}:identity/example.com`;
const PROD_SET = `${SES}:configuration-set/example-prod`;
const DEV_SET = `${SES}:configuration-set/example-dev`;

const ENV = {
  BLOCKLIST_SECRET_ID: "example-blocklist",
  ALLOWLIST_SECRET_ID: "example-allowlist",
  ALLOWLIST_TTL_SECONDS: "0",
  SES_ROLE_NAME: "example-instance-role",
  SES_POLICY_NAME: "example-policy",
  SES_SEND_RESOURCES_PROD: `${IDENTITY}, ${PROD_SET}`,
  SES_SEND_RESOURCE_DEV: DEV_SET,
};

const LIST = ["Owner@Example.com", "*@example.org", "tester+*@example.net"];
const BLOCKS = ["Spammer@Example.org", "*@example.biz"];

// Prod's environment: the block list and no invitation list or SES variables.
const PROD_ENV = { BLOCKLIST_SECRET_ID: ENV.BLOCKLIST_SECRET_ID, ALLOWLIST_TTL_SECONDS: "0" };

// A secrets client that answers with the given lists (or throws) and counts
// reads: `reads` of the invitation list, `blockReads` of the block list.
function fakeSecrets(list = LIST, blocks = BLOCKS) {
  const client = {
    reads: 0,
    blockReads: 0,
    secretString: JSON.stringify(list),
    blockString: JSON.stringify(blocks),
    fail: false,
    blockFail: false,
    async getSecretValue(input) {
      if (input.SecretId === ENV.BLOCKLIST_SECRET_ID) {
        client.blockReads++;
        if (client.blockFail) throw new Error("AccessDeniedException");
        return { SecretString: client.blockString };
      }
      client.reads++;
      assert.equal(input.SecretId, ENV.ALLOWLIST_SECRET_ID);
      if (client.fail) throw new Error("AccessDeniedException");
      return { SecretString: client.secretString };
    },
  };
  return client;
}

// An IAM client holding one inline policy, URL-encoded as IAM returns it.
function fakeIam(document) {
  const client = {
    document: encodeURIComponent(JSON.stringify(document, null, 2)),
    puts: [],
    async getRolePolicy(input) {
      assert.deepEqual(input, { RoleName: ENV.SES_ROLE_NAME, PolicyName: ENV.SES_POLICY_NAME });
      return { RoleName: input.RoleName, PolicyName: input.PolicyName, PolicyDocument: client.document };
    },
    async putRolePolicy(input) {
      client.puts.push(input);
      client.document = encodeURIComponent(input.PolicyDocument);
      return {};
    },
  };
  return client;
}

function signUp(email, triggerSource = "PreSignUp_SignUp") {
  return {
    version: "1",
    triggerSource,
    userName: "someone",
    request: { userAttributes: email === undefined ? {} : { email } },
    response: { autoConfirmUser: false, autoVerifyEmail: false, autoVerifyPhone: false },
  };
}

// Runs fn with console.log captured; returns [result or error, lines].
async function captureLog(fn) {
  const original = console.log;
  const lines = [];
  console.log = (line) => lines.push(line);
  try {
    return [await fn(), lines];
  } catch (err) {
    return [err, lines];
  } finally {
    console.log = original;
  }
}

const OTHER_BEFORE = { Effect: "Allow", Action: "ses:GetAccount", Resource: "*" };
const OTHER_AFTER = {
  Effect: "Allow",
  Action: ["cognito-idp:DescribeUserPool", "cognito-idp:UpdateUserPool"],
  Resource: [`arn:aws:cognito-idp:us-west-2:${ACCOUNT}:userpool/example`],
};
const LEGACY = { Effect: "Allow", Action: ["ses:SendEmail", "ses:SendRawEmail"], Resource: [IDENTITY, DEV_SET, PROD_SET] };
const legacyDocument = () => ({ Version: "2012-10-17", Statement: [OTHER_BEFORE, LEGACY, OTHER_AFTER] });

// matches

test("matches an exact address", () => {
  assert.ok(matches("owner@example.com", ["owner@example.com"]));
});

test("matches *@domain", () => {
  assert.ok(matches("anyone@example.org", ["*@example.org"]));
  assert.ok(matches("a.b+c@example.org", ["*@example.org"]));
});

test("matches name+*@domain", () => {
  assert.ok(matches("tester+one@example.net", ["tester+*@example.net"]));
  assert.ok(matches("tester+@example.net", ["tester+*@example.net"]));
});

test("matches folds case on both sides", () => {
  assert.ok(matches("OWNER@example.COM", ["Owner@Example.com"]));
  assert.ok(matches("Anyone@EXAMPLE.org", ["*@Example.Org"]));
});

test("matches trims surrounding whitespace on both sides", () => {
  assert.ok(matches("  owner@example.com\n", [" owner@example.com\t"]));
});

test("matches refuses addresses one character away", () => {
  assert.ok(!matches("owner@example.co", ["owner@example.com"]));
  assert.ok(!matches("owners@example.com", ["owner@example.com"]));
  assert.ok(!matches("owner@exampleXcom", ["owner@example.com"]), "the dot is literal");
  assert.ok(!matches("anyone@example.orgs", ["*@example.org"]));
  assert.ok(!matches("anyone@xexample.org", ["*@example.org"]));
  assert.ok(!matches("testerX+one@example.net", ["tester+*@example.net"]));
  assert.ok(!matches("testerone@example.net", ["tester+*@example.net"]), "the plus is literal");
  assert.ok(!matches("", ["*"]));
  assert.ok(!matches("owner@example.com", []));
});

// parseList

test("parseList accepts an array of strings, an empty array included", () => {
  assert.deepEqual(parseList(JSON.stringify(LIST)), LIST);
  assert.deepEqual(parseList("[]"), []);
});

test("parseList refuses an object, a non-string entry, an empty string, and bad JSON", () => {
  assert.throws(() => parseList(JSON.stringify({ entries: LIST })));
  assert.throws(() => parseList(JSON.stringify(["owner@example.com", 7])));
  assert.throws(() => parseList(JSON.stringify(["owner@example.com", ""])));
  assert.throws(() => parseList(JSON.stringify(["  "])));
  assert.throws(() => parseList("not json"));
});

// Pre sign-up

test("SignUp on the list returns the same event with no auto flags", async () => {
  const handler = createHandler({ secrets: fakeSecrets(), env: ENV });
  const input = signUp("owner@example.com");
  const before = structuredClone(input);
  const [result, lines] = await captureLog(() => handler(input));
  assert.equal(result, input);
  assert.deepEqual(result, before);
  assert.equal(result.response.autoConfirmUser, false);
  assert.equal(result.response.autoVerifyEmail, false);
  assert.equal(lines.length, 1);
  assert.deepEqual(JSON.parse(lines[0]), { msg: "signup_gate", triggerSource: "PreSignUp_SignUp", allowed: true });
  assert.ok(!lines[0].includes("example.com"));
});

test("SignUp outside the list throws REFUSAL and logs no address", async () => {
  const handler = createHandler({ secrets: fakeSecrets(), env: ENV });
  const [err, lines] = await captureLog(() => handler(signUp("stranger@example.com")));
  assert.ok(err instanceof Error);
  assert.equal(err.message, REFUSAL);
  assert.equal(lines.length, 1);
  assert.deepEqual(JSON.parse(lines[0]), { msg: "signup_gate", triggerSource: "PreSignUp_SignUp", allowed: false, refusedBy: "allowlist" });
  assert.ok(!lines[0].includes("stranger"));
});

test("ExternalProvider behaves as SignUp", async () => {
  const handler = createHandler({ secrets: fakeSecrets(), env: ENV });
  const input = signUp("someone@example.org", "PreSignUp_ExternalProvider");
  const [result] = await captureLog(() => handler(input));
  assert.equal(result, input);
  const [err, lines] = await captureLog(() => handler(signUp("someone@example.com", "PreSignUp_ExternalProvider")));
  assert.equal(err.message, REFUSAL);
  assert.equal(JSON.parse(lines[0]).triggerSource, "PreSignUp_ExternalProvider");
});

test("AdminCreateUser passes without reading the secret", async () => {
  const secrets = fakeSecrets();
  const handler = createHandler({ secrets, env: ENV });
  const input = signUp("stranger@example.com", "PreSignUp_AdminCreateUser");
  const before = structuredClone(input);
  const [result, lines] = await captureLog(() => handler(input));
  assert.equal(result, input);
  assert.deepEqual(result, before);
  assert.equal(secrets.reads, 0);
  assert.equal(secrets.blockReads, 0);
  assert.equal(JSON.parse(lines[0]).allowed, true);
});

test("a failing secret read throws UNAVAILABLE", async () => {
  const secrets = fakeSecrets();
  secrets.fail = true;
  const handler = createHandler({ secrets, env: ENV });
  const [err, lines] = await captureLog(() => handler(signUp("owner@example.com")));
  assert.equal(err.message, UNAVAILABLE);
  assert.equal(JSON.parse(lines[0]).allowed, false);
  assert.ok(!lines[0].includes("owner"));
});

test("an unparsable list or a missing email throws UNAVAILABLE", async () => {
  const secrets = fakeSecrets();
  secrets.secretString = JSON.stringify({ entries: LIST });
  const [err] = await captureLog(() => createHandler({ secrets, env: ENV })(signUp("owner@example.com")));
  assert.equal(err.message, UNAVAILABLE);
  const [err2] = await captureLog(() => createHandler({ secrets: fakeSecrets(), env: ENV })(signUp(undefined)));
  assert.equal(err2.message, UNAVAILABLE);
});

test("the cache answers a second call without a second read and re-reads after the TTL", async () => {
  const secrets = fakeSecrets();
  let clock = 1_000_000;
  const handler = createHandler({ secrets, env: { ...ENV, ALLOWLIST_TTL_SECONDS: undefined }, now: () => clock });
  await captureLog(() => handler(signUp("owner@example.com")));
  clock += 59_000;
  await captureLog(() => handler(signUp("owner@example.com")));
  assert.equal(secrets.reads, 1);
  clock += 1_000;
  await captureLog(() => handler(signUp("owner@example.com")));
  assert.equal(secrets.reads, 2);
});

test("a TTL of 0 reads the secret on every call", async () => {
  const secrets = fakeSecrets();
  const handler = createHandler({ secrets, env: ENV });
  await captureLog(() => handler(signUp("owner@example.com")));
  await captureLog(() => handler(signUp("owner@example.com")));
  assert.equal(secrets.reads, 2);
});

// The block list

test("a blocked address is refused with BLOCKED before the allow list is consulted", async () => {
  const secrets = fakeSecrets();
  const handler = createHandler({ secrets, env: ENV });
  // On the allow list through *@example.org, and blocked by address.
  const [err, lines] = await captureLog(() => handler(signUp("spammer@EXAMPLE.org")));
  assert.ok(err instanceof Error);
  assert.equal(err.message, BLOCKED);
  assert.equal(err.message, "This address cannot sign up here.");
  assert.equal(secrets.blockReads, 1);
  assert.equal(secrets.reads, 0);
  assert.equal(lines.length, 1);
  assert.deepEqual(JSON.parse(lines[0]), {
    msg: "signup_gate",
    triggerSource: "PreSignUp_SignUp",
    allowed: false,
    refusedBy: "blocks",
  });
  assert.ok(!lines[0].includes("spammer"));
});

test("a blocked domain is refused with BLOCKED, ExternalProvider included", async () => {
  const secrets = fakeSecrets([...LIST, "*@example.biz", "*@example.bizz"]);
  const handler = createHandler({ secrets, env: ENV });
  const [err] = await captureLog(() => handler(signUp("anyone@example.biz")));
  assert.equal(err.message, BLOCKED);
  const [err2, lines] = await captureLog(() => handler(signUp("other@example.biz", "PreSignUp_ExternalProvider")));
  assert.equal(err2.message, BLOCKED);
  assert.equal(JSON.parse(lines[0]).triggerSource, "PreSignUp_ExternalProvider");
  assert.equal(secrets.reads, 0);
  // The domain pattern does not reach a longer domain, which the allow list admits.
  const [result] = await captureLog(() => handler(signUp("anyone@example.bizz")));
  assert.equal(result.triggerSource, "PreSignUp_SignUp");
});

test("an unreadable block secret lets an allow-listed address through and the allow list refuses an outsider", async () => {
  const secrets = fakeSecrets();
  secrets.blockFail = true;
  const handler = createHandler({ secrets, env: ENV });
  const input = signUp("owner@example.com");
  const [result, lines] = await captureLog(() => handler(input));
  assert.equal(result, input);
  assert.deepEqual(lines.map((l) => JSON.parse(l)), [
    { msg: "signup_gate", check: "blocks", error: "Error" },
    { msg: "signup_gate", triggerSource: "PreSignUp_SignUp", allowed: true },
  ]);
  assert.ok(!lines.join("\n").includes("owner"));
  const [err, lines2] = await captureLog(() => handler(signUp("stranger@example.com")));
  assert.equal(err.message, REFUSAL);
  assert.equal(JSON.parse(lines2.at(-1)).refusedBy, "allowlist");
});

test("an unparsable block secret fails open", async () => {
  const secrets = fakeSecrets();
  secrets.blockString = JSON.stringify({ entries: BLOCKS });
  const input = signUp("spammer@example.org");
  const [result, lines] = await captureLog(() => createHandler({ secrets, env: ENV })(input));
  assert.equal(result, input);
  assert.equal(JSON.parse(lines[0]).check, "blocks");
});

test("the block list and the allow list are cached separately", async () => {
  const secrets = fakeSecrets();
  let clock = 1_000_000;
  const handler = createHandler({ secrets, env: { ...ENV, ALLOWLIST_TTL_SECONDS: undefined }, now: () => clock });
  await captureLog(() => handler(signUp("owner@example.com")));
  await captureLog(() => handler(signUp("owner@example.com")));
  assert.equal(secrets.blockReads, 1);
  assert.equal(secrets.reads, 1);
  clock += 60_000;
  await captureLog(() => handler(signUp("owner@example.com")));
  assert.equal(secrets.blockReads, 2);
  assert.equal(secrets.reads, 2);
});

// Prod: the block list only

test("prod: an unblocked address passes with no allow list and no SES variables", async () => {
  const secrets = fakeSecrets();
  const handler = createHandler({ secrets, env: PROD_ENV });
  const input = signUp("stranger@example.com");
  const before = structuredClone(input);
  const [result, lines] = await captureLog(() => handler(input));
  assert.equal(result, input);
  assert.deepEqual(result, before);
  assert.equal(secrets.reads, 0);
  assert.equal(secrets.blockReads, 1);
  assert.deepEqual(lines.map((l) => JSON.parse(l)), [
    { msg: "signup_gate", triggerSource: "PreSignUp_SignUp", allowed: true },
  ]);
});

test("prod: a blocked address is refused", async () => {
  const secrets = fakeSecrets();
  const handler = createHandler({ secrets, env: PROD_ENV });
  const [err, lines] = await captureLog(() => handler(signUp("Spammer@example.org")));
  assert.equal(err.message, BLOCKED);
  assert.equal(JSON.parse(lines[0]).refusedBy, "blocks");
  const [err2] = await captureLog(() => handler(signUp("someone@example.biz")));
  assert.equal(err2.message, BLOCKED);
  assert.equal(secrets.reads, 0);
});

test("prod: an unreadable block secret lets the sign-up through", async () => {
  const secrets = fakeSecrets();
  secrets.blockFail = true;
  const handler = createHandler({ secrets, env: PROD_ENV });
  const input = signUp("spammer@example.org");
  const [result, lines] = await captureLog(() => handler(input));
  assert.equal(result, input);
  assert.deepEqual(JSON.parse(lines[0]), { msg: "signup_gate", check: "blocks", error: "Error" });
  assert.equal(JSON.parse(lines[1]).allowed, true);
});

test("prod: a missing block secret id fails open as an unreadable secret does", async () => {
  const secrets = fakeSecrets();
  const input = signUp("owner@example.com");
  const [result, lines] = await captureLog(() => createHandler({ secrets, env: { ALLOWLIST_TTL_SECONDS: "0" } })(input));
  assert.equal(result, input);
  assert.equal(secrets.blockReads, 0);
  assert.equal(JSON.parse(lines[0]).check, "blocks");
});

test("an unknown event shape throws", async () => {
  const handler = createHandler({ secrets: fakeSecrets(), iam: fakeIam(legacyDocument()), env: ENV });
  await assert.rejects(() => handler({}));
  await assert.rejects(() => handler({ action: "other" }));
  await assert.rejects(() => handler({ triggerSource: "CustomMessage_SignUp" }));
  await assert.rejects(() => handler({ triggerSource: "PreSignUp_Other", request: { userAttributes: {} } }));
  await assert.rejects(() => handler(null));
});

// buildDocument

test("buildDocument replaces the legacy statement with the pair in its place", () => {
  const current = legacyDocument();
  const snapshot = structuredClone(current);
  const next = buildDocument(current, LIST, ENV);
  assert.deepEqual(current, snapshot, "the input is not modified");
  assert.equal(next.Version, "2012-10-17");
  assert.equal(next.Statement.length, 4);
  assert.equal(JSON.stringify(next.Statement[0]), JSON.stringify(OTHER_BEFORE));
  assert.deepEqual(next.Statement[1], {
    Sid: "SesSendProd",
    Effect: "Allow",
    Action: ["ses:SendEmail", "ses:SendRawEmail"],
    Resource: [IDENTITY, PROD_SET],
  });
  assert.deepEqual(next.Statement[2], {
    Sid: "SesSendDevAllowList",
    Effect: "Allow",
    Action: ["ses:SendEmail", "ses:SendRawEmail"],
    Resource: DEV_SET,
    Condition: { "ForAllValues:StringLike": { "ses:Recipients": ["owner@example.com", "*@example.org", "tester+*@example.net"] } },
  });
  assert.equal(JSON.stringify(next.Statement[3]), JSON.stringify(OTHER_AFTER));
});

test("buildDocument finds the pair by Sid on a later run, keeps others byte for byte, sorts nothing", () => {
  const first = buildDocument(legacyDocument(), LIST, ENV);
  // A later statement that looks like the legacy one is not touched once the pair exists.
  const lookalike = { Effect: "Allow", Action: ["ses:SendRawEmail", "ses:SendEmail"], Resource: "*" };
  first.Statement.push(lookalike);
  const entries = ["Zed@Example.com", "*@example.org", "alpha@example.com"];
  const next = buildDocument(first, entries, ENV);
  assert.equal(next.Statement.length, 5);
  assert.equal(next.Statement[1].Sid, "SesSendProd");
  assert.equal(next.Statement[2].Sid, "SesSendDevAllowList");
  assert.deepEqual(next.Statement[2].Condition["ForAllValues:StringLike"]["ses:Recipients"], [
    "zed@example.com",
    "*@example.org",
    "alpha@example.com",
  ]);
  for (const i of [0, 3, 4]) assert.equal(JSON.stringify(next.Statement[i]), JSON.stringify(first.Statement[i]));
  assert.equal(JSON.stringify(buildDocument(next, entries, ENV)), JSON.stringify(next), "a rerun is stable");
});

test("buildDocument with an empty list yields a dev statement against an empty array", () => {
  const next = buildDocument(legacyDocument(), [], ENV);
  assert.deepEqual(next.Statement[2].Condition, { "ForAllValues:StringLike": { "ses:Recipients": [] } });
});

test("buildDocument throws when neither the pair nor the legacy statement exists", () => {
  assert.throws(() => buildDocument({ Version: "2012-10-17", Statement: [OTHER_BEFORE] }, LIST, ENV));
  assert.throws(() => buildDocument(legacyDocument(), LIST, { ...ENV, SES_SEND_RESOURCE_DEV: "" }));
  assert.throws(() => buildDocument(legacyDocument(), LIST, { ...ENV, SES_SEND_RESOURCES_PROD: "" }));
});

// sync-ses

test("sync-ses writes the new document when the list changed and skips when unchanged", async () => {
  const secrets = fakeSecrets();
  const iam = fakeIam(legacyDocument());
  const handler = createHandler({ secrets, iam, env: ENV });

  const [first, lines] = await captureLog(() => handler({ action: "sync-ses" }));
  assert.deepEqual(first, { changed: true, entries: 3 });
  assert.equal(iam.puts.length, 1);
  assert.equal(iam.puts[0].RoleName, ENV.SES_ROLE_NAME);
  assert.equal(iam.puts[0].PolicyName, ENV.SES_POLICY_NAME);
  assert.equal(iam.puts[0].PolicyDocument, JSON.stringify(buildDocument(legacyDocument(), LIST, ENV)));
  assert.deepEqual(JSON.parse(lines[0]), { action: "sync-ses", changed: true, entries: 3 });

  const [second, lines2] = await captureLog(() => handler({ action: "sync-ses" }));
  assert.deepEqual(second, { changed: false, entries: 3 });
  assert.equal(iam.puts.length, 1);
  assert.deepEqual(JSON.parse(lines2[0]), { action: "sync-ses", changed: false, entries: 3 });

  secrets.secretString = JSON.stringify([...LIST, "new@example.com"]);
  const [third] = await captureLog(() => handler({ action: "sync-ses" }));
  assert.deepEqual(third, { changed: true, entries: 4 });
  assert.equal(iam.puts.length, 2);
  const written = JSON.parse(iam.puts[1].PolicyDocument);
  assert.deepEqual(written.Statement[2].Condition["ForAllValues:StringLike"]["ses:Recipients"].at(-1), "new@example.com");
});

test("sync-ses throws on any failure", async () => {
  const secrets = fakeSecrets();
  secrets.fail = true;
  const iam = fakeIam(legacyDocument());
  await assert.rejects(() => createHandler({ secrets, iam, env: ENV })({ action: "sync-ses" }));
  assert.equal(iam.puts.length, 0);

  const noPair = fakeIam({ Version: "2012-10-17", Statement: [OTHER_BEFORE] });
  await assert.rejects(() => createHandler({ secrets: fakeSecrets(), iam: noPair, env: ENV })({ action: "sync-ses" }));
  assert.equal(noPair.puts.length, 0);
});
