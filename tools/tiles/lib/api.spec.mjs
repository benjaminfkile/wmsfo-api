import assert from "node:assert/strict";
import { test } from "node:test";
import { ApiError, apiSettings, createApi, formatApiError } from "./api.mjs";
import { json, withFetch } from "../testing/fakes.mjs";

test("both variables are required with a clear message", () => {
  assert.throws(() => apiSettings({}), /WMSFO_API_BASE_URL and WMSFO_API_KEY must be set/);
  assert.throws(() => apiSettings({ WMSFO_API_BASE_URL: "https://api.example" }), /^Error: WMSFO_API_KEY must be set/);
  assert.deepEqual(apiSettings({ WMSFO_API_BASE_URL: "https://api.example/", WMSFO_API_KEY: "wak_k" }), {
    baseUrl: "https://api.example",
    key: "wak_k",
  });
});

test("requests carry the Bearer key and JSON body", async () => {
  await withFetch(() => json(200, { ok: true }), async (calls) => {
    const api = createApi({ baseUrl: "https://api.example", key: "wak_k" });
    const result = await api.request("POST", "/admin/maps", { a: 1 });
    assert.deepEqual(result, { status: 200, body: { ok: true } });
    assert.equal(calls[0].url, "https://api.example/admin/maps");
    assert.equal(calls[0].headers.Authorization, "Bearer wak_k");
    assert.deepEqual(calls[0].body, { a: 1 });
  });
});

test("an error body becomes an ApiError with code, message, and details", async () => {
  await withFetch(() => json(409, { code: "package_invalid", message: "bad", details: { field: "tiles", reason: "bounds" } }), async () => {
    const api = createApi({ baseUrl: "https://api.example", key: "wak_k" });
    const error = await api.request("POST", "/admin/maps/1/confirm").catch((e) => e);
    assert.ok(error instanceof ApiError);
    assert.equal(error.status, 409);
    assert.equal(formatApiError(error), 'package_invalid: bad {"field":"tiles","reason":"bounds"}');
  });
});

test("a 429 waits retryAfterSeconds and retries once", async () => {
  const waits = [];
  let n = 0;
  await withFetch(() => (n++ < 2
    ? json(429, { code: "rate_limited", message: "slow down", details: { retryAfterSeconds: 3 } })
    : json(200, {})), async (calls) => {
    const api = createApi({ baseUrl: "https://api.example", key: "wak_k", sleep: async (ms) => waits.push(ms) });
    const error = await api.request("GET", "/admin/maps").catch((e) => e);
    assert.equal(error.code, "rate_limited");
    assert.equal(calls.length, 2);
    assert.deepEqual(waits, [3000]);
    assert.equal((await api.request("GET", "/admin/maps")).status, 200);
  });
});
