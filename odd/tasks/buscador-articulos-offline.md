# Buscador de artículos del POS offline-first

## Objective

The POS article search modal (`ModalDeBusquedaDeArticulos`) searches the local offline snapshot first and only falls back to the server when there is no snapshot, the same way the combo search does (`FuenteDeCombos`).

## Problem

The modal always calls `GET /articulos?busqueda=…&soloVendibles=true` and `POST ofertas/resolver` for the price preview. Without network it shows "No se pudo buscar artículos.", and with a slow network it is the only part of the sale flow that waits for the server. Scanning and combos already work from the snapshot.

## Scope

- A search source for articles with `origen: 'instantanea' | 'api'`, modeled on `src/Ways.Web/src/pos/fuenteDeCombos.ts`.
- Local text search over the fields the snapshot holds, and the price preview from the same offline engine the scan uses.
- Wiring in `ModalDeBusquedaDeArticulos.tsx` and `paginas/Pos.tsx`.
- Colocated Vitest tests.

Out of scope: backend changes, snapshot schema changes, POS release (a separate `pos-v*` tag after merge).

## Constraints

- Server search can match fields the snapshot lacks (e.g. supplier codes). The local search matches what the snapshot has; this difference is accepted and documented in the source.
- Keep `motivoSinAgregar`, `onAgregar` and the async gating of the modal (`react-async-state`).

## Tasks

- [x] T1 — Article search source (snapshot | API), modal and POS wiring, tests. Route: delegated (writer trigger: 2+ non-trivial files). Commit 3c1b060b.

## Acceptance criteria

- With a snapshot loaded, typing in the modal makes no request to `/articulos` or `ofertas/resolver`.
- Without a snapshot, the modal behaves as today.
- The previewed price matches what the scan resolves offline for the same article and list.

## Checks

- `npx vitest run` in `src/Ways.Web` (full web suite), `npx tsc -b`, `npm run lint`.

## Progress

- Branch `pnader/buscador-articulos-offline` from `origin/main` 5507f8c9.
- RDD off (clone-local); PR gate is judgment-day.

- T1 checks: tsc -b clean; lint clean on touched files; vitest full 10075/10076 (1 failure in untouched CompraEditor/GrillaDeItemsDeCompra area, passes alone); targeted 35/35.
- judgment-day: APPROVED, no severe findings. Info: offer marks hidden when a snapshot exists (even online); preview prices from the snapshot even for a client the cart would price online (not admitted offline or list missing shows "—"); top-25 order uses localeCompare vs DB collation.

## Next step

Merge, then POS release (Ways.Desktop bump + pos-v* tag). Possible follow-up: price and offer marks via API when the cart would go online.
