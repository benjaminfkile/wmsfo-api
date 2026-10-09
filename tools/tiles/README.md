# wmsfo-tiles

The tile package CLI (docs/platform.md 1.8): builds a tracker map package on an operator's machine, uploads it through the admin API, and lists the maps. Node 22.13 or later, no npm dependency, and one external binary, `pmtiles` (go-pmtiles), on `PATH`.

Install go-pmtiles with `brew install pmtiles`, or download a release binary from https://github.com/protomaps/go-pmtiles/releases and put it on `PATH` as `pmtiles`. The CLI runs `pmtiles version` first and prints this install line when it fails.

## Variables

| Variable | Used by | Value |
|---|---|---|
| `WMSFO_API_BASE_URL` | `build --event`, `upload`, `list` | `https://<api-domain>` |
| `WMSFO_API_KEY` | `build --event`, `upload`, `list` | `wak_...`, an API key with the `maps` capability, and `events` as well for `build --event` |
| `WMSFO_TILES_TERRAIN_URL` | `build` unless `--no-terrain` | `<elevation-tiles-url>`, a terrarium tile template ending in `{z}/{x}/{y}.png` |
| `WMSFO_PANEL_BASE_URL` | `upload` (optional) | `https://<admin-domain>`; the last line of an upload is `<WMSFO_PANEL_BASE_URL>/maps` when set, the map id and name otherwise |

## Commands

Run from this directory (`node cli.mjs ...`), or `npm link` once for `wmsfo-tiles` on `PATH`.

```sh
# build from an event's box (read through the API), from a file a panel box editor exported, or from a flag
wmsfo-tiles build --event 41 [--name <name>] [--no-terrain] [--max-zoom 15] [--terrain-max-zoom 13] [--build 20261001] [--dry-run] [--out out]
wmsfo-tiles build --bbox-file ./missoula.json [--name "Missoula valley"] ...
wmsfo-tiles build --bbox -114.75,46.35,-113.30,47.25 --name "Missoula valley" ...

# upload the package, confirm it, print the panel URL of the new map
wmsfo-tiles upload ./out/<packageKey>

# the maps the API knows
wmsfo-tiles list
```

`build` takes exactly one box source; each side of the box is 0.05 to 20 degrees. It probes `https://build.protomaps.com/YYYYMMDD.pmtiles` from today back seven days (or uses `--build`), runs `pmtiles extract`, fetches the terrain tiles eight at a time into `terrain.mbtiles` and runs `pmtiles convert` (unless `--no-terrain`), writes `manifest.json` beside the archives under `<out>/<packageKey>/`, and prints the package key last. `--dry-run` passes `--dry-run` to `pmtiles extract` and stops.

`upload` sends each archive in 64 MB parts, four in flight with three retries each, completes it, and confirms the map. Running it again after a failure resumes on the same pending row. `409 package_exists` prints the ready map that already has the package and exits 0; `409 package_invalid` prints the failed archive and check and exits 1.

## Tests

```sh
node --test tools/tiles   # from the repository root, or npm test here
```

The tests are offline: `fetch` is replaced on `globalThis` and the `pmtiles` spawn is injected.
