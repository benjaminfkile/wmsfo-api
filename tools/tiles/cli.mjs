#!/usr/bin/env node
// wmsfo-tiles: builds a tracker map tile package on an operator's machine,
// uploads it through the admin API, and lists the maps (platform.md 1.8).
// A bad call prints the usage; any other failure prints one line; both exit 1.
import { spawn as nodeSpawn } from "node:child_process";
import { realpathSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { parseArgs } from "node:util";
import { ApiError, apiSettings, createApi, formatApiError } from "./lib/api.mjs";
import { parseBboxFlag } from "./lib/bbox.mjs";
import { runList } from "./lib/list.mjs";
import { runUpload } from "./lib/upload.mjs";

export const USAGE = `usage:
  wmsfo-tiles build (--event <id> | --bbox-file <file> | --bbox <west,south,east,north>) [--name <name>]
                    [--max-zoom 8..15] [--terrain-max-zoom 8..15] [--no-terrain] [--build YYYYMMDD]
                    [--dry-run] [--out <dir>]
  wmsfo-tiles upload <package dir>
  wmsfo-tiles list

environment: WMSFO_API_BASE_URL, WMSFO_API_KEY (a wak_ key with maps, and events for --event),
             WMSFO_TILES_TERRAIN_URL (build with terrain), WMSFO_PANEL_BASE_URL (optional)`;

export class UsageError extends Error {}

const BUILD_OPTIONS = {
  event: { type: "string" },
  "bbox-file": { type: "string" },
  bbox: { type: "string" },
  name: { type: "string" },
  "max-zoom": { type: "string" },
  "terrain-max-zoom": { type: "string" },
  "no-terrain": { type: "boolean" },
  build: { type: "string" },
  "dry-run": { type: "boolean" },
  out: { type: "string" },
};

function integerFlag(values, flag, min, max) {
  const text = values[flag];
  if (text === undefined) return undefined;
  if (!/^\d+$/.test(text) || Number(text) < min || Number(text) > max) {
    throw new UsageError(`--${flag} must be an integer from ${min} to ${max}`);
  }
  return Number(text);
}

// "--bbox -114.75,..." as "--bbox=-114.75,...": parseArgs refuses a separate
// value that starts with a dash, and a western box always does.
function joinNegativeValues(args) {
  const joined = [];
  for (let i = 0; i < args.length; i++) {
    const flag = args[i].startsWith("--") ? args[i].slice(2) : null;
    if (flag && BUILD_OPTIONS[flag]?.type === "string" && /^-\d/.test(args[i + 1] ?? "")) {
      joined.push(`${args[i]}=${args[++i]}`);
    } else {
      joined.push(args[i]);
    }
  }
  return joined;
}

function parseBuild(args) {
  const { values, positionals } = parseArgs({
    args: joinNegativeValues(args),
    options: BUILD_OPTIONS,
    strict: true,
    allowPositionals: true,
  });
  if (positionals.length) throw new UsageError(`build takes no argument: ${positionals[0]}`);
  const sources = ["event", "bbox-file", "bbox"].filter((s) => values[s] !== undefined);
  if (sources.length !== 1) {
    throw new UsageError(sources.length
      ? `one box source only, not ${sources.map((s) => `--${s}`).join(" and ")}`
      : "a box source is required: --event, --bbox-file, or --bbox");
  }
  if (values.event !== undefined && !/^[1-9]\d*$/.test(values.event)) {
    throw new UsageError("--event must be an event id");
  }
  let bbox;
  if (values.bbox !== undefined) {
    try {
      bbox = parseBboxFlag(values.bbox);
    } catch (error) {
      throw new UsageError(error.message);
    }
    if (!values.name?.trim()) throw new UsageError("--name is required with --bbox");
  }
  if (values.name !== undefined && !values.name.trim()) throw new UsageError("--name must not be empty");
  if (values.build !== undefined && !/^\d{8}$/.test(values.build)) {
    throw new UsageError("--build must be a date as YYYYMMDD");
  }
  if (values["no-terrain"] && values["terrain-max-zoom"] !== undefined) {
    throw new UsageError("--terrain-max-zoom and --no-terrain do not go together");
  }
  return {
    event: values.event === undefined ? undefined : Number(values.event),
    bboxFile: values["bbox-file"],
    bbox,
    name: values.name,
    maxZoom: integerFlag(values, "max-zoom", 8, 15),
    terrainMaxZoom: integerFlag(values, "terrain-max-zoom", 8, 15),
    noTerrain: values["no-terrain"] ?? false,
    build: values.build,
    dryRun: values["dry-run"] ?? false,
    out: values.out ?? "out",
  };
}

// { command, options } from the arguments after the program name, or a UsageError.
export function parseCommand(argv) {
  const [command, ...args] = argv;
  try {
    switch (command) {
      case "build":
        return { command, options: parseBuild(args) };
      case "upload": {
        const { positionals } = parseArgs({ args, options: {}, strict: true, allowPositionals: true });
        if (positionals.length !== 1) throw new UsageError("upload takes one package directory");
        return { command, options: { dir: positionals[0] } };
      }
      case "list": {
        const { positionals } = parseArgs({ args, options: {}, strict: true, allowPositionals: true });
        if (positionals.length) throw new UsageError(`list takes no argument: ${positionals[0]}`);
        return { command, options: {} };
      }
      default:
        throw new UsageError(command ? `unknown command: ${command}` : "a command is required");
    }
  } catch (error) {
    if (error instanceof UsageError) throw error;
    throw new UsageError(error.message);
  }
}

// Runs one command; answers the exit code. deps: env, spawn, out, err, sleep, now.
export async function main(argv, deps = {}) {
  const {
    env = process.env,
    spawn = nodeSpawn,
    out = (line) => process.stdout.write(`${line}\n`),
    err = (line) => process.stderr.write(`${line}\n`),
  } = deps;
  let parsed;
  try {
    parsed = parseCommand(argv);
  } catch (error) {
    err(`wmsfo-tiles: ${error.message}`);
    err(USAGE);
    return 1;
  }
  let api;
  const getApi = () => (api ??= createApi({ ...apiSettings(env), sleep: deps.sleep }));
  const context = { ...deps, env, spawn, out, getApi };
  try {
    switch (parsed.command) {
      case "build": {
        const { runBuild } = await import("./lib/build.mjs");
        await runBuild(parsed.options, context);
        return 0;
      }
      case "upload":
        return await runUpload(parsed.options.dir, context);
      default:
        return await runList(context);
    }
  } catch (error) {
    err(`wmsfo-tiles: ${error instanceof ApiError ? formatApiError(error) : error.message}`);
    return 1;
  }
}

function invokedDirectly() {
  try {
    return realpathSync(process.argv[1]) === realpathSync(fileURLToPath(import.meta.url));
  } catch {
    return false;
  }
}

if (invokedDirectly()) {
  process.exitCode = await main(process.argv.slice(2));
}
