import assert from "node:assert/strict";
import { existsSync, mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { DatabaseSync } from "node:sqlite";
import { test } from "node:test";
import { manifestOf, openMbtiles, probeBuild, runBuild, sourceBuildOf, tileRange, tilesOf, tileUrl } from "./build.mjs";
import { fakeSpawn, lines, noSleep, withFetch } from "../testing/fakes.mjs";

const VALLEY = { west: -114.75, south: 46.35, east: -113.3, north: 47.25 };
const NOW = new Date("2026-10-09T18:00:00Z");

test("the probe walks back from today and stops at the first 200", async () => {
  await withFetch(({ url }) => new Response(null, { status: url.endsWith("20261007.pmtiles") || url.endsWith("20261006.pmtiles") ? 200 : 404 }), async (calls) => {
    assert.equal(await probeBuild(NOW), "20261007");
    assert.deepEqual(calls.map((c) => [c.method, c.url]), [
      ["HEAD", "https://build.protomaps.com/20261009.pmtiles"],
      ["HEAD", "https://build.protomaps.com/20261008.pmtiles"],
      ["HEAD", "https://build.protomaps.com/20261007.pmtiles"],
    ]);
  });
});

test("the probe gives up after today and seven days back", async () => {
  await withFetch(() => new Response(null, { status: 404 }), async (calls) => {
    await assert.rejects(probeBuild(NOW), /--build YYYYMMDD/);
    assert.equal(calls.length, 8);
    assert.match(calls.at(-1).url, /20261002\.pmtiles$/);
  });
});

test("sourceBuild is the date with dashes", () => {
  assert.equal(sourceBuildOf("20261001"), "2026-10-01");
});

test("the tile range covers the box", () => {
  assert.deepEqual(tileRange(VALLEY, 0), { minX: 0, maxX: 0, minY: 0, maxY: 0 });
  const r = tileRange(VALLEY, 8);
  assert.deepEqual(r, { minX: 46, maxX: 47, minY: 89, maxY: 90 });
  assert.equal([...tilesOf(VALLEY, 1)].length, 2);
  assert.equal(tileUrl("https://dem.example/{z}/{x}/{y}.png", { z: 3, x: 1, y: 2 }), "https://dem.example/3/1/2.png");
});

test("the mbtiles writer stores TMS rows and the metadata", () => {
  const dir = mkdtempSync(join(tmpdir(), "tiles-"));
  try {
    const path = join(dir, "terrain.mbtiles");
    const writer = openMbtiles(path, { name: "Test", bbox: VALLEY, minZoom: 0, maxZoom: 1 });
    for (const x of [0, 1]) for (const y of [0, 1]) writer.put(1, x, y, new Uint8Array([x, y]));
    writer.close();
    const db = new DatabaseSync(path, { readOnly: true });
    const rows = db.prepare("SELECT zoom_level, tile_column, tile_row, tile_data FROM tiles ORDER BY tile_column, tile_row").all();
    assert.deepEqual(rows.map((r) => [r.zoom_level, r.tile_column, r.tile_row, [...r.tile_data]]), [
      [1, 0, 0, [0, 1]],
      [1, 0, 1, [0, 0]],
      [1, 1, 0, [1, 1]],
      [1, 1, 1, [1, 0]],
    ]);
    const meta = Object.fromEntries(db.prepare("SELECT name, value FROM metadata").all().map((r) => [r.name, r.value]));
    assert.deepEqual(meta, {
      name: "Test", format: "png", bounds: "-114.75,46.35,-113.3,47.25", minzoom: "0", maxzoom: "1", type: "baselayer",
    });
    const index = db.prepare("SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'tile_index'").get();
    assert.match(index.sql, /UNIQUE INDEX tile_index ON tiles \(zoom_level, tile_column, tile_row\)/);
    db.close();
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("the manifest carries the identity and its key", () => {
  assert.deepEqual(manifestOf({ name: "Missoula valley", bbox: VALLEY, maxZoom: 15, terrainMaxZoom: 13, sourceBuild: "2026-10-01" }), {
    name: "Missoula valley",
    bbox: VALLEY,
    minZoom: 0,
    maxZoom: 15,
    terrainMaxZoom: 13,
    sourceBuild: "2026-10-01",
    packageKey: "660ecae3f635b40d8b88525e15012ec4992db199774c055539abe7a541cbd4ec",
  });
});

function options(overrides) {
  return { bbox: VALLEY, name: "Missoula valley", noTerrain: false, dryRun: false, ...overrides };
}

test("--build skips the probe; --dry-run passes --dry-run and stops", async () => {
  const dir = mkdtempSync(join(tmpdir(), "tiles-"));
  try {
    const { spawn, calls } = fakeSpawn();
    await withFetch(() => assert.fail("no fetch"), async () => {
      await runBuild(options({ build: "20261001", dryRun: true, out: dir }), { spawn, env: {}, out: () => {} });
    });
    assert.deepEqual(calls.map((c) => c[1]), [
      ["version"],
      ["extract", "https://build.protomaps.com/20261001.pmtiles", join(dir, "660ecae3f635b40d8b88525e15012ec4992db199774c055539abe7a541cbd4ec", "tiles.pmtiles"),
        "--bbox=-114.75,46.35,-113.3,47.25", "--maxzoom=15", "--dry-run"],
    ]);
    assert.equal(existsSync(join(dir, "660ecae3f635b40d8b88525e15012ec4992db199774c055539abe7a541cbd4ec")), false);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("terrain is required unless --no-terrain", async () => {
  await assert.rejects(runBuild(options({ build: "20261001", out: "x" }), { spawn: fakeSpawn().spawn, env: {}, out: () => {} }), /WMSFO_TILES_TERRAIN_URL/);
});

test("a build with terrain: probe, extract, terrain fetches into mbtiles, convert, manifest, key last", async () => {
  const dir = mkdtempSync(join(tmpdir(), "tiles-"));
  const box = { west: -114.1, south: 46.8, east: -114.0, north: 46.9 };
  try {
    const { spawn, calls } = fakeSpawn();
    const out = lines();
    let mbtilesSeen = null;
    let failures = 0;
    const manifest = await withFetch(({ method, url }) => {
      if (method === "HEAD") return new Response(null, { status: url.endsWith("20261009.pmtiles") ? 200 : 404 });
      if (url.endsWith("/8/46/90.png") && failures++ < 2) return new Response("busy", { status: 503 });
      return new Response(new Uint8Array([137, 80, 78, 71]), { status: 200 });
    }, async (fetches) => {
      const spawnWatch = (cmd, args, opts) => {
        if (args[0] === "convert") {
          const db = new DatabaseSync(args[1], { readOnly: true });
          mbtilesSeen = db.prepare("SELECT count(*) AS n FROM tiles").get().n;
          db.close();
        }
        return spawn(cmd, args, opts);
      };
      const result = await runBuild(
        options({ bbox: box, name: "Small", terrainMaxZoom: 8, maxZoom: 10, out: dir }),
        { spawn: spawnWatch, env: { WMSFO_TILES_TERRAIN_URL: "https://dem.example/{z}/{x}/{y}.png" }, out: out.push, now: () => NOW, sleep: noSleep },
      );
      const tileFetches = fetches.filter((f) => f.method === "GET");
      assert.equal(tileFetches.length, [...tilesOf(box, 8)].length + 2);
      return result;
    });
    const pkg = join(dir, manifest.packageKey);
    assert.deepEqual(calls.map((c) => c[1][0]), ["version", "extract", "convert"]);
    assert.deepEqual(calls[2][1], ["convert", join(pkg, "terrain.mbtiles"), join(pkg, "terrain.pmtiles")]);
    assert.equal(mbtilesSeen, [...tilesOf(box, 8)].length);
    assert.equal(existsSync(join(pkg, "terrain.mbtiles")), false);
    assert.deepEqual(JSON.parse(readFileSync(join(pkg, "manifest.json"), "utf8")), {
      name: "Small", bbox: box, minZoom: 0, maxZoom: 10, terrainMaxZoom: 8, sourceBuild: "2026-10-09", packageKey: manifest.packageKey,
    });
    assert.equal(out.list.at(-1), manifest.packageKey);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("--no-terrain extracts only and declares terrainMaxZoom null", async () => {
  const dir = mkdtempSync(join(tmpdir(), "tiles-"));
  try {
    const { spawn, calls } = fakeSpawn();
    const manifest = await runBuild(options({ noTerrain: true, build: "20261001", out: dir }), { spawn, env: {}, out: () => {} });
    assert.equal(manifest.terrainMaxZoom, null);
    assert.deepEqual(calls.map((c) => c[1][0]), ["version", "extract"]);
    assert.equal(JSON.parse(readFileSync(join(dir, manifest.packageKey, "manifest.json"), "utf8")).terrainMaxZoom, null);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
