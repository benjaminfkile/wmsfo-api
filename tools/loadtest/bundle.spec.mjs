import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { after, before, test } from "node:test";
import { fileURLToPath } from "node:url";
import { bundle } from "./bundle.mjs";

const bundler = fileURLToPath(new URL("./bundle.mjs", import.meta.url));
const scenario = readFileSync(new URL("./scenario.js", import.meta.url), "utf8");
const dir = mkdtempSync(join(tmpdir(), "wmsfo-bundle-"));
const out = join(dir, "scenario.js");
let text;

before(() => {
  const run = spawnSync(process.execPath, [bundler, "--out", out], { encoding: "utf8" });
  assert.equal(run.status, 0, run.stderr);
  text = readFileSync(out, "utf8");
});

after(() => rmSync(dir, { recursive: true, force: true }));

const count = (source, match) => source.split("\n").filter((line) => (typeof match === "function" ? match(line) : match.test(line))).length;

test("the bundle has no relative import", () => {
  for (const line of text.split("\n")) assert.doesNotMatch(line, /from ["']\.\.?\//, line);
});

test("every k6 import of the scenario is at the top, once", () => {
  const k6 = scenario.split("\n").filter((line) => /^import .* from "k6(\/[a-z]+)?";$/.test(line));
  assert.ok(k6.length >= 4);
  const firstCode = text.split("\n").findIndex((line) => line !== "" && !line.startsWith("//") && !line.startsWith("import "));
  const head = text.split("\n").slice(0, firstCode);
  for (const line of k6) {
    assert.equal(count(text, (l) => l === line), 1, line);
    assert.ok(head.includes(line), `${line} is not at the top`);
  }
  assert.equal(count(text, /^import /), k6.length);
});

test("the scenario exports k6 reads are kept, once each", () => {
  assert.equal(count(text, /^export const options\b/), 1);
  assert.equal(count(text, /^export function setup\b/), 1);
  assert.equal(count(text, /^export default function\b/), 1);
  assert.equal(count(text, /^export\b/), 3);
});

test("the bundle passes node --check", () => {
  const run = spawnSync(process.execPath, ["--check", out], { encoding: "utf8" });
  assert.equal(run.status, 0, run.stderr);
});

test("the bundle keeps every custom metric", () => {
  const names = [...readFileSync(new URL("./scenario.spec.mjs", import.meta.url), "utf8").matchAll(/"(?:Counter|Trend|Rate) ([a-z_]+)"/g)].map((m) => m[1]);
  assert.equal(names.length, 15);
  for (const name of names) assert.ok(text.includes(`("${name}"`), name);
});

test("the lib modules come before the scenario, each after what it imports", () => {
  const at = (name) => text.indexOf(`// ---- ${name} ----`);
  for (const name of ["lib/signalr.js", "lib/live.js", "lib/params.js", "lib/session.js", "scenario.js"]) assert.ok(at(name) > 0, name);
  assert.ok(at("lib/signalr.js") < at("lib/session.js"));
  assert.ok(at("lib/live.js") < at("lib/session.js"));
  assert.ok(at("lib/session.js") < at("scenario.js"));
  assert.ok(at("lib/params.js") < at("scenario.js"));
});

function fixture(files) {
  const root = mkdtempSync(join(dir, "fixture-"));
  for (const [name, source] of Object.entries(files)) {
    mkdirSync(join(root, name, ".."), { recursive: true });
    writeFileSync(join(root, name), source);
  }
  return join(root, "entry.js");
}

test("a name declared in two files is refused with both files named", () => {
  const entry = fixture({
    "entry.js": 'import { a } from "./lib/one.js";\nconst shared = 1;\nexport default function () { a(shared); }\n',
    "lib/one.js": "export function a() {}\nfunction shared() {}\n",
  });
  assert.throws(() => bundle(entry), (err) => /lib\/one\.js:2/.test(err.message) && /entry\.js:2/.test(err.message));
});

test("import * as is refused with the line named", () => {
  const entry = fixture({
    "entry.js": '// one\nimport * as one from "./one.js";\nexport default function () { one.a(); }\n',
    "one.js": "export function a() {}\n",
  });
  assert.throws(() => bundle(entry), /entry\.js:2: `import \* as`/);
});

test("the other forms it does not understand are refused with the line named", () => {
  const cases = [
    ["import a from \"./one.js\";", /entry\.js:1: unsupported import `a`/],
    ["import { a as b } from \"./one.js\";", /entry\.js:1: unsupported import specifier `a as b`/],
    ["export { x as y };", /entry\.js:1: `export \{ x as y \};`/],
    ["export * from \"./one.js\";", /entry\.js:1: /],
    ["const m = await import(\"./one.js\");", /entry\.js:1: dynamic import/],
  ];
  for (const [line, re] of cases) {
    const entry = fixture({ "entry.js": `${line}\nexport default function () {}\n`, "one.js": "export function a() {}\n" });
    assert.throws(() => bundle(entry), re, line);
  }
});

test("an import of a name the file does not export is refused", () => {
  const entry = fixture({
    "entry.js": 'import { b } from "./one.js";\nexport default function () { b(); }\n',
    "one.js": "export function a() {}\n",
  });
  assert.throws(() => bundle(entry), /entry\.js:1: one\.js does not export `b`/);
});
