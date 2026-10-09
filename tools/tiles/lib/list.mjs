// `list`: GET /admin/maps as a table.

const known = (bytes) => bytes !== null && bytes !== undefined;

function megabytes(bytes) {
  return known(bytes) ? `${(bytes / 1048576).toFixed(1)} MB` : "-";
}

// The archives' sizes, "-" while none is recorded.
function sizes(m) {
  if (m.terrainMaxZoom === null || !(known(m.tilesBytes) || known(m.terrainBytes))) return megabytes(m.tilesBytes);
  return `${megabytes(m.tilesBytes)} + ${megabytes(m.terrainBytes)}`;
}

export function mapRows(items) {
  return items.map((m) => [
    String(m.id),
    m.name,
    m.state,
    `${m.bbox.west},${m.bbox.south},${m.bbox.east},${m.bbox.north}`,
    `${m.minZoom}-${m.maxZoom}${m.terrainMaxZoom === null ? "" : ` (${m.terrainMaxZoom})`}`,
    m.terrainMaxZoom === null ? "no" : "yes",
    sizes(m),
    String(m.eventCount),
  ]);
}

export function formatTable(header, rows) {
  const widths = header.map((h, i) => Math.max(h.length, ...rows.map((r) => r[i].length)));
  return [header, ...rows].map((r) => r.map((cell, i) => cell.padEnd(widths[i])).join("  ").trimEnd());
}

export async function runList({ getApi, out }) {
  const { body } = await getApi().request("GET", "/admin/maps");
  if (!body.items.length) {
    out("no maps");
    return 0;
  }
  const header = ["id", "name", "state", "box", "zooms", "terrain", "sizes", "events"];
  for (const line of formatTable(header, mapRows(body.items))) out(line);
  return 0;
}
