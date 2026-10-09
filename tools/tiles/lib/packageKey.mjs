// The package key (contracts 1.1): the lowercase hex SHA-256 of the canonical
// JSON {"bbox":{"west","south","east","north"},"minZoom","maxZoom","terrainMaxZoom"}
// in exactly that key order, numbers as JavaScript prints them, and
// terrainMaxZoom null for a package without terrain. The API computes the same
// key (TrackerThemeSeed.PackageKeyOf), so the CLI names the output directory
// and the API names the object prefix identically.
import { createHash } from "node:crypto";

export function canonicalIdentity({ bbox, minZoom, maxZoom, terrainMaxZoom }) {
  return JSON.stringify({
    bbox: { west: bbox.west, south: bbox.south, east: bbox.east, north: bbox.north },
    minZoom,
    maxZoom,
    terrainMaxZoom: terrainMaxZoom ?? null,
  });
}

export function packageKey(identity) {
  return createHash("sha256").update(canonicalIdentity(identity), "utf8").digest("hex");
}
