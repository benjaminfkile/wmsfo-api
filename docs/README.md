# Design documents

This folder holds the originals of the two documents shared by every WMSFO v2 repository:

- `DESIGN.md`: the design overview.
- `contracts.md`: the shared contracts. Wins on any conflict with another document.

And the documents that belong to this repository and the platform around it: `api.md`, `sql.md`, `platform.md`, `reference/legacy-schema.md`.

`santa/docs`, `wmsfo-admin-panel/docs`, and `red-nose/docs` carry byte-identical copies of `DESIGN.md` and `contracts.md`. When either changes here, recopy it to all three; a copy is never edited in place.

The per-component designs live with their code: `site.md` in `santa`, `admin.md` in `wmsfo-admin-panel`, `red-nose.md` in `red-nose`.
