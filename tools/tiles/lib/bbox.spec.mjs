import assert from "node:assert/strict";
import { test } from "node:test";
import { bboxProblem, parseBboxFlag, resolveBox } from "./bbox.mjs";
import { createApi } from "./api.mjs";
import { json, withFetch } from "../testing/fakes.mjs";

const VALLEY = { west: -114.75, south: 46.35, east: -113.3, north: 47.25 };

test("sides are 0.05 to 20 degrees, west < east, south < north", () => {
  assert.equal(bboxProblem(VALLEY), null);
  assert.equal(bboxProblem({ west: 0, south: 0, east: 0.05, north: 20 }), null);
  assert.match(bboxProblem({ west: 0, south: 0, east: 0.04, north: 1 }), /east minus west/);
  assert.match(bboxProblem({ west: 0, south: 0, east: 1, north: 20.5 }), /north minus south/);
  assert.match(bboxProblem({ west: 1, south: 0, east: 0, north: 1 }), /west must be less than east/);
  assert.match(bboxProblem({ west: 0, south: 1, east: 1, north: 0 }), /south must be less than north/);
  assert.match(bboxProblem({ west: 0, south: 0, east: "1", north: 1 }), /east must be a number/);
});

test("the box from the flag", async () => {
  const bbox = parseBboxFlag("-114.75,46.35,-113.30,47.25");
  assert.deepEqual(await resolveBox({ bbox, name: "Valley" }), { name: "Valley", bbox: VALLEY, maxZoom: undefined, terrainMaxZoom: undefined });
});

test("the box from an event, its name the default", async () => {
  await withFetch(() => json(200, { id: 41, name: "Flight 2026", trackerBbox: VALLEY }), async (calls) => {
    const getApi = () => createApi({ baseUrl: "https://api.example", key: "wak_k" });
    const box = await resolveBox({ event: 41 }, { getApi });
    assert.equal(calls[0].url, "https://api.example/admin/events/41");
    assert.equal(calls[0].method, "GET");
    assert.deepEqual(box, { name: "Flight 2026", bbox: VALLEY, maxZoom: undefined, terrainMaxZoom: undefined });
    assert.equal((await resolveBox({ event: 41, name: "Other" }, { getApi })).name, "Other");
  });
});

test("the box from an exported file, with its zooms", async () => {
  const read = async () => JSON.stringify({ name: "Missoula valley", bbox: VALLEY, maxZoom: 14, terrainMaxZoom: 12 });
  assert.deepEqual(await resolveBox({ bboxFile: "box.json" }, { read }), {
    name: "Missoula valley", bbox: VALLEY, maxZoom: 14, terrainMaxZoom: 12,
  });
});

test("a file without a name needs --name; a bad file box is refused", async () => {
  const unnamed = async () => JSON.stringify({ bbox: VALLEY });
  await assert.rejects(resolveBox({ bboxFile: "box.json" }, { read: unnamed }), /--name is required/);
  assert.equal((await resolveBox({ bboxFile: "box.json", name: "N" }, { read: unnamed })).name, "N");
  const bad = async () => JSON.stringify({ name: "x", bbox: { ...VALLEY, east: -114.8 } });
  await assert.rejects(resolveBox({ bboxFile: "box.json" }, { read: bad }), /west must be less than east/);
});
