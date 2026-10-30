import assert from "node:assert/strict";
import { test } from "node:test";
import { lagMs, pollCadence, pollsFor, seqRegressed, stagesFor, startVusFor, utf8Length } from "./live.js";

test("lag is now minus publishedAt", () => {
  const at = Date.parse("2026-12-22T01:31:07.430Z");
  assert.equal(lagMs(at + 120, "2026-12-22T01:31:07.430Z"), 120);
  assert.equal(lagMs(at - 5, "2026-12-22T01:31:07.430Z"), -5);
  assert.equal(lagMs(at, "not a time"), null);
  assert.equal(lagMs(at, undefined), null);
});

test("a seq regressed when it repeats or steps back", () => {
  assert.equal(seqRegressed(null, 7), false);
  assert.equal(seqRegressed(7, 8), false);
  assert.equal(seqRegressed(7, 10), false);
  assert.equal(seqRegressed(7, 7), true);
  assert.equal(seqRegressed(7, 5), true);
  assert.equal(seqRegressed(7, null), false);
});

test("the cadence is the interval, or the quiet cadence while live", () => {
  const now = 100000;
  const live = { eventStatusId: 3, pollIntervalMs: 5000 };
  assert.equal(pollCadence(live, now - 1000, now), 5000);
  assert.equal(pollCadence(live, now - 10000, now), 5000);
  assert.equal(pollCadence(live, now - 10001, now), 2500);
  assert.equal(pollCadence(live, null, now), 2500);
  assert.equal(pollCadence({ eventStatusId: 3, pollIntervalMs: 1500 }, null, now), 1000);
  // outside status 3 the hub is never quiet
  assert.equal(pollCadence({ eventStatusId: 2, pollIntervalMs: 5000 }, null, now), 5000);
  assert.equal(pollCadence({ eventStatusId: null, pollIntervalMs: 8000 }, null, now), 8000);
  // no live object yet: the fallback interval, not quiet
  assert.equal(pollCadence(null, null, now), 5000);
  assert.equal(pollCadence(null, null, now, 3000), 3000);
  assert.equal(pollCadence({ eventStatusId: 3 }, null, now, 3000), 1500);
});

test("the poll fraction picks users by vu modulo 100", () => {
  assert.ok(pollsFor(1, 1));
  assert.ok(pollsFor(99, 1));
  assert.ok(pollsFor(100, 1));
  assert.ok(!pollsFor(1, 0));
  assert.ok(!pollsFor(100, 0));
  const picked = Array.from({ length: 100 }, (_, i) => i + 1).filter((vu) => pollsFor(vu, 0.3));
  assert.equal(picked.length, 30);
  assert.ok(pollsFor(129, 0.3));
  assert.ok(!pollsFor(130, 0.3));
});

test("stages ramp then hold; a zero ramp is one stage", () => {
  assert.deepEqual(stagesFor(60, 300, 500), [
    { duration: "60s", target: 500 },
    { duration: "300s", target: 500 },
  ]);
  assert.deepEqual(stagesFor(0, 300, 1), [{ duration: "300s", target: 1 }]);
  assert.equal(startVusFor(0, 7), 7);
  assert.equal(startVusFor(60, 7), 0);
});

test("utf8Length counts bytes, not characters", () => {
  assert.equal(utf8Length(""), 0);
  assert.equal(utf8Length("abc"), 3);
  assert.equal(utf8Length("café"), 5);
  assert.equal(utf8Length("€"), 3);
  assert.equal(utf8Length("🎅"), 4);
  assert.equal(utf8Length(JSON.stringify({ type: 6 }) + ""), 11);
});
