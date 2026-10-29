import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";

const read = (name) => readFileSync(new URL(name, import.meta.url), "utf8");
const importsOf = (source) => [...source.matchAll(/^\s*import\s[^"']*["']([^"']+)["']/gm)].map((m) => m[1]);

test("the library imports only itself: plain ESM for k6 and node alike", () => {
  for (const name of ["lib/signalr.js", "lib/live.js", "lib/params.js", "lib/session.js"]) {
    const source = read(name);
    for (const spec of importsOf(source)) assert.match(spec, /^\.\/[a-z]+\.js$/, `${name} imports ${spec}`);
    assert.doesNotMatch(source, /\brequire\(|\bprocess\.|^await\s/m, name);
  }
});

test("the scenario imports k6 and the library only", () => {
  for (const spec of importsOf(read("scenario.js"))) assert.match(spec, /^(k6(\/[a-z]+)?|\.\/lib\/[a-z]+\.js)$/, spec);
});

test("the scenario declares the custom metrics of the brief", () => {
  const source = read("scenario.js");
  const declared = [...source.matchAll(/new (Counter|Trend|Rate)\("([a-z_]+)"/g)].map((m) => `${m[1]} ${m[2]}`);
  assert.deepEqual(declared, [
    "Counter ws_opened",
    "Counter ws_closed",
    "Counter ws_errors",
    "Counter joins_ok",
    "Counter joins_denied",
    "Trend join_ms",
    "Counter events",
    "Trend event_lag_ms",
    "Counter seq_gaps",
    "Counter evictions",
    "Counter polls",
    "Counter poll_errors",
    "Trend poll_ms",
    "Rate events_on_time",
  ]);
  assert.match(source, /executor: "ramping-vus"/);
});

test("the library loads in node", async () => {
  for (const name of ["./lib/signalr.js", "./lib/live.js", "./lib/params.js", "./lib/session.js"]) await import(name);
});
