// `upload`: register the package with POST /admin/maps, send each declared
// archive as a multipart upload in 64 MB parts (four PUTs in flight, each
// retried three times, part URLs signed in batches of at most 100), complete
// it, then confirm. A pending row with the same key is reused by the API, so
// running again after a failure resumes on the same row.
import { open, readFile, stat } from "node:fs/promises";
import { join } from "node:path";
import { ApiError } from "./api.mjs";
import { pool } from "./pool.mjs";

export const PART_SIZE = 64 * 1024 * 1024;
export const PART_BATCH = 100;
export const PUT_CONCURRENCY = 4;
export const PUT_RETRIES = 3;
// Part URLs live 15 minutes; a batch older than this is signed again.
export const URL_REFRESH_MS = 10 * 60 * 1000;

const defaultSleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

// [{ partNumber, start, length }]: PART_SIZE each, the last one shorter.
export function partPlan(size, partSize = PART_SIZE) {
  const parts = [];
  for (let start = 0, partNumber = 1; start < size; start += partSize, partNumber++) {
    parts.push({ partNumber, start, length: Math.min(partSize, size - start) });
  }
  return parts;
}

// The part numbers in groups of at most PART_BATCH, in order.
export function batches(partNumbers, size = PART_BATCH) {
  const groups = [];
  for (let i = 0; i < partNumbers.length; i += size) groups.push(partNumbers.slice(i, i + size));
  return groups;
}

// The archives a manifest declares, as [file, filename].
export const archivesOf = (manifest) =>
  manifest.terrainMaxZoom === null || manifest.terrainMaxZoom === undefined
    ? [["tiles", "tiles.pmtiles"]]
    : [["tiles", "tiles.pmtiles"], ["terrain", "terrain.pmtiles"]];

export async function readManifest(dir) {
  let manifest;
  try {
    manifest = JSON.parse(await readFile(join(dir, "manifest.json"), "utf8"));
  } catch (error) {
    throw new Error(`${join(dir, "manifest.json")}: ${error.message}`);
  }
  for (const field of ["name", "bbox", "minZoom", "maxZoom", "packageKey"]) {
    if (manifest[field] === undefined) throw new Error(`manifest.json has no ${field}`);
  }
  return manifest;
}

// PUTs one part's bytes; answers its ETag.
async function putPart(url, bytes, sleep) {
  let lastError;
  for (let attempt = 0; attempt <= PUT_RETRIES; attempt++) {
    if (attempt > 0) await sleep(1000 * 2 ** (attempt - 1));
    try {
      const response = await globalThis.fetch(url, { method: "PUT", body: bytes });
      if (response.ok) {
        const etag = response.headers.get("etag");
        if (etag) return etag;
        lastError = new Error("the part answer carried no ETag");
      } else {
        lastError = new Error(`HTTP ${response.status}`);
      }
    } catch (error) {
      lastError = error;
    }
  }
  throw lastError;
}

// Sends one archive and completes its upload.
async function uploadArchive(api, id, file, path, { out, sleep, now }) {
  const { size } = await stat(path);
  const parts = partPlan(size);
  out(`${file}: ${size} bytes in ${parts.length} part${parts.length === 1 ? "" : "s"}`);
  const groups = batches(parts.map((p) => p.partNumber));
  const signed = new Map();

  // The URL of a part, signing its batch when it has none or it is old.
  async function urlOf(partNumber) {
    const group = groups[Math.floor((partNumber - 1) / PART_BATCH)];
    let entry = signed.get(group);
    if (!entry || now() - entry.at > URL_REFRESH_MS) {
      entry = {
        at: now(),
        urls: api.request("POST", `/admin/maps/${id}/parts`, { file, partNumbers: group }).then(({ body }) =>
          new Map(body.parts.map((p) => [p.partNumber, p.url]))),
      };
      signed.set(group, entry);
    }
    return (await entry.urls).get(partNumber);
  }

  const etags = new Map();
  const handle = await open(path, "r");
  let sent = 0;
  try {
    await pool(parts, PUT_CONCURRENCY, async (part) => {
      const url = await urlOf(part.partNumber);
      const bytes = Buffer.alloc(part.length);
      await handle.read(bytes, 0, part.length, part.start);
      try {
        etags.set(part.partNumber, await putPart(url, bytes, sleep));
      } catch (error) {
        throw new Error(`${file} part ${part.partNumber}: ${error.message}`);
      }
      sent++;
      out(`${file}: part ${part.partNumber} sent (${sent} of ${parts.length})`);
    });
  } finally {
    await handle.close();
  }
  await api.request("POST", `/admin/maps/${id}/complete`, {
    file,
    parts: parts.map((p) => ({ partNumber: p.partNumber, etag: etags.get(p.partNumber) })),
  });
  out(`${file}: complete`);
}

// Answers the exit code. deps: getApi, env, out, sleep, now.
export async function runUpload(dir, deps) {
  const { getApi, env, out, sleep = defaultSleep, now = Date.now } = deps;
  const manifest = await readManifest(dir);
  const archives = archivesOf(manifest);
  for (const [, filename] of archives) {
    await stat(join(dir, filename)).catch(() => {
      throw new Error(`${join(dir, filename)} is missing`);
    });
  }
  const api = getApi();

  let created;
  try {
    created = await api.request("POST", "/admin/maps", {
      name: manifest.name,
      bbox: manifest.bbox,
      minZoom: manifest.minZoom,
      maxZoom: manifest.maxZoom,
      terrainMaxZoom: manifest.terrainMaxZoom ?? null,
      sourceBuild: manifest.sourceBuild ?? null,
    });
  } catch (error) {
    if (error instanceof ApiError && error.code === "package_exists") {
      const { body } = await api.request("GET", "/admin/maps");
      const existing = body.items.find((m) => m.packageKey === manifest.packageKey && m.state === "ready");
      out(existing
        ? `already uploaded: map ${existing.id} "${existing.name}" is ready with package ${existing.packageKey}`
        : `already uploaded: a ready map has package ${manifest.packageKey}`);
      return 0;
    }
    throw error;
  }
  const { id } = created.body;
  out(`map ${id} ${created.status === 201 ? "created" : "resumed"}, package ${created.body.packageKey}`);
  if (created.body.packageKey !== manifest.packageKey) {
    throw new Error(`the API computed package ${created.body.packageKey}, the manifest says ${manifest.packageKey}`);
  }

  for (const [file, filename] of archives) {
    await uploadArchive(api, id, file, join(dir, filename), { out, sleep, now });
  }

  let confirmed;
  try {
    confirmed = await api.request("POST", `/admin/maps/${id}/confirm`);
  } catch (error) {
    if (error instanceof ApiError && error.code === "package_invalid") {
      out(`package invalid: ${error.details?.field}: ${error.details?.reason}`);
      return 1;
    }
    throw error;
  }
  const map = confirmed.body;
  out(`map ${map.id} is ${map.state}`);
  const panel = env.WMSFO_PANEL_BASE_URL?.replace(/\/+$/, "");
  out(panel ? `${panel}/maps` : `map ${map.id} "${map.name}"`);
  return 0;
}
