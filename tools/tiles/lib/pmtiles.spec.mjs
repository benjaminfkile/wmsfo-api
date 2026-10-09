import assert from "node:assert/strict";
import { test } from "node:test";
import { INSTALL_LINE, checkPmtiles, convert, convertArgs, extract, extractArgs } from "./pmtiles.mjs";
import { fakeSpawn } from "../testing/fakes.mjs";

const VALLEY = { west: -114.75, south: 46.35, east: -113.3, north: 47.25 };

test("the extract command line", () => {
  assert.deepEqual(extractArgs({ source: "https://build.protomaps.com/20261001.pmtiles", output: "out/k/tiles.pmtiles", bbox: VALLEY, maxZoom: 15 }), [
    "extract", "https://build.protomaps.com/20261001.pmtiles", "out/k/tiles.pmtiles",
    "--bbox=-114.75,46.35,-113.3,47.25", "--maxzoom=15",
  ]);
  assert.equal(extractArgs({ source: "s", output: "o", bbox: VALLEY, maxZoom: 12, dryRun: true }).at(-1), "--dry-run");
});

test("the convert command line", () => {
  assert.deepEqual(convertArgs({ input: "terrain.mbtiles", output: "terrain.pmtiles" }), ["convert", "terrain.mbtiles", "terrain.pmtiles"]);
});

test("the binary check runs pmtiles version and prints the install line when it fails", async () => {
  const ok = fakeSpawn();
  await checkPmtiles(ok.spawn);
  assert.deepEqual(ok.calls, [["pmtiles", ["version"], { stdio: "ignore" }]]);
  await assert.rejects(checkPmtiles(fakeSpawn(() => 127).spawn), { message: INSTALL_LINE });
  const missing = () => {
    throw Object.assign(new Error("spawn pmtiles ENOENT"), { code: "ENOENT" });
  };
  await assert.rejects(checkPmtiles(missing), { message: INSTALL_LINE });
  assert.match(INSTALL_LINE, /go-pmtiles/);
});

test("extract and convert inherit stdio and fail on a non-zero exit", async () => {
  const { spawn, calls } = fakeSpawn((args) => (args[0] === "convert" ? 2 : 0));
  await extract(spawn, { source: "s", output: "o", bbox: VALLEY, maxZoom: 15 });
  assert.deepEqual(calls[0][2], { stdio: "inherit" });
  await assert.rejects(convert(spawn, { input: "a", output: "b" }), /pmtiles convert exited with 2/);
});
