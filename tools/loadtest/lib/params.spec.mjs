import assert from "node:assert/strict";
import { test } from "node:test";
import { readRun } from "./params.js";

const PARAMS = JSON.stringify({ hubUrl: "wss://<gateway-domain>/hub", channel: "wmsfo-api-dev:location", cdnBaseUrl: "https://<cdn-domain>/" });

test("everything absent: the run defaults and the required params are missing", () => {
  const { run, params, missing, problems } = readRun({});
  assert.deepEqual(run, { runId: null, scenario: null, index: 0, count: 1, vus: 1, rampSeconds: 0, holdSeconds: 300, target: null });
  assert.equal(params.pollFraction, 1);
  assert.equal(params.pollIntervalMs, 5000);
  assert.equal(params.allowedLagMs, 2000);
  assert.deepEqual(missing, ["hubUrl", "channel", "cdnBaseUrl"]);
  assert.equal(problems.length, 1);
  assert.match(problems[0], /missing required params in GATEWAY_LOAD_PARAMS: hubUrl, channel, cdnBaseUrl/);
});

test("a full run", () => {
  const { run, params, problems } = readRun({
    GATEWAY_LOAD_RUN_ID: "r1",
    GATEWAY_LOAD_SCENARIO: "tracker-visitor",
    GATEWAY_LOAD_INDEX: "2",
    GATEWAY_LOAD_COUNT: "4",
    GATEWAY_LOAD_VUS: "250",
    GATEWAY_LOAD_RAMP_SECONDS: "60",
    GATEWAY_LOAD_HOLD_SECONDS: "600",
    GATEWAY_LOAD_TARGET: "dev",
    GATEWAY_LOAD_PARAMS: PARAMS,
  });
  assert.deepEqual(problems, []);
  assert.deepEqual(run, { runId: "r1", scenario: "tracker-visitor", index: 2, count: 4, vus: 250, rampSeconds: 60, holdSeconds: 600, target: "dev" });
  assert.deepEqual(params, {
    hubUrl: "wss://<gateway-domain>/hub",
    channel: "wmsfo-api-dev:location",
    cdnBaseUrl: "https://<cdn-domain>",
    pollFraction: 1,
    pollIntervalMs: 5000,
    allowedLagMs: 2000,
  });
});

test("the CDN is required only when someone polls", () => {
  const p = JSON.stringify({ hubUrl: "wss://<gateway-domain>/hub", channel: "svc:location", pollFraction: "0", pollIntervalMs: "3000", allowedLagMs: "500" });
  const { params, problems } = readRun({ GATEWAY_LOAD_PARAMS: p });
  assert.deepEqual(problems, []);
  assert.equal(params.cdnBaseUrl, null);
  assert.equal(params.pollFraction, 0);
  assert.equal(params.pollIntervalMs, 3000);
  assert.equal(params.allowedLagMs, 500);
});

test("values that do not parse are listed", () => {
  assert.match(readRun({ GATEWAY_LOAD_PARAMS: "{nope" }).problems.join("\n"), /must be a JSON object/);
  assert.match(readRun({ GATEWAY_LOAD_PARAMS: "[]" }).problems.join("\n"), /must be a JSON object/);
  assert.match(readRun({ GATEWAY_LOAD_VUS: "ten", GATEWAY_LOAD_PARAMS: PARAMS }).problems.join("\n"), /GATEWAY_LOAD_VUS must be a whole number/);
  const bad = JSON.stringify({ hubUrl: "https://<gateway-domain>/hub", channel: "location", cdnBaseUrl: "https://<cdn-domain>", pollFraction: "2" });
  const text = readRun({ GATEWAY_LOAD_PARAMS: bad }).problems.join("\n");
  assert.match(text, /hubUrl must start with wss/);
  assert.match(text, /channel must be <service>:<topic>/);
  assert.match(text, /pollFraction must be a number from 0 to 1/);
});
