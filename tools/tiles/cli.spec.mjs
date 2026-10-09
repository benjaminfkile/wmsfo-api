import assert from "node:assert/strict";
import { test } from "node:test";
import { USAGE, main, parseCommand } from "./cli.mjs";
import { lines } from "./testing/fakes.mjs";

const VALLEY = "-114.75,46.35,-113.30,47.25";

test("build parses every flag", () => {
  const { command, options } = parseCommand([
    "build", "--bbox", VALLEY, "--name", "Missoula valley", "--max-zoom", "14", "--terrain-max-zoom", "12",
    "--build", "20261001", "--dry-run", "--out", "/tmp/pkg",
  ]);
  assert.equal(command, "build");
  assert.deepEqual(options, {
    event: undefined,
    bboxFile: undefined,
    bbox: { west: -114.75, south: 46.35, east: -113.3, north: 47.25 },
    name: "Missoula valley",
    maxZoom: 14,
    terrainMaxZoom: 12,
    noTerrain: false,
    build: "20261001",
    dryRun: true,
    out: "/tmp/pkg",
  });
});

test("build defaults: no zooms named, terrain on, out under ./out", () => {
  const { options } = parseCommand(["build", "--event", "41"]);
  assert.equal(options.event, 41);
  assert.equal(options.maxZoom, undefined);
  assert.equal(options.terrainMaxZoom, undefined);
  assert.equal(options.noTerrain, false);
  assert.equal(options.dryRun, false);
  assert.equal(options.out, "out");
});

test("build takes --bbox-file and --no-terrain, and --bbox=value", () => {
  assert.equal(parseCommand(["build", "--bbox-file", "box.json", "--no-terrain"]).options.bboxFile, "box.json");
  assert.equal(parseCommand(["build", "--bbox-file", "box.json", "--no-terrain"]).options.noTerrain, true);
  assert.equal(parseCommand(["build", `--bbox=${VALLEY}`, "--name", "V"]).options.bbox.west, -114.75);
});

const BAD = [
  [["build", "--event", "41", "--bbox", VALLEY, "--name", "V"], /one box source only/],
  [["build", "--bbox-file", "a.json", "--event", "3"], /one box source only/],
  [["build"], /a box source is required/],
  [["build", "--bbox", VALLEY], /--name is required/],
  [["build", "--bbox", VALLEY, "--name", " "], /--name/],
  [["build", "--bbox", "-114.75,46.35,-114.73,47.25", "--name", "V"], /east minus west must be 0.05 to 20/],
  [["build", "--bbox", "-140,30,-110,47", "--name", "V"], /east minus west must be 0.05 to 20/],
  [["build", "--bbox", "-113,46.35,-114,47.25", "--name", "V"], /west must be less than east/],
  [["build", "--bbox", "-114,47.25,-113,46.35", "--name", "V"], /south must be less than north/],
  [["build", "--bbox", "1,2,3", "--name", "V"], /four numbers/],
  [["build", "--event", "x"], /event id/],
  [["build", "--event", "1", "--max-zoom", "16"], /--max-zoom must be an integer from 8 to 15/],
  [["build", "--event", "1", "--terrain-max-zoom", "16"], /--terrain-max-zoom must be an integer from 8 to 15/],
  [["build", "--event", "1", "--terrain-max-zoom", "12", "--no-terrain"], /do not go together/],
  [["build", "--event", "1", "--build", "2026-10-01"], /YYYYMMDD/],
  [["build", "--event", "1", "--zoom", "3"], /Unknown option/],
  [["build", "--event", "1", "extra"], /no argument/],
  [["upload"], /one package directory/],
  [["upload", "a", "b"], /one package directory/],
  [["upload", "a", "--force"], /Unknown option/],
  [["list", "x"], /no argument/],
  [["remove"], /unknown command/],
  [[], /a command is required/],
];

for (const [argv, message] of BAD) {
  test(`refuses ${argv.join(" ") || "no arguments"}`, () => {
    assert.throws(() => parseCommand(argv), message);
  });
}

test("upload and list parse", () => {
  assert.deepEqual(parseCommand(["upload", "./out/abc"]), { command: "upload", options: { dir: "./out/abc" } });
  assert.deepEqual(parseCommand(["list"]), { command: "list", options: {} });
});

test("a bad call prints the usage and exits 1", async () => {
  const err = lines();
  const code = await main(["build", "--bbox", VALLEY], { err: err.push, out: () => {}, env: {} });
  assert.equal(code, 1);
  assert.match(err.list[0], /--name is required/);
  assert.equal(err.list[1], USAGE);
});

test("a missing API setting is one line and exit 1", async () => {
  const err = lines();
  const code = await main(["list"], { err: err.push, out: () => {}, env: { WMSFO_API_KEY: "wak_x" } });
  assert.equal(code, 1);
  assert.equal(err.list.length, 1);
  assert.match(err.list[0], /WMSFO_API_BASE_URL must be set/);
});
