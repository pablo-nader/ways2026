# Proposal: Stage 4 — Ofertas

## Intent

Implement Etapa 4 of `docs/10-modelo-de-datos.md`: the `ofertas` engine. Doc 10
(§ stage sequence) marks stage 4 as what turns the catalog into a "POS completo" —
stage 3 made a price resolvable, stage 4 makes the *selling* price resolvable.

Two problems are being solved at once:

1. **The legacy has offers but no rules.** In `alsina/`, item-level offers
   (`OfertaDia`/`OfertaHora`/`OfertaCant` on `articulos`) and group-level offers
   (`comprobarOfertaGrupo`) fire independently, and precedence between them is
   *accidental*: last-write-wins between the date and quantity blocks, `elseif`
   exclusivity inside the group path. Nobody can explain to a tenant why a given
   line got a given price. Stage 4 replaces that with explicit, deterministic,
   testable precedence.
2. **Price resolution is already a documented N+1** (`ServicioDePrecios.PreciosVigentesAsync`,
   with an INFO doc-comment deferring the fix to the POS stage). Offer resolution
   sits on top of price resolution, so building it per-article would double the
   debt right before stage 5 hits it with a full cart. Stage 4 therefore ships
   resolution **batch-first**.

## Scope

### In Scope

- **`ofertas` table**, full doc-10 shape, catálogo scope (`id_tenant` +
  `id_empresa NULL` = tenant-wide, per doc 09 §84): `nombre citext` (the ticket
  label), exclusive scope `CHECK num_nonnulls(id_articulo, id_grupo, id_categoria) = 1`,
  vigencia windows (`fecha_desde/hasta`, `hora_desde/hasta`, `dias_semana smallint[]`
  `{1..7}`, NULL = todos), `cantidad_minima` (NULL = oferta directa), exclusive
  benefit `CHECK num_nonnulls(precio_unitario, porcentaje, importe_fijo) = 1`,
  `prioridad`, `acumulable`, `activo`.
- **`ofertas_listas` junction** — an oferta may target MULTIPLE listas. Zero rows
  = applies to every lista (including `derivada` ones, where the benefit computes
  over the lista's already-resolved price); rows present = only those listas.
  This **replaces** doc 10's single `id_lista_precio NULL` column (see decision 4).
- **Deterministic resolution capability, query-only and batch-first**: one call
  resolves N `(articulo, cantidad, lista, momento)` inputs and returns, per input,
  the final unit price plus which ofertas were applied. No comprobante is written.
- **Precedence** (decision 1): among matching ofertas, the highest-`prioridad`
  **non-acumulable** oferta wins as the base benefit; then **every** matching
  `acumulable = true` oferta stacks on top of that result.
- **CRUD/ABM**: dedicated Ofertas screen with articulo / grupo / categoria / lista
  pickers and an optional empresa picker.
- **`docs/10-modelo-de-datos.md` update** recording the `ofertas_listas` deviation
  (doc 10 is the definitive schema per CLAUDE.md — it must not drift).

### Out of Scope

- `items_comprobante_venta` (`descuento` + `id_oferta` snapshot) — stage 5 write path.
- POS cart / ticket rendering, including the default offer-label output and the
  legacy phantom `OF...` line representation — stage 5.
- Negative-quantity / returns semantics for offers — stage 5.
- `stock` / `movimientos_stock` — stage 5.
- Legacy offer data migration.

## Capabilities

### New Capabilities

- `ofertas`: oferta CRUD/ABM, exclusive scope model (articulo | grupo | categoria),
  vigencia windows (fecha / hora / dias_semana), exclusive benefit model
  (precio_unitario | porcentaje | importe_fijo), `cantidad_minima` trigger,
  multi-lista targeting via `ofertas_listas`, `prioridad` / `acumulable` / `activo`.
- `resolucion-de-ofertas`: deterministic precedence + stacking algorithm, batch
  resolution of many articulos per call, query-only (no comprobante writes).

### Modified Capabilities

- `precios`: gains a **batch** current-price resolution path (many articulos ×
  listas in one query) that offer resolution consumes, closing the deliberate
  N+1 flagged in `ServicioDePrecios.PreciosVigentesAsync`. History, close-and-open
  and date-query semantics are **unchanged**.

## Approach

1. **Reuse, not redesign.** `EntidadTenant`, RLS helper, `ManejadorDeErrores`
   mapping contract, migration/testing patterns from stages 1–3. The generic
   `ServicioDeCatalogo<T,TListado,TAlta>` base is likely **not** a fit (two
   CHECK-exclusive column groups + a junction) — expect a dedicated
   `ServicioDeOfertas`, the same divergence `ServicioDePrecios` already took.
   Design decides.
2. **`ResolvedorDeOfertas` is pure Domain, DB-free**, mirroring `ResolvedorDePrecios`:
   it receives candidate ofertas + resolved base prices and returns the final
   price. All precedence/stacking/tie-break rules are unit-tested without a database.
3. **Batch-first service boundary**: `ServicioDeOfertas` loads candidates for the
   whole input set in one query (articulo ids + their grupo/categoria ids + lista
   ids), resolves base prices in one batch, then hands everything to the pure
   resolver.
4. **Precedence algorithm** (proposal-level; spec pins the scenarios):
   candidates = active ofertas matching scope, lista, vigencia window, day/hour
   and `cantidad_minima`. Base = highest `prioridad` among `acumulable = false`;
   **tie-break: greater effective discount for that line, then lower `id_oferta`**.
   Then all matching `acumulable = true` ofertas stack **additively over the
   ORIGINAL resolved price** (user decision 2026-08-03, superseding this
   proposal's earlier sequential draft): each oferta's discount is computed
   independently against the original unit price, the discounts are summed
   together with the base's, and the total is clamped so the final price never
   goes below 0 (combined discount capped at 100%). Application order for
   reporting purposes only: descending `prioridad`, then ascending `id_oferta`.
   Spec must pin the per-benefit-type arithmetic under this rule (porcentaje =
   original × pct; importe_fijo = fixed amount; precio_unitario = original −
   precio_unitario) and the clamp scenarios.
5. **DB CHANGE GATE** (CLAUDE.md) before any migration: this stage has 2 write
   paths (`ofertas`, `ofertas_listas`); the gate summary groups them and calls out
   the doc-10 deviation explicitly.
6. **`db-error-backstops` per constraint from the start** — each new CHECK, FK and
   unique index gets its 23505/23503/23514 mapping and race test in the same work
   unit that introduces it.

## Affected Areas

| Area | Impact | Description |
|---|---|---|
| `src/Ways.Domain/Ofertas` (new) | New | `Oferta`, `OfertaLista`, `ResolvedorDeOfertas` (pure) |
| `src/Ways.Domain/Precios` | Modified | Batch-friendly resolution entry point |
| `src/Ways.Infrastructure` | Modified | EF configs, migration, RLS policies, new backstop mappings |
| `src/Ways.Application` | New/Modified | `ServicioDeOfertas` (new); `ServicioDePrecios` gains batch price resolution |
| `src/Ways.Api` | New | `OfertasEndpoints` (CRUD) + batch resolution endpoint |
| `src/Ways.Web` | New | Ofertas ABM screen + descriptor/component tests (vitest infra exists since PR #28) |
| `docs/10-modelo-de-datos.md` | Modified | Record `ofertas_listas` replacing `id_lista_precio` |

## Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| Dual CHECK-exclusive column shape is a new modeling pattern here (EF + domain invariants) | Med-High | Enforce in Domain (factory/guard) AND DB CHECK; design pins the mapping before apply |
| Precedence/stacking edge cases (equal prioridad, zero/negative results, overlapping windows, `cantidad_minima` boundaries) | High | Rules decided here; spec must enumerate scenarios; pure resolver = exhaustive unit tests, no DB |
| Batch resolution changes an already-shipped price path | Med | Keep existing single-article semantics intact; batch is an added path, covered by parity tests |
| Multi-lista junction diverges from doc 10 | Med | Doc-10 update is **in scope**, not a follow-up; gate summary states the deviation |
| Reviewer overload (schema + engine + API + heavy screen) | High | Chained PRs, stacked-to-main; `sdd-tasks` slices by write path |
| Derivada listas + percentage offers can compound into odd prices | Med | Explicit rule: benefit computes over the lista's already-resolved price; floor at 0; spec scenario required |

## Rollback Plan

Fully additive. New tables (`ofertas`, `ofertas_listas`), new endpoints, a new
`/ofertas` route, and a new Domain namespace — all removable without touching
stage 1–3 flows. The only change to shipped code is the **added** batch price
path in `ServicioDePrecios`; the existing single-article methods keep their
current signatures and semantics, so reverting the stage cannot break stage 3.
The doc-10 edit is text-only and revertible with the migration.

## Dependencies

- Stages 1–3 (merged): `EntidadTenant`, RLS helpers, keyed platform context,
  `ManejadorDeErrores` contract, and all FK targets (`articulos`, `grupos`,
  `categorias`, `listas_precio`) already existing with composite-FK alternate keys.
- DB Change Gate approval (blocking, before the migration).
- `react-async-state` skill is mandatory for the web slice; `web-descriptor-tests`
  applies now that Ways.Web has vitest infra.

## Success Criteria

- [ ] `ofertas` enforces both exclusivity rules (scope and benefit) at Domain and DB level
- [ ] An oferta can target multiple listas via `ofertas_listas`; zero rows = all listas
- [ ] Resolution is deterministic: same inputs → same applied ofertas and price, always
- [ ] Highest-`prioridad` non-acumulable wins as base; all matching acumulables stack on top
- [ ] Ties resolve without ambiguity (greater discount, then lower `id_oferta`)
- [ ] Resolution resolves MANY articulos in one call (no per-article query loop)
- [ ] Ofertas ABM works end-to-end in `Ways.Web`, with descriptor/component tests
- [ ] `docs/10-modelo-de-datos.md` reflects the `ofertas_listas` junction
- [ ] Every new constraint has its `db-error-backstops` mapping + race test

## Resolved product decisions (question round)

Question round already run with the user (2026-08-03). Binding for spec/design/tasks:

1. **Full doc-10 precedence semantics from day one.** Highest-`prioridad`
   non-acumulable = base; all matching `acumulable = true` stack on top,
   **additively over the ORIGINAL resolved price** (each discount computed
   independently against the original, summed, clamped at 100% combined /
   final price floor 0 — user decision 2026-08-03, second question round).
   *Rationale*: the legacy's accidental precedence is exactly the thing being
   replaced; shipping a partial rule now would mean re-teaching tenants later;
   additive-over-original is the arithmetic tenants can verify by hand.
   Tie-break proposed here (greater effective discount, then lower `id_oferta`)
   so the engine is never ambiguous.
2. **v1 = full doc-10 shape**: articulo/grupo/categoria exclusive scope, fecha /
   hora / dias_semana windows, all three exclusive benefit types, per-lista
   limiting. *Rationale*: the columns are cheap; the resolution rules are the hard
   part and they must be written once, against the complete shape.
3. **Resolution service is in scope, batch-first.** Stage 4 ships CRUD/ABM **and**
   a query-only resolution capability designed for MULTIPLE articulos per call.
   *Rationale*: kills the documented N+1 before stage 5's POS depends on it.
4. **Multi-lista targeting — DEVIATION from doc 10.** `ofertas_listas` junction
   replaces the single `id_lista_precio NULL` column. No rows = all listas
   (including derivadas, where the benefit computes over the already-resolved
   price); rows = only those listas. *Rationale*: real promos target a set of
   listas (e.g. General + Mayorista but not Empleados), which the single column
   cannot express. **Updating `docs/10-modelo-de-datos.md` is in scope** — doc 10
   is the definitive schema and must not drift.
5. **Minor defaults (assumed, user-confirmed)**: every oferta defaults to
   tenant-wide (`id_empresa NULL`, empresa picker optional); a dedicated Ofertas
   screen with articulo/grupo/categoria/lista pickers (not tabs inside other ABMs);
   negative-quantity/returns semantics deferred to stage 5; default offer-label
   rendering deferred to stage 5 ticket rendering.

## Note for sdd-tasks

Slice by **write path**: (1) schema + domain + migration, (2) CRUD/ABM service +
API, (3) resolution engine (pure resolver + batch service + endpoint, including
the batch price path), (4) web Ofertas screen. Apply the Review Workload Forecast
discipline (400-line budget, `Decision needed before apply`, `Chained PRs
recommended`, `400-line budget risk`); delivery is chained PRs stacked-to-main per
`protocolo-pr-solo-dev` and the stage-3 precedent.
