// The run as the gateway hands it over: the GATEWAY_LOAD_* variables and the
// JSON object of string params in GATEWAY_LOAD_PARAMS. Every one may be absent
// when the script is run by hand. Pure: reads the object it is given.

export // Polling is off unless a run asks for it: the CDN is not the system under
// test and every poll is a paid request.
const PARAM_DEFAULTS = {
  pollFraction: "0",
  pollIntervalMs: "5000",
  allowedLagMs: "2000",
};

function count(value, fallback) {
  if (value === undefined || value === null || String(value).trim() === "") return fallback;
  const n = Number(value);
  return Number.isInteger(n) && n >= 0 ? n : NaN;
}

function text(value) {
  return value === undefined || value === null ? null : String(value);
}

// { run, params, problems }. `problems` lists every missing required param
// and every value that does not parse; the run must not start unless it is empty.
export function readRun(env) {
  const e = env || {};
  const problems = [];

  const run = {
    runId: text(e.GATEWAY_LOAD_RUN_ID),
    scenario: text(e.GATEWAY_LOAD_SCENARIO),
    index: count(e.GATEWAY_LOAD_INDEX, 0),
    count: count(e.GATEWAY_LOAD_COUNT, 1),
    vus: count(e.GATEWAY_LOAD_VUS, 1),
    rampSeconds: count(e.GATEWAY_LOAD_RAMP_SECONDS, 0),
    holdSeconds: count(e.GATEWAY_LOAD_HOLD_SECONDS, 300),
    target: text(e.GATEWAY_LOAD_TARGET),
  };
  for (const [key, name] of [
    ["index", "GATEWAY_LOAD_INDEX"],
    ["count", "GATEWAY_LOAD_COUNT"],
    ["vus", "GATEWAY_LOAD_VUS"],
    ["rampSeconds", "GATEWAY_LOAD_RAMP_SECONDS"],
    ["holdSeconds", "GATEWAY_LOAD_HOLD_SECONDS"],
  ]) {
    if (Number.isNaN(run[key])) problems.push(`${name} must be a whole number at or above 0`);
  }

  let raw = {};
  const source = e.GATEWAY_LOAD_PARAMS;
  if (source !== undefined && source !== null && String(source).trim() !== "") {
    try {
      raw = JSON.parse(source);
    } catch {
      raw = null;
    }
    if (!raw || typeof raw !== "object" || Array.isArray(raw)) {
      problems.push("GATEWAY_LOAD_PARAMS must be a JSON object of strings");
      raw = {};
    }
  }
  const given = (key) => {
    const v = raw[key];
    return v === undefined || v === null || String(v).trim() === "" ? null : String(v).trim();
  };

  const params = {
    hubUrl: given("hubUrl"),
    channel: given("channel"),
    cdnBaseUrl: given("cdnBaseUrl"),
    pollFraction: Number(given("pollFraction") ?? PARAM_DEFAULTS.pollFraction),
    pollIntervalMs: Number(given("pollIntervalMs") ?? PARAM_DEFAULTS.pollIntervalMs),
    allowedLagMs: Number(given("allowedLagMs") ?? PARAM_DEFAULTS.allowedLagMs),
  };
  if (params.cdnBaseUrl) params.cdnBaseUrl = params.cdnBaseUrl.replace(/\/+$/, "");

  const missing = [];
  if (!params.hubUrl) missing.push("hubUrl");
  if (!params.channel) missing.push("channel");
  if (!(params.pollFraction >= 0 && params.pollFraction <= 1)) problems.push("pollFraction must be a number from 0 to 1");
  else if (params.pollFraction > 0 && !params.cdnBaseUrl) missing.push("cdnBaseUrl");
  if (!(params.pollIntervalMs > 0)) problems.push("pollIntervalMs must be a number above 0");
  if (!(params.allowedLagMs >= 0)) problems.push("allowedLagMs must be a number at or above 0");
  if (params.hubUrl && !/^wss?:\/\//.test(params.hubUrl)) problems.push("hubUrl must start with wss:// or ws://");
  if (params.channel && !/^[^:]+:[^:]+$/.test(params.channel)) problems.push("channel must be <service>:<topic>");

  if (missing.length) problems.unshift(`missing required params in GATEWAY_LOAD_PARAMS: ${missing.join(", ")}`);
  return { run, params, missing, problems };
}
