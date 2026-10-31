# Ofertas desde el artículo y marca de ofertas en la búsqueda del POS

## Objective

1. Inside the article edit modal, an "Ofertas" section to list, create, edit and delete the offers that target that article or its family, without leaving the article.
2. In the POS article search (F2), a mark on every result that has live offers; hovering or focusing it shows those offers with their condition.

## Problem / why

Owner request 2026-10-31. Today offers are managed only in `/ofertas`, a separate screen with no filter by article; to see or change the offers of one article the user has to search the whole list. At the till, the cashier cannot tell which articles have an offer before adding them, and quantity offers ("llevando 3") never show up because the search resolves each row at quantity 1.

## Scope

- Backend: filter `GET /api/ofertas` by article (direct + family offers) and a new read `POST /api/ofertas/vigentes` that returns the live offers per article for the POS search, including quantity offers.
- Web, article modal: an "Ofertas" section (edit mode only, catalog managers only), stacked like the other `Gestor*` sections. The modal has no tabs today; the section follows the existing layout instead of introducing tabs for a single feature. The offer form is extracted from `paginas/Ofertas.tsx` so both screens share it.
- Web, POS search: badge + tooltip on `ModalDeBusquedaDeArticulos` rows.

Out of scope: offline search (the F2 search is online-only today), new offer types, combos, the offline snapshot.

## Contract

- `GET /api/ofertas?idArticulo={id}` → `200 OfertaListado[]` with the non-deleted offers (active or not, any validity) whose scope is that article, plus the ones whose scope is the article's family when it has one. Unknown, soft-deleted or other-tenant article → `200 []`. Without `idArticulo` the endpoint is unchanged. Policy unchanged (`OperacionDePos`).
- `POST /api/ofertas/vigentes` body `{ idsArticulos: number[] (1..200, distinct), idEmpresa: number, idListaPrecio?: number, momento?: DateTimeOffset }` → `200 [{ idArticulo, ofertas: [{ idOferta, nombre, alcance: "Articulo"|"Grupo"|"Categoria"|"Familia", cantidadMinima?, soloMultiplos, porcentaje?, precioUnitario?, importeFijo?, fechaHasta?, acumulable }] }]`. Only active offers matching the article by any scope, the empresa (or tenant-wide), the list targeting (when `idListaPrecio` is given) and the moment (dates, hours, weekdays, in the empresa's zone), ignoring quantity. Articles with no offers are omitted. 400 on empty/oversized/invalid ids. Policy `OperacionDePos`.

## Data model

No schema change. `ofertas` / `ofertas_listas` (doc 10) already hold everything.

## Tasks

- [x] T1 — Backend: `idArticulo` filter + `POST /ofertas/vigentes` + unit and integration tests. Route: delegated direct (writer trigger: service, endpoint, contracts, tests).
  - Evidence: `dotnet build` 0 errors; Domain.Tests 2113 passed; Application.Tests 1516 passed; full IntegrationTests 5782 passed (includes `SuperficieDeAutorizacionTests`, `AislamientoDeTenantTests`; allowlist gained `POST /api/ofertas/vigentes`).
  - Design: `GET /api/ofertas` takes `idArticulo` (`ListarAsync(incluirEliminados, idArticulo, ct)`); `ListarVigentesAsync` reuses new shared `CargarAlcancesAsync` / `CargarCandidatasAsync` (extracted from `ConstruirContextoAsync`) and `ResolvedorDeOfertas.CoincideSinCantidad` (extracted from `Coincide`). Error codes: `ids_articulos_requeridos`, `ids_articulos_excedidos`, `ids_articulos_invalidos`, `empresa_requerida`. Empresa filtering is the SQL clause of `CargarCandidatasAsync` (exact for one empresa).
  - Mutation evidence (each mutated, red observed in `OfertasVigentesTests`, reverted): drop family clause of the article filter -> `ElFiltroPorArticuloDevuelveLasDirectasYLasDeSuFamiliaYNadaMas`; `considerarLista: true` -> `VigentesFiltraPorListaSoloCuandoSePideUna...`; empresa SQL clause -> `true` -> `VigentesExcluyeInactivasDadasDeBajaYDeOtraEmpresa...`; remove weekday clause and, separately, the `HoraHasta` clause -> `VigentesRespetaLaVentanaDeFechasHorasYDias...`; remove `o.Activo` -> `VigentesExcluyeInactivas...`.
  - Note: the hour/weekday test derives its fixtures from the server local zone (`TimeZoneInfo.Local`), like the resolver; the "antes/despues de hora" fixtures are skipped only if the local hour is outside 2..20.
- [ ] T2 — Web: extract the offer modal, "Ofertas" section in the article modal + Vitest tests. Route: delegated direct (writer trigger).
- [x] T3 — Web POS: offers badge and tooltip in the F2 search + Vitest tests. Route: delegated direct (writer trigger). Evidence: `tsc -b` clean; lint 0 errors; full vitest 9061/9062 (the known `GrillaDeItemsDeCompra.test.tsx` load flake, 134/134 alone); after the polish commit the three touched suites 43/43; mutation evidence: removing the generation gate in `cargarVigentes` reds the stale-response test, and always stopping Escape reds the hover-only Escape test. Judgment day: `APPROVED` round 1, no CRITICAL; applied the shared suggestion (decimal comma and Spanish "de descuento") plus judge B's (Escape with only the mouse over a badge also closes the search; family offers say "de la familia"). Info kept: one Tab stop per badge; ids beyond 200 get no badge.

## Acceptance criteria

- An Admin opens an article, sees its offers and its family's, creates one preset to the article or to the family, edits and deletes it; the list refreshes.
- A Vendedor does not see the section (the writes are 403 anyway).
- In the POS F2 search, an article with an offer valid now shows the mark; a quantity offer shows its condition ("Llevando 3: 10%"); an offer outside its hours or of another empresa or list does not.
- If `/ofertas/vigentes` fails, the search still works, without marks.
- Async reads gated and writes guarded (`react-async-state`); tests wait for data (`web-test-data-gates`); request fields all used (`dto-contract-honesty`); structural guards pass (`structural-guard-tests`).

## Checks

- `dotnet build`, `dotnet test tests/Ways.Domain.Tests`, `tests/Ways.Application.Tests`, full `tests/Ways.IntegrationTests` (includes `SuperficieDeAutorizacionTests`, `AislamientoDeTenantTests`).
- `npm run lint`, `npx tsc -b`, `npx vitest run` in `src/Ways.Web`.

## Delivery

Forecast ~1500 authored lines: over budget; cached chain strategy `stacked-to-main`. Slice 1 = T1 (backend PR), slice 2 = T2 (stacked on 1), slice 3 = T3 (stacked on 1).

## Progress

- 2026-10-31: exploration done; owner chose "all live offers, including quantity offers" for the POS mark. Worktree `.claude/worktrees/ofertas-desde-articulo`, branch `pnader/ofertas-desde-articulo` from origin/main f2ea9def.

## Next step

T2 and T3 (web), both stacked on the T1 PR.

## Judgment day

- T1 (2026-10-31): `JUDGMENT: APPROVED` in round 1, no CRITICAL from either judge. Fixed as doc-only follow-up: duplicated `<summary>` on `ListarAsync` (both judges) and the contract wording "live" for `GET ?idArticulo` (it returns active and inactive, any validity: the section is an ABM). Info kept: with `incluirEliminados=true` and a soft-deleted article the filter still yields `[]` (judge A, suggestion).
