import assert from "node:assert/strict";
import { mkdtempSync, rmSync, truncateSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { createApi } from "./api.mjs";
import { PART_SIZE, batches, partPlan, runUpload } from "./upload.mjs";
import { json, lines, noSleep, withFetch } from "../testing/fakes.mjs";

const MB = 1024 * 1024;
const BOX = { west: -114.1, south: 46.8, east: -114.0, north: 46.9 };

test("the part plan: 64 MB parts, the last one shorter", () => {
  assert.equal(PART_SIZE, 64 * MB);
  assert.deepEqual(partPlan(130 * MB), [
    { partNumber: 1, start: 0, length: 64 * MB },
    { partNumber: 2, start: 64 * MB, length: 64 * MB },
    { partNumber: 3, start: 128 * MB, length: 2 * MB },
  ]);
  assert.deepEqual(partPlan(128 * MB).map((p) => p.length), [64 * MB, 64 * MB]);
  assert.deepEqual(partPlan(10), [{ partNumber: 1, start: 0, length: 10 }]);
});

test("part numbers are signed in batches of at most 100", () => {
  const numbers = partPlan(250 * PART_SIZE + 1).map((p) => p.partNumber);
  const groups = batches(numbers);
  assert.deepEqual(groups.map((g) => g.length), [100, 100, 51]);
  assert.deepEqual([groups[1][0], groups[2].at(-1)], [101, 251]);
});

// A package directory with a manifest and archives of the given sizes.
function packageDir(sizes, terrainMaxZoom) {
  const dir = mkdtempSync(join(tmpdir(), "tiles-up-"));
  const manifest = { name: "Small", bbox: BOX, minZoom: 8, maxZoom: 10, terrainMaxZoom, sourceBuild: "2026-10-01", packageKey: "k".repeat(64) };
  writeFileSync(join(dir, "manifest.json"), JSON.stringify(manifest));
  for (const [file, size] of Object.entries(sizes)) {
    writeFileSync(join(dir, file), "");
    truncateSync(join(dir, file), size);
  }
  return { dir, manifest };
}

const getApi = () => createApi({ baseUrl: "https://api.example", key: "wak_k", sleep: noSleep });

// The scripted API and part store; confirmAnswer replaces the confirm's answer.
function scriptedApi({ created = 200, confirm, failPut = new Set() } = {}) {
  const failed = new Set();
  return ({ method, url, body }) => {
    const path = url.replace("https://api.example", "");
    if (method === "POST" && path === "/admin/maps") {
      return json(created, { id: 7, packageKey: "k".repeat(64), uploads: { tiles: { uploadId: "u-tiles" }, terrain: body.terrainMaxZoom === null ? null : { uploadId: "u-terrain" } } });
    }
    if (method === "POST" && path === "/admin/maps/7/parts") {
      return json(200, { file: body.file, parts: body.partNumbers.map((n) => ({ partNumber: n, url: `https://store.example/${body.file}/${n}` })), expiresAt: "2026-10-09T00:15:00Z" });
    }
    if (method === "PUT") {
      if (failPut.has(url) && !failed.has(url)) {
        failed.add(url);
        return new Response("", { status: 500 });
      }
      return new Response(null, { status: 200, headers: { ETag: `"${url.split("/").slice(-2).join("-")}"` } });
    }
    if (method === "POST" && path === "/admin/maps/7/complete") return json(200, { id: 7, state: "pending" });
    if (method === "POST" && path === "/admin/maps/7/confirm") return confirm ? confirm() : json(200, { id: 7, name: "Small", state: "ready" });
    return json(404, { code: "not_found", message: `${method} ${path}` });
  };
}

const shape = (calls) => calls.map((c) => `${c.method} ${c.url.replace("https://api.example", "")}`);

test("upload: create (200, an open upload), parts, PUTs with retry, complete in order, confirm", async () => {
  const { dir } = packageDir({ "tiles.pmtiles": 130 * MB }, null);
  try {
    const out = lines();
    const code = await withFetch(scriptedApi({ failPut: new Set(["https://store.example/tiles/2"]) }), async (calls) => {
      const result = await runUpload(dir, { getApi, env: {}, out: out.push, sleep: noSleep });
      const seen = shape(calls);
      assert.deepEqual(seen.slice(0, 2), ["POST /admin/maps", "POST /admin/maps/7/parts"]);
      assert.deepEqual(seen.slice(2, 6).sort(), [
        "PUT https://store.example/tiles/1",
        "PUT https://store.example/tiles/2",
        "PUT https://store.example/tiles/2",
        "PUT https://store.example/tiles/3",
      ]);
      assert.deepEqual(seen.slice(6), ["POST /admin/maps/7/complete", "POST /admin/maps/7/confirm"]);
      assert.deepEqual(calls[0].body, { name: "Small", bbox: BOX, minZoom: 8, maxZoom: 10, terrainMaxZoom: null, sourceBuild: "2026-10-01" });
      assert.deepEqual(calls[1].body, { file: "tiles", partNumbers: [1, 2, 3] });
      const puts = calls.filter((c) => c.method === "PUT");
      assert.deepEqual(puts.map((c) => c.body.length).sort((a, b) => a - b), [2 * MB, 64 * MB, 64 * MB, 64 * MB]);
      assert.deepEqual(calls[6].body, {
        file: "tiles",
        parts: [1, 2, 3].map((n) => ({ partNumber: n, etag: `"tiles-${n}"` })),
      });
      return result;
    });
    assert.equal(code, 0);
    assert.match(out.list[0], /map 7 resumed/);
    assert.equal(out.list.at(-1), 'map 7 "Small"');
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("upload with terrain sends both archives and ends on the panel URL", async () => {
  const { dir } = packageDir({ "tiles.pmtiles": 1000, "terrain.pmtiles": 2000 }, 9);
  try {
    const out = lines();
    await withFetch(scriptedApi({ created: 201 }), async (calls) => {
      assert.equal(await runUpload(dir, { getApi, env: { WMSFO_PANEL_BASE_URL: "https://admin.example/" }, out: out.push, sleep: noSleep }), 0);
      assert.deepEqual(shape(calls), [
        "POST /admin/maps",
        "POST /admin/maps/7/parts",
        "PUT https://store.example/tiles/1",
        "POST /admin/maps/7/complete",
        "POST /admin/maps/7/parts",
        "PUT https://store.example/terrain/1",
        "POST /admin/maps/7/complete",
        "POST /admin/maps/7/confirm",
      ]);
      assert.equal(calls[0].body.terrainMaxZoom, 9);
      assert.equal(calls[6].body.file, "terrain");
    });
    assert.match(out.list[0], /map 7 created/);
    assert.equal(out.list.at(-1), "https://admin.example/maps");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("409 package_invalid prints the field and the reason and exits 1", async () => {
  const { dir } = packageDir({ "tiles.pmtiles": 1000 }, null);
  try {
    const out = lines();
    const confirm = () => json(409, { code: "package_invalid", message: "The archive does not match", details: { field: "tiles", reason: "bounds" } });
    await withFetch(scriptedApi({ confirm }), async () => {
      assert.equal(await runUpload(dir, { getApi, env: {}, out: out.push, sleep: noSleep }), 1);
    });
    assert.equal(out.list.at(-1), "package invalid: tiles: bounds");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("409 package_exists prints the ready map and exits 0", async () => {
  const { dir, manifest } = packageDir({ "tiles.pmtiles": 1000 }, null);
  try {
    const out = lines();
    await withFetch(({ method, url }) => {
      if (method === "POST") return json(409, { code: "package_exists", message: "exists", details: null });
      assert.equal(url, "https://api.example/admin/maps");
      return json(200, { items: [
        { id: 3, name: "Pending twin", packageKey: manifest.packageKey, state: "pending" },
        { id: 4, name: "Ready twin", packageKey: manifest.packageKey, state: "ready" },
      ] });
    }, async (calls) => {
      assert.equal(await runUpload(dir, { getApi, env: {}, out: out.push, sleep: noSleep }), 0);
      assert.deepEqual(shape(calls), ["POST /admin/maps", "GET /admin/maps"]);
    });
    assert.match(out.list.at(-1), /map 4 "Ready twin" is ready/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("a missing archive stops before any call", async () => {
  const { dir } = packageDir({ "tiles.pmtiles": 10 }, 9);
  try {
    await withFetch(() => assert.fail("no call"), async () => {
      await assert.rejects(runUpload(dir, { getApi, env: {}, out: () => {} }), /terrain\.pmtiles is missing/);
    });
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
