// `build`: resolve the box, probe the newest daily basemap build, extract the
// vector archive with pmtiles, fetch the terrarium terrain tiles into an
// MBTiles file through node:sqlite and convert it, then write manifest.json
// beside the archives under <out>/<packageKey>/ and print the key last.
import { mkdir, rm, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { DatabaseSync } from "node:sqlite";
import { resolveBox } from "./bbox.mjs";
import { packageKey } from "./packageKey.mjs";
import { checkPmtiles, convert, extract } from "./pmtiles.mjs";
import { pool } from "./pool.mjs";

export const BUILD_BASE_URL = "https://build.protomaps.com";
export const PROBE_DAYS_BACK = 7;
export const DEFAULT_MAX_ZOOM = 15;
export const DEFAULT_TERRAIN_MAX_ZOOM = 13;
export const MIN_ZOOM = 0;
export const TERRAIN_CONCURRENCY = 8;
export const TERRAIN_RETRIES = 3;

const defaultSleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

export const buildUrl = (ymd) => `${BUILD_BASE_URL}/${ymd}.pmtiles`;

// YYYYMMDD of a date in UTC, the calendar the daily builds are named in.
export function ymdOf(date) {
  return date.toISOString().slice(0, 10).replaceAll("-", "");
}

// YYYYMMDD as the API's sourceBuild, YYYY-MM-DD.
export const sourceBuildOf = (ymd) => `${ymd.slice(0, 4)}-${ymd.slice(4, 6)}-${ymd.slice(6, 8)}`;

// The newest build answering HEAD 200: today first, then each of the seven
// days before it.
export async function probeBuild(now) {
  for (let back = 0; back <= PROBE_DAYS_BACK; back++) {
    const day = new Date(now.getTime() - back * 86_400_000);
    const ymd = ymdOf(day);
    let response;
    try {
      response = await globalThis.fetch(buildUrl(ymd), { method: "HEAD" });
    } catch {
      continue;
    }
    if (response.status === 200) return ymd;
  }
  throw new Error(`no basemap build answered at ${BUILD_BASE_URL} for today or the ${PROBE_DAYS_BACK} days before; pin one with --build YYYYMMDD`);
}

// The web mercator tile columns and rows covering the box at zoom z.
export function tileRange(bbox, z) {
  const n = 2 ** z;
  const clamp = (v) => Math.min(n - 1, Math.max(0, v));
  const column = (lon) => clamp(Math.floor(((lon + 180) / 360) * n));
  const row = (lat) => {
    const rad = (Math.max(-85.0511, Math.min(85.0511, lat)) * Math.PI) / 180;
    return clamp(Math.floor(((1 - Math.log(Math.tan(rad) + 1 / Math.cos(rad)) / Math.PI) / 2) * n));
  };
  return { minX: column(bbox.west), maxX: column(bbox.east), minY: row(bbox.north), maxY: row(bbox.south) };
}

// Every { z, x, y } of the box from zoom 0 to maxZoom.
export function* tilesOf(bbox, maxZoom) {
  for (let z = MIN_ZOOM; z <= maxZoom; z++) {
    const { minX, maxX, minY, maxY } = tileRange(bbox, z);
    for (let x = minX; x <= maxX; x++) {
      for (let y = minY; y <= maxY; y++) yield { z, x, y };
    }
  }
}

export function tileUrl(template, { z, x, y }) {
  return template.replaceAll("{z}", z).replaceAll("{x}", x).replaceAll("{y}", y);
}

// The bytes of a URL, retried TERRAIN_RETRIES times after the first try.
export async function fetchBytes(url, sleep = defaultSleep) {
  let lastError;
  for (let attempt = 0; attempt <= TERRAIN_RETRIES; attempt++) {
    if (attempt > 0) await sleep(500 * 2 ** (attempt - 1));
    try {
      const response = await globalThis.fetch(url);
      if (response.ok) return new Uint8Array(await response.arrayBuffer());
      lastError = new Error(`GET ${url}: HTTP ${response.status}`);
    } catch (error) {
      lastError = new Error(`GET ${url}: ${error.message}`);
    }
  }
  throw lastError;
}

// An MBTiles file at path with its metadata rows written; put takes XYZ
// coordinates and stores the TMS row (2^z - 1 - y). close commits.
export function openMbtiles(path, { name, bbox, minZoom, maxZoom }) {
  const db = new DatabaseSync(path);
  db.exec(`
    CREATE TABLE metadata (name TEXT, value TEXT);
    CREATE TABLE tiles (zoom_level INTEGER, tile_column INTEGER, tile_row INTEGER, tile_data BLOB);
    CREATE UNIQUE INDEX tile_index ON tiles (zoom_level, tile_column, tile_row);
  `);
  const meta = db.prepare("INSERT INTO metadata (name, value) VALUES (?, ?)");
  for (const [key, value] of [
    ["name", name],
    ["format", "png"],
    ["bounds", `${bbox.west},${bbox.south},${bbox.east},${bbox.north}`],
    ["minzoom", String(minZoom)],
    ["maxzoom", String(maxZoom)],
    ["type", "baselayer"],
  ]) {
    meta.run(key, value);
  }
  db.exec("BEGIN");
  const insert = db.prepare("INSERT INTO tiles (zoom_level, tile_column, tile_row, tile_data) VALUES (?, ?, ?, ?)");
  return {
    put(z, x, y, data) {
      insert.run(z, x, 2 ** z - 1 - y, data);
    },
    close() {
      db.exec("COMMIT");
      db.close();
    },
  };
}

// Fetches the box's terrain tiles into path, eight in flight.
export async function writeTerrainMbtiles(path, { name, bbox, terrainMaxZoom, template, sleep, log = () => {} }) {
  const tiles = [...tilesOf(bbox, terrainMaxZoom)];
  log(`terrain: ${tiles.length} tiles, zoom ${MIN_ZOOM} to ${terrainMaxZoom}`);
  const mbtiles = openMbtiles(path, { name, bbox, minZoom: MIN_ZOOM, maxZoom: terrainMaxZoom });
  let done = 0;
  try {
    await pool(tiles, TERRAIN_CONCURRENCY, async (tile) => {
      const data = await fetchBytes(tileUrl(template, tile), sleep);
      mbtiles.put(tile.z, tile.x, tile.y, data);
      done++;
      if (done % 500 === 0) log(`terrain: ${done} of ${tiles.length}`);
    });
  } finally {
    mbtiles.close();
  }
}

export function manifestOf({ name, bbox, maxZoom, terrainMaxZoom, sourceBuild }) {
  const identity = { bbox, minZoom: MIN_ZOOM, maxZoom, terrainMaxZoom };
  return {
    name,
    bbox: { west: bbox.west, south: bbox.south, east: bbox.east, north: bbox.north },
    minZoom: MIN_ZOOM,
    maxZoom,
    terrainMaxZoom,
    sourceBuild,
    packageKey: packageKey(identity),
  };
}

// options: the parsed build flags. deps: spawn, env, out (a line printer),
// getApi, now, sleep, readFile.
export async function runBuild(options, deps) {
  const { spawn, env, out, getApi, now = () => new Date(), sleep = defaultSleep } = deps;
  const box = await resolveBox(options, { getApi, read: deps.readFile });
  const maxZoom = options.maxZoom ?? box.maxZoom ?? DEFAULT_MAX_ZOOM;
  let terrainMaxZoom = null;
  if (!options.noTerrain) {
    terrainMaxZoom = options.terrainMaxZoom !== undefined ? options.terrainMaxZoom
      : box.terrainMaxZoom !== undefined ? box.terrainMaxZoom
      : DEFAULT_TERRAIN_MAX_ZOOM;
  }
  const template = env.WMSFO_TILES_TERRAIN_URL;
  if (terrainMaxZoom !== null && !options.dryRun && !template) {
    throw new Error("WMSFO_TILES_TERRAIN_URL must be set to the terrarium tile template ({z}/{x}/{y}.png), or pass --no-terrain");
  }
  await checkPmtiles(spawn);

  const ymd = options.build ?? (await probeBuild(now()));
  const manifest = manifestOf({ name: box.name, bbox: box.bbox, maxZoom, terrainMaxZoom, sourceBuild: sourceBuildOf(ymd) });
  const dir = join(options.out, manifest.packageKey);
  const source = buildUrl(ymd);
  out(`box ${box.bbox.west},${box.bbox.south},${box.bbox.east},${box.bbox.north} "${box.name}", build ${ymd}`);

  if (options.dryRun) {
    await extract(spawn, { source, output: join(dir, "tiles.pmtiles"), bbox: box.bbox, maxZoom, dryRun: true });
    return manifest;
  }

  await mkdir(dir, { recursive: true });
  await extract(spawn, { source, output: join(dir, "tiles.pmtiles"), bbox: box.bbox, maxZoom });
  if (terrainMaxZoom !== null) {
    const mbtiles = join(dir, "terrain.mbtiles");
    await rm(mbtiles, { force: true });
    await writeTerrainMbtiles(mbtiles, { name: box.name, bbox: box.bbox, terrainMaxZoom, template, sleep, log: out });
    await convert(spawn, { input: mbtiles, output: join(dir, "terrain.pmtiles") });
    await rm(mbtiles, { force: true });
  }
  await writeFile(join(dir, "manifest.json"), `${JSON.stringify(manifest, null, 2)}\n`);
  out(`package ${dir}`);
  out(manifest.packageKey);
  return manifest;
}
