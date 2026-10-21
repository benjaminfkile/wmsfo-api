// The dev gate: the dev people pool's Pre sign-up trigger and the scheduled
// SES sync, both driven by one allow list held in Secrets Manager.
// Pre sign-up refuses every address outside the list and fails closed when the
// list cannot be read. The sync keeps the instance role's SES statements equal
// to the list, so dev mail reaches only listed addresses. The AWS SDK clients
// come from the Lambda runtime and are loaded on first use; tests inject them.

export const REFUSAL = "This site is by invitation. Ask the operator to add your address.";
export const UNAVAILABLE = "Sign-up is unavailable right now. Try again later.";

export const SES_ACTIONS = ["ses:SendEmail", "ses:SendRawEmail"];
export const SID_PROD = "SesSendProd";
export const SID_DEV = "SesSendDevAllowList";

const DEFAULT_TTL_SECONDS = 60;

function log(line) {
  console.log(JSON.stringify(line));
}

const normalise = (s) => s.trim().toLowerCase();

// True when the address equals an entry, where `*` in an entry matches any run
// of characters (including none). Both sides are trimmed and lowercased.
export function matches(address, entries) {
  if (typeof address !== "string") return false;
  const a = normalise(address);
  if (a === "") return false;
  return entries.some((entry) => {
    const pattern = normalise(entry)
      .split("*")
      .map((part) => part.replace(/[.+?^${}()|[\]\\/-]/g, "\\$&"))
      .join(".*");
    return new RegExp(`^${pattern}$`, "s").test(a);
  });
}

// Parses the secret string: a JSON array of non-empty strings. Anything else throws.
export function parseList(secretString) {
  const list = JSON.parse(secretString);
  if (!Array.isArray(list)) throw new Error("the allow list is not a JSON array");
  for (const entry of list) {
    if (typeof entry !== "string" || entry.trim() === "") {
      throw new Error("the allow list has an entry that is not a non-empty string");
    }
  }
  return list;
}

const sameActions = (action) =>
  Array.isArray(action) &&
  action.length === SES_ACTIONS.length &&
  SES_ACTIONS.every((a) => action.includes(a));

const isLegacy = (s) => s && s.Sid === undefined && s.Effect === "Allow" && sameActions(s.Action);

// Returns a new policy document in which the SesSendProd and
// SesSendDevAllowList statements hold the given entries and resources. The
// pair replaces itself by Sid, or else the first legacy statement (no Sid,
// actions exactly the two SES sends) in its place. Every other statement is
// kept as it is and where it is. Throws when neither is found.
export function buildDocument(current, entries, env) {
  const prodResources = String(env.SES_SEND_RESOURCES_PROD ?? "")
    .split(",")
    .map((r) => r.trim())
    .filter((r) => r !== "");
  const devResource = String(env.SES_SEND_RESOURCE_DEV ?? "").trim();
  if (prodResources.length === 0) throw new Error("SES_SEND_RESOURCES_PROD is not set");
  if (devResource === "") throw new Error("SES_SEND_RESOURCE_DEV is not set");

  const prod = { Sid: SID_PROD, Effect: "Allow", Action: [...SES_ACTIONS], Resource: prodResources };
  const dev = {
    Sid: SID_DEV,
    Effect: "Allow",
    Action: [...SES_ACTIONS],
    Resource: devResource,
    Condition: { "ForAllValues:StringLike": { "ses:Recipients": entries.map(normalise) } },
  };

  const statements = current?.Statement === undefined ? [] : [].concat(current.Statement);
  const prodAt = statements.findIndex((s) => s?.Sid === SID_PROD);
  const devAt = statements.findIndex((s) => s?.Sid === SID_DEV);
  let next;
  if (prodAt >= 0 && devAt >= 0) {
    next = statements.map((s, i) => (i === prodAt ? prod : i === devAt ? dev : s));
  } else if (prodAt >= 0 || devAt >= 0) {
    const at = prodAt >= 0 ? prodAt : devAt;
    next = [...statements.slice(0, at), prod, dev, ...statements.slice(at + 1)];
  } else {
    const at = statements.findIndex(isLegacy);
    if (at < 0) throw new Error("the policy has neither the SES pair nor the legacy SES statement");
    next = [...statements.slice(0, at), prod, dev, ...statements.slice(at + 1)];
  }
  return { ...current, Statement: next };
}

// The runtime's SDK v3 aggregated clients, created on first use.
async function defaultSecrets() {
  const { SecretsManager } = await import("@aws-sdk/client-secrets-manager");
  return new SecretsManager({});
}

async function defaultIam() {
  const { IAM } = await import("@aws-sdk/client-iam");
  return new IAM({});
}

// Returns the handler. `secrets` needs getSecretValue and `iam` needs
// getRolePolicy and putRolePolicy, in the shape of the SDK v3 clients.
export function createHandler({ secrets, iam, env = process.env, now = () => Date.now() } = {}) {
  let cache = null;
  let secretsClient = secrets;
  let iamClient = iam;

  const ttlMs = () => {
    const raw = env.ALLOWLIST_TTL_SECONDS;
    const seconds = raw === undefined || raw === "" ? DEFAULT_TTL_SECONDS : Number(raw);
    return (Number.isFinite(seconds) && seconds >= 0 ? seconds : DEFAULT_TTL_SECONDS) * 1000;
  };

  // The parsed list, read through the secrets client and kept for the TTL.
  async function loadList() {
    if (cache && now() - cache.at < ttlMs()) return cache.entries;
    const secretId = env.ALLOWLIST_SECRET_ID;
    if (!secretId) throw new Error("ALLOWLIST_SECRET_ID is not set");
    secretsClient ??= await defaultSecrets();
    const out = await secretsClient.getSecretValue({ SecretId: secretId });
    if (typeof out?.SecretString !== "string") throw new Error("the allow list secret has no string");
    const entries = parseList(out.SecretString);
    cache = { at: now(), entries };
    return entries;
  }

  async function preSignUp(event) {
    const triggerSource = event.triggerSource;
    if (triggerSource === "PreSignUp_AdminCreateUser") {
      log({ msg: "dev_gate", triggerSource, allowed: true });
      return event;
    }
    if (triggerSource !== "PreSignUp_SignUp" && triggerSource !== "PreSignUp_ExternalProvider") {
      throw new Error(`unknown trigger source ${triggerSource}`);
    }
    let allowed;
    try {
      const email = event.request?.userAttributes?.email;
      if (typeof email !== "string" || email.trim() === "") throw new Error("the event has no email");
      allowed = matches(email, await loadList());
    } catch (err) {
      log({ msg: "dev_gate", triggerSource, allowed: false, error: err?.name ?? "Error" });
      throw new Error(UNAVAILABLE);
    }
    log({ msg: "dev_gate", triggerSource, allowed });
    if (!allowed) throw new Error(REFUSAL);
    return event;
  }

  async function syncSes() {
    const entries = await loadList();
    const roleName = env.SES_ROLE_NAME;
    const policyName = env.SES_POLICY_NAME;
    if (!roleName) throw new Error("SES_ROLE_NAME is not set");
    if (!policyName) throw new Error("SES_POLICY_NAME is not set");
    iamClient ??= await defaultIam();
    const out = await iamClient.getRolePolicy({ RoleName: roleName, PolicyName: policyName });
    const current = JSON.parse(decodeURIComponent(out.PolicyDocument));
    const next = buildDocument(current, entries, env);
    const serialised = JSON.stringify(next);
    const changed = serialised !== JSON.stringify(current);
    if (changed) {
      await iamClient.putRolePolicy({ RoleName: roleName, PolicyName: policyName, PolicyDocument: serialised });
    }
    log({ action: "sync-ses", changed, entries: entries.length });
    return { changed, entries: entries.length };
  }

  return async function handler(event) {
    if (typeof event?.triggerSource === "string" && event.triggerSource.startsWith("PreSignUp_")) {
      return preSignUp(event);
    }
    if (event?.action === "sync-ses") return syncSes();
    throw new Error("unknown event");
  };
}

export const handler = createHandler();
