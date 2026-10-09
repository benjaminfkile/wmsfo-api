// The go-pmtiles binary, the CLI's one external dependency. Every call goes
// through an injected spawn (node:child_process spawn by default) with stdio
// inherited, so the binary's progress reaches the operator's terminal.

export const INSTALL_LINE =
  "pmtiles not found on PATH: install go-pmtiles (brew install pmtiles, or a release binary from https://github.com/protomaps/go-pmtiles/releases saved as pmtiles on PATH)";

// Resolves to the exit code; rejects when the binary cannot be started.
export function run(spawn, args, stdio = "inherit") {
  return new Promise((resolve, reject) => {
    let child;
    try {
      child = spawn("pmtiles", args, { stdio });
    } catch (error) {
      reject(error);
      return;
    }
    child.once("error", reject);
    child.once("close", (code) => resolve(code ?? 1));
  });
}

// Throws the install line unless `pmtiles version` runs and exits 0.
export async function checkPmtiles(spawn) {
  let code;
  try {
    code = await run(spawn, ["version"], "ignore");
  } catch {
    code = -1;
  }
  if (code !== 0) throw new Error(INSTALL_LINE);
}

export function extractArgs({ source, output, bbox, maxZoom, dryRun }) {
  const args = [
    "extract",
    source,
    output,
    `--bbox=${bbox.west},${bbox.south},${bbox.east},${bbox.north}`,
    `--maxzoom=${maxZoom}`,
  ];
  if (dryRun) args.push("--dry-run");
  return args;
}

export function convertArgs({ input, output }) {
  return ["convert", input, output];
}

async function runOrThrow(spawn, args) {
  const code = await run(spawn, args);
  if (code !== 0) throw new Error(`pmtiles ${args[0]} exited with ${code}`);
}

export const extract = (spawn, options) => runOrThrow(spawn, extractArgs(options));
export const convert = (spawn, options) => runOrThrow(spawn, convertArgs(options));
