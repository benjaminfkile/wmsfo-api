// The package's box, from exactly one source: an event's trackerBbox through
// GET /admin/events/{id} (the event's name is the default name), a file a
// panel box editor exported ({ name, bbox, maxZoom, terrainMaxZoom }), or the
// --bbox west,south,east,north flag. Each side is 0.05 to 20 degrees.
import { readFile } from "node:fs/promises";

export const MIN_SIDE = 0.05;
export const MAX_SIDE = 20;

// The reason a box is not acceptable, or null.
export function bboxProblem(bbox) {
  if (!bbox || typeof bbox !== "object") return "the box must be { west, south, east, north }";
  const { west, south, east, north } = bbox;
  for (const [name, value] of Object.entries({ west, south, east, north })) {
    if (typeof value !== "number" || !Number.isFinite(value)) return `${name} must be a number`;
  }
  if (west < -180 || east > 180) return "west and east must be within -180 to 180";
  if (south < -90 || north > 90) return "south and north must be within -90 to 90";
  if (!(west < east)) return "west must be less than east";
  if (!(south < north)) return "south must be less than north";
  for (const [name, side] of [["east minus west", east - west], ["north minus south", north - south]]) {
    if (side < MIN_SIDE || side > MAX_SIDE) return `${name} must be ${MIN_SIDE} to ${MAX_SIDE} degrees`;
  }
  return null;
}

// "west,south,east,north" as a box, or an Error.
export function parseBboxFlag(text) {
  const parts = String(text).split(",").map((s) => s.trim());
  if (parts.length !== 4 || parts.some((p) => p === "" || !Number.isFinite(Number(p)))) {
    throw new Error("--bbox must be four numbers: west,south,east,north");
  }
  const [west, south, east, north] = parts.map(Number);
  const bbox = { west, south, east, north };
  const problem = bboxProblem(bbox);
  if (problem) throw new Error(`--bbox: ${problem}`);
  return bbox;
}

// { name, bbox, maxZoom, terrainMaxZoom } with the zooms undefined when the
// source does not name them. options carries event, bboxFile, bbox (already
// parsed), and name; getApi is called only for an event.
export async function resolveBox(options, { getApi, read = readFile } = {}) {
  let name;
  let bbox;
  let maxZoom;
  let terrainMaxZoom;
  if (options.event !== undefined) {
    const { body } = await getApi().request("GET", `/admin/events/${options.event}`);
    bbox = body?.trackerBbox;
    name = body?.name;
    const problem = bboxProblem(bbox);
    if (problem) throw new Error(`event ${options.event} trackerBbox: ${problem}`);
  } else if (options.bboxFile !== undefined) {
    let file;
    try {
      file = JSON.parse(await read(options.bboxFile, "utf8"));
    } catch (error) {
      throw new Error(`--bbox-file ${options.bboxFile}: ${error.message}`);
    }
    bbox = file?.bbox;
    name = typeof file?.name === "string" && file.name.trim() ? file.name : undefined;
    const problem = bboxProblem(bbox);
    if (problem) throw new Error(`--bbox-file ${options.bboxFile}: ${problem}`);
    if (file.maxZoom !== undefined) maxZoom = file.maxZoom;
    if (file.terrainMaxZoom !== undefined) terrainMaxZoom = file.terrainMaxZoom;
  } else {
    bbox = options.bbox;
  }
  if (options.name !== undefined) name = options.name;
  if (!name) throw new Error("--name is required: the box source names no map");
  return {
    name,
    bbox: { west: bbox.west, south: bbox.south, east: bbox.east, north: bbox.north },
    maxZoom,
    terrainMaxZoom,
  };
}
