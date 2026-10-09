import assert from "node:assert/strict";
import { test } from "node:test";
import { canonicalIdentity, packageKey } from "./packageKey.mjs";

const VALLEY = { bbox: { west: -114.75, south: 46.35, east: -113.30, north: 47.25 }, minZoom: 0, maxZoom: 15, terrainMaxZoom: 13 };

test("the valley identity hashes to the seeded key", () => {
  assert.equal(
    canonicalIdentity(VALLEY),
    '{"bbox":{"west":-114.75,"south":46.35,"east":-113.3,"north":47.25},"minZoom":0,"maxZoom":15,"terrainMaxZoom":13}',
  );
  assert.equal(packageKey(VALLEY), "660ecae3f635b40d8b88525e15012ec4992db199774c055539abe7a541cbd4ec");
});

test("key order is fixed whatever order the input has", () => {
  const shuffled = { terrainMaxZoom: 13, maxZoom: 15, minZoom: 0, bbox: { north: 47.25, east: -113.3, south: 46.35, west: -114.75 } };
  assert.equal(packageKey(shuffled), packageKey(VALLEY));
});

test("a package without terrain hashes differently, with terrainMaxZoom null", () => {
  const flat = { ...VALLEY, terrainMaxZoom: null };
  assert.match(canonicalIdentity(flat), /"terrainMaxZoom":null}$/);
  assert.notEqual(packageKey(flat), packageKey(VALLEY));
  assert.equal(packageKey({ ...VALLEY, terrainMaxZoom: undefined }), packageKey(flat));
});
