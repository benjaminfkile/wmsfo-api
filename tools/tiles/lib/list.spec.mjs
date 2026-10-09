import assert from "node:assert/strict";
import { test } from "node:test";
import { createApi } from "./api.mjs";
import { runList } from "./list.mjs";
import { json, lines, withFetch } from "../testing/fakes.mjs";

const getApi = () => createApi({ baseUrl: "https://api.example", key: "wak_k" });

test("list prints the maps as a table", async () => {
  const out = lines();
  await withFetch(() => json(200, { items: [
    { id: 1, name: "Missoula valley", state: "ready", bbox: { west: -114.75, south: 46.35, east: -113.3, north: 47.25 }, minZoom: 0, maxZoom: 15, terrainMaxZoom: 13, tilesBytes: 104857600, terrainBytes: 181403648, eventCount: 2 },
    { id: 9, name: "Small", state: "pending", bbox: { west: -114.1, south: 46.8, east: -114, north: 46.9 }, minZoom: 8, maxZoom: 10, terrainMaxZoom: null, tilesBytes: null, terrainBytes: null, eventCount: 0 },
  ] }), async (calls) => {
    assert.equal(await runList({ getApi, out: out.push }), 0);
    assert.equal(calls[0].url, "https://api.example/admin/maps");
  });
  assert.deepEqual(out.list, [
    "id  name             state    box                         zooms      terrain  sizes                events",
    "1   Missoula valley  ready    -114.75,46.35,-113.3,47.25  0-15 (13)  yes      100.0 MB + 173.0 MB  2",
    "9   Small            pending  -114.1,46.8,-114,46.9       8-10       no       -                    0",
  ]);
});

test("an empty list says so", async () => {
  const out = lines();
  await withFetch(() => json(200, { items: [] }), () => runList({ getApi, out: out.push }));
  assert.deepEqual(out.list, ["no maps"]);
});
