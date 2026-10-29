// The one-file bundle of the load scenario (docs/platform.md 1.9). The gateway
// stores a scenario as one script body and its runner writes it to a single
// file before `k6 run`, so the relative imports of scenario.js cannot resolve
// there. This joins scenario.js and the lib files it imports into one k6 file:
// the k6 imports once at the top, then each lib module in dependency order
// with its relative imports removed and its `export ` keywords stripped, then
// scenario.js the same way, keeping `export const options`, `export function
// setup`, and `export default function`.
//
// It is not a general bundler. It understands named imports from relative
// files (`import { a, b } from "./x.js"`), imports from k6 modules, and
// `export function`, `export const`, `export let`, `export class` at the start
// of a line. Anything else that imports or exports is an error naming the file
// and line, and so is a top-level name declared in two files, since the
// bundle shares one scope.
//
//   node tools/loadtest/bundle.mjs [--out <file>]
//
// writes the bundle to stdout, or to the file.

import { readFileSync, writeFileSync } from "node:fs";
import { dirname, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));

// The scenario exports k6 reads, kept as exports in the bundle.
const KEPT_EXPORTS = [/^export const options\b/, /^export function setup\b/, /^export default function\b/];

const IMPORT_FROM = /^import\s+([\s\S]*?)\s+from\s+["']([^"']+)["'];?\s*$/;
const NAMED = /^\{([\s\S]*)\}$/;
const IDENT = /^[A-Za-z_$][\w$]*$/;
const DECLARATION = /^(?:export\s+)?(?:async\s+)?(function\*?|const|let|var|class)\s*([\s\S]*)$/;

class BundleError extends Error {}

function fail(where, message) {
  throw new BundleError(`${where}: ${message}`);
}

function isRelative(spec) {
  return spec.startsWith("./") || spec.startsWith("../");
}

function isK6(spec) {
  return spec === "k6" || spec.startsWith("k6/");
}

// The names a top-level declaration line binds: `function f`, `class C`,
// `const X`, and the identifiers of a one-line `const { a, b: c } =` or
// `const [a, b] =`.
function declaredNames(kind, rest) {
  if (kind !== "const" && kind !== "let" && kind !== "var") {
    const m = /^([A-Za-z_$][\w$]*)/.exec(rest);
    return m ? [m[1]] : [];
  }
  const pattern = /^([{[])([^}\]]*)[}\]]/.exec(rest);
  if (!pattern) {
    const m = /^([A-Za-z_$][\w$]*)/.exec(rest);
    return m ? [m[1]] : [];
  }
  return pattern[2]
    .split(",")
    .map((part) => part.split("=")[0].split(":").pop().replace(/^\.\.\./, "").trim())
    .filter((name) => IDENT.test(name));
}

// The bound names of an import clause; only `{ a, b }` is understood.
function namedImports(clause, where) {
  const m = NAMED.exec(clause.trim());
  if (!m) fail(where, `unsupported import \`${clause.trim()}\`: only named imports \`{ a, b }\` are understood`);
  const names = [];
  for (const part of m[1].split(",")) {
    const name = part.trim();
    if (name === "") continue;
    if (!IDENT.test(name)) fail(where, `unsupported import specifier \`${name}\`: renaming is not understood`);
    names.push(name);
  }
  return names;
}

// The bound names of a k6 import clause: `x`, `{ a, b as c }`, or `x, { a }`.
function k6Names(clause, where) {
  const m = /^(?:([A-Za-z_$][\w$]*)\s*,?\s*)?(?:\{([\s\S]*)\})?$/.exec(clause.trim());
  if (!m || (!m[1] && m[2] === undefined)) fail(where, `unsupported import \`${clause.trim()}\``);
  const names = m[1] ? [m[1]] : [];
  for (const part of (m[2] ?? "").split(",")) {
    const name = part.trim().split(/\s+as\s+/).pop();
    if (name) names.push(name);
  }
  return names;
}

// One file split into its statements of interest. Lines are kept as they are
// except for the import statements, which are taken out (an import may span
// lines until its `from "..."`).
function parse(path, root) {
  const name = relative(root, path).split("\\").join("/");
  const lines = readFileSync(path, "utf8").replace(/\r\n/g, "\n").split("\n");
  const file = { path, name, body: [], k6Imports: [], deps: [], declared: [] };

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const where = `${name}:${i + 1}`;
    const trimmed = line.trim();

    if (!trimmed.startsWith("//") && /\bimport\s*\(/.test(line)) fail(where, "dynamic import() is not understood");

    if (/^import\b/.test(line)) {
      let statement = line;
      let end = i;
      while (!/\bfrom\s+["'][^"']+["'];?\s*$/.test(statement) && !/^import\s+["']/.test(statement) && end + 1 < lines.length) {
        end += 1;
        statement += "\n" + lines[end];
      }
      if (/^import\s+["']/.test(statement)) fail(where, "a side-effect import is not understood");
      const m = IMPORT_FROM.exec(statement);
      if (!m) fail(where, "an import statement that is not understood");
      const [, clause, spec] = m;
      if (/^\*\s*as\b/.test(clause.trim())) fail(where, `\`import * as\` is not understood: \`${statement.trim()}\``);
      if (isK6(spec)) {
        file.k6Imports.push({ text: statement.trim(), where });
      } else if (isRelative(spec)) {
        const names = namedImports(clause, where);
        file.deps.push({ path: resolve(dirname(path), spec), names, where });
      } else {
        fail(where, `import from \`${spec}\` is neither relative nor k6`);
      }
      i = end;
      continue;
    }

    if (/^export\b/.test(line)) {
      if (/^export\s*\{/.test(line) || /^export\s*\*/.test(line)) fail(where, `\`${trimmed}\` is not understood: export declarations only`);
      if (/^export\s+default\b/.test(line) && !/^export default function\b/.test(line)) {
        fail(where, "only `export default function` is understood");
      }
    }

    if (/^\S/.test(line)) {
      const d = DECLARATION.exec(line);
      if (d) {
        for (const declared of declaredNames(d[1], d[2])) file.declared.push({ name: declared, where });
      }
    }
    file.body.push({ text: line, where });
  }
  return file;
}

function exportedNames(file) {
  const names = new Set();
  for (const { text: line } of file.body) {
    const d = /^export\s+(?:async\s+)?(function\*?|const|let|var|class)\s*([\s\S]*)$/.exec(line);
    if (d) for (const name of declaredNames(d[1], d[2])) names.add(name);
  }
  return names;
}

// The body of `file` with its `export ` keywords stripped, the kept exports
// of the entry left alone.
function stripExports(file, isEntry) {
  return file.body.map(({ text: line, where }) => {
    if (!/^export\b/.test(line)) return line;
    if (isEntry && KEPT_EXPORTS.some((re) => re.test(line))) return line;
    if (/^export default\b/.test(line)) fail(where, "`export default function` is understood in the entry only");
    return line.replace(/^export\s+/, "");
  });
}

// The files reachable from `entry`, each once, every file after the files it
// imports; the entry is last.
function graph(entry, root) {
  const order = [];
  const seen = new Map();
  const visiting = new Set();
  const visit = (path, from) => {
    if (seen.has(path)) return seen.get(path);
    if (visiting.has(path)) fail(from, `circular import of ${relative(root, path)}`);
    visiting.add(path);
    let file;
    try {
      file = parse(path, root);
    } catch (err) {
      if (err instanceof BundleError) throw err;
      fail(from, `cannot read ${relative(root, path)}: ${err.message}`);
    }
    for (const dep of file.deps) {
      const target = visit(dep.path, dep.where);
      const exported = exportedNames(target);
      for (const name of dep.names) {
        if (!exported.has(name)) fail(dep.where, `${target.name} does not export \`${name}\``);
      }
    }
    visiting.delete(path);
    seen.set(path, file);
    order.push(file);
    return file;
  };
  visit(entry, relative(root, entry));
  return order;
}

// The bundle text of `entry` (default scenario.js beside this file). Throws
// with the file and line on anything it does not understand.
export function bundle(entry = resolve(here, "scenario.js")) {
  const root = dirname(entry);
  const files = graph(resolve(entry), root);

  const owners = new Map();
  const claim = (name, where) => {
    const owner = owners.get(name);
    if (owner && owner.split(":")[0] !== where.split(":")[0]) {
      fail(where, `\`${name}\` is declared in both ${owner} and ${where}; the bundle shares one scope`);
    }
    if (!owner) owners.set(name, where);
  };

  const k6Lines = [];
  for (const file of files) {
    for (const k6 of file.k6Imports) {
      if (k6Lines.includes(k6.text)) continue;
      k6Lines.push(k6.text);
      for (const name of k6Names(IMPORT_FROM.exec(k6.text)[1], k6.where)) claim(name, k6.where);
    }
  }
  for (const file of files) for (const d of file.declared) claim(d.name, d.where);

  const entryFile = files[files.length - 1];
  const out = [
    `// Built by tools/loadtest/bundle.mjs from ${files.map((f) => f.name).join(", ")}.`,
    "// A build artifact for registration with the gateway: edit the sources, not this file.",
    "",
    ...k6Lines,
  ];
  for (const file of files) {
    out.push("", `// ---- ${file.name} ----`, "");
    const body = stripExports(file, file === entryFile);
    while (body.length && body[0].trim() === "") body.shift();
    while (body.length && body[body.length - 1].trim() === "") body.pop();
    out.push(...body);
  }
  return out.join("\n") + "\n";
}

function main(argv) {
  let outPath = null;
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === "--out" && i + 1 < argv.length) outPath = argv[++i];
    else {
      console.error("usage: node tools/loadtest/bundle.mjs [--out <file>]");
      process.exit(2);
    }
  }
  let text;
  try {
    text = bundle();
  } catch (err) {
    if (!(err instanceof BundleError)) throw err;
    console.error(`bundle: ${err.message}`);
    process.exit(1);
  }
  if (outPath) writeFileSync(outPath, text);
  else process.stdout.write(text);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main(process.argv.slice(2));
