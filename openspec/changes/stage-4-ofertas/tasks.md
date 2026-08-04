# Tasks: Stage 4 — Ofertas

## Orchestrator Decisions Recorded This Phase

One naming discrepancy between `spec.md` and `design.md` had to be resolved
before the backstop task could be written. Recorded here as binding for
`sdd-apply`.

1. **CHECK-mapping error codes for `ofertas`.** `specs/ofertas/spec.md`
   (normative for the API contract) pins `oferta_alcance_invalido` and
   `oferta_beneficio_invalido` for the two exclusivity CHECKs.
   `design.md`'s Backstop Map table uses different code text
   (`alcance_de_oferta_invalido`/`beneficio_de_oferta_invalido`) for the same
   two constraints — an editorial drift, not a deliberate second contract.
   **Resolution: spec's codes win** for `ck_ofertas_alcance_exclusivo` /
   `ck_ofertas_beneficio_exclusivo` (spec is what `sdd-verify` checks
   against). `ck_ofertas_ventana_valida` → `ventana_de_oferta_invalida` and
   `ck_ofertas_dias_semana` → `dias_semana_invalidos` are unaffected — no
   spec scenario pins those two, so design's naming is used as-is.

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | ~2,850–3,950 total (incl. EF migration boilerplate) |
| 400-line budget risk | High |
| Chained PRs recommended | Yes |
| Suggested split | Slice 1 → Slice 2 → (Slice 3 ∥ Slice 4) |
| Delivery strategy | chained PRs, stacked-to-main (resolved, cached decision) |
| Chain strategy | stacked-to-main |

Decision needed before apply: No — resolved: chained PRs, stacked-to-main.
Four slices are forecast, each individually near or above the 400-line
budget once tests are included. Slice 1 (schema + the project's first
CHECK-constraint backstop classifier) is the most likely `size:exception`
candidate, same precedent as stage 1 Slices 3/4, stage 2 Slice 1, and stage 3
Slice 1. Slice 3 (resolution engine) and Slice 4 (web screen) are independent
of each other once Slice 2's CRUD API exists and can run in parallel.

### Suggested Work Units

| Unit | Goal | Likely PR | Est. lines | Notes |
|------|------|-----------|-----------|-------|
| 1 | Schema (2 tables, DB CHANGE GATE), domain entities, `ReglaDeOfertas`, `ClasificarCheck` backstop classifier, RLS proofs, docs/10 update | PR 1 | ~900–1,300 | Base: `main`. One combined migration, one gate (design: Migration Sequencing). No backfill/seed. |
| 2 | CRUD/ABM (`ServicioDeOfertas`, contracts, endpoints, `ofertas_listas` replace-set, FK + `pk_ofertas_listas` race tests) | PR 2 | ~500–700 | Depends on PR 1. |
| 3 | Resolution engine (pure `ResolvedorDeOfertas`, `PreciosVigentesEnLoteAsync`, `ServicioDeOfertas.ResolverAsync`, `/resolver` endpoint, parity + query-count tests) | PR 3 | ~750–1,050 | Depends on PR 1 + PR 2. Independent of PR 4. |
| 4 | Web: dedicated Ofertas screen (`Ofertas.tsx`, `api/ofertas.ts` mappers, descriptor/component tests) | PR 4 | ~700–950 | Depends on PR 2 only (CRUD API — the screen does not call `/resolver`). Independent of PR 3. |

### Slice Cut Rationale (deviation from proposal's write-path prose grouping)

The proposal's "Note for sdd-tasks" lists write-path order as schema →
CRUD/ABM → resolution engine → web screen, implying a linear chain. One
adjustment made here, justified by the design's own ABM Composition section
rather than by the prose order:

- **Slice 4 (web) depends on Slice 2, not Slice 3.** Design's ABM
  Composition describes the Ofertas form as pure CRUD (identification, scope
  picker, vigencia block, benefit picker, lista multi-select) — it never
  calls `POST /api/ofertas/resolver`. Resolution is a separate, query-only
  capability consumed by stage 5's POS, out of this stage's UI scope. Slice
  3 and Slice 4 therefore have no technical dependency on each other, both
  depending only on Slice 2's CRUD API — the same shape as stage 3's Slice
  5/Slice 6 parallel split.

Each slice: start = base branch state, finish = its own tests green,
verification = its own unit/integration/race tests, rollback = down-migration
(Slice 1 only) or new-routes-only removal (Slices 2–4), per proposal.md §
Rollback Plan (all-additive).

---

## Slice 1: Schema, Domain Foundation & Migration (PR 1)

**Start**: `main`. **Finish**: migration applied, RLS proven on both new
tables, tests green. **Rollback**: down-migration (additive only — drop both
tables).

### 1A. DB CHANGE GATE — BLOCKING

- [x] 1.1 **APPROVED 2026-08-03**, exactly as presented (both tables, the 4
  CHECKs, composite FKs, hand-named indexes, `pk_ofertas_listas`, RLS on both,
  no-unique-index-on-nombre exemption, docs/10 update for the junction
  deviation, no seed/backfill). **STOP.** Present the migration model summary
  and wait for explicit approval before generating anything (CLAUDE.md gate).
  The summary MUST group:
  - **New table `ofertas`** (catálogo scope, `id_tenant` NOT NULL,
    `id_empresa NULL` = tenant-wide, doc 09 §84): `nombre citext(150)`,
    `id_articulo`/`id_grupo`/`id_categoria int NULL`, `fecha_desde`/`hasta
    date NULL`, `hora_desde`/`hasta time NULL`, `dias_semana smallint[]
    NULL`, `cantidad_minima numeric(12,3) NULL`, `precio_unitario
    numeric(14,2) NULL`, `porcentaje numeric(5,2) NULL`, `importe_fijo
    numeric(14,2) NULL`, `prioridad int NOT NULL DEFAULT 0`, `acumulable
    bool NOT NULL DEFAULT false`, `activo`.
  - **Both CHECK groups, exact names**: `ck_ofertas_alcance_exclusivo`
    (`num_nonnulls(id_articulo,id_grupo,id_categoria)=1`);
    `ck_ofertas_beneficio_exclusivo`
    (`num_nonnulls(precio_unitario,porcentaje,importe_fijo)=1`);
    `ck_ofertas_ventana_valida` (`fecha_hasta >= fecha_desde` and
    `hora_hasta >= hora_desde`, both NULL-tolerant); `ck_ofertas_dias_semana`
    (`dias_semana <@ ARRAY[1..7]::smallint[]`).
  - Composite FKs `fk_ofertas_{tenant,empresa,articulo,grupo,categoria}`;
    explicit snake_case indexes `ix_ofertas_{tenant,empresa,articulo,grupo,
    categoria}` (EF's PascalCase default is the stage-3 trap).
  - **New table `ofertas_listas`** (junction, tenant-scoped): `id_oferta,
    id_lista_precio, id_tenant`; PK named explicitly `pk_ofertas_listas`
    (fixes the `PK_articulos_empresas` naming inconsistency instead of
    copying it); composite FKs `fk_ofertas_listas_{tenant,oferta,
    lista_precio}`; indexes `ix_ofertas_listas_{tenant,oferta,lista_precio}`;
    no audit/soft-delete columns (same PK-only shape as `articulos_empresas`).
  - **RLS**: `HabilitarRlsDeTenant("ofertas")` +
    `HabilitarRlsDeTenant("ofertas_listas")` in the same migration.
  - **DEVIATION**: `ofertas_listas` replaces doc-10's single
    `id_lista_precio NULL` column — `docs/10-modelo-de-datos.md` update
    ships in the **same PR**.
  - **No unique index on `ofertas`** — documented exemption, not an
    oversight (`nombre` is a ticket label, deliberately non-unique).
  - **No seed/backfill.**
  - **Rollback** = drop both tables.

### 1B. Domain

- [x] 1.2 [P] Add `Oferta : EntidadTenant` (raw nullable columns per the
  gate summary) in `Ways.Domain/Ofertas`. *(spec: ofertas / Ofertas Schema
  At Rest)*
- [x] 1.3 [P] Add `OfertaLista` junction entity (no soft-delete, PK-only
  row) in `Ways.Domain/Ofertas`. *(spec: ofertas / Multi-Lista Targeting
  via ofertas_listas)*
- [x] 1.4 Add pure `ReglaDeOfertas.LeerAlcance`/`LeerBeneficio` (total
  functions projecting an `Oferta` row into `AlcanceDeOferta`/
  `BeneficioDeOferta` record structs, or throw `ErrorDominio`) plus range
  validation (`porcentaje ∈ (0,100]`, `importe_fijo ≥ 0`, `precio_unitario ≥
  0`, `cantidad_minima > 0`, `dias_semana ⊆ {1..7}` sin duplicados) + unit
  tests. *(design decisions 1, 2; spec: ofertas / Domain guard rejects
  invalid shapes, Vigencia Window Semantics, cantidad_minima Trigger
  Semantics)*

### 1C. Migration (only after 1.1 approved)

- [x] 1.5 Generate migration `OfertasEtapa4`: both tables, the four CHECKs,
  composite FKs, hand-named snake_case indexes, `HabilitarRlsDeTenant` on
  both tables. Confirm `dotnet ef migrations has-pending-model-changes` is
  clean before committing. *(design: Migration Sequencing)*
- [x] 1.6 Update `docs/10-modelo-de-datos.md` in the same PR: record the
  `ofertas_listas` junction replacing the single `id_lista_precio NULL`
  column, stating the deviation explicitly. *(proposal: docs/10 update in
  scope; design: Migration Sequencing)*

### 1D. db-error-backstops mapping groundwork

- [x] 1.7 Add a `ClasificarCheck` classifier matched by **exact name**
  behind a `ck_ofertas_` prefix guard, appended **after** the two existing
  exact-name 23514 branches (`ck_clientes_cf_protegido`,
  `ck_precios_ventana_valida`): `ck_ofertas_alcance_exclusivo` → 400
  `oferta_alcance_invalido`; `ck_ofertas_beneficio_exclusivo` → 400
  `oferta_beneficio_invalido` (both spec-pinned codes, Orchestrator Decision
  1 above); `ck_ofertas_ventana_valida` → 400 `ventana_de_oferta_invalida`;
  `ck_ofertas_dias_semana` → 400 `dias_semana_invalidos`. Add
  `pk_ofertas_listas` → 23505 → 409 `oferta_lista_duplicada` (same family as
  `pk_articulos_empresas`). Confirm (code comment, no code change) the
  existing generic `fk_` prefix branch already covers `fk_ofertas_*`/
  `fk_ofertas_listas_*`. *(design decision 8; Backstop Map)*

### 1E. Tests

- [x] 1.8 Integration: RLS proofs for both new tables (EF filter blocks
  cross-tenant read; raw-SQL/`IgnoreQueryFilters` blocked), mirroring
  `AislamientoDeTenantTests`. *(spec: ofertas / Tenant Isolation for
  ofertas and ofertas_listas, both scenarios)* —
  `tests/Ways.IntegrationTests/OfertasRlsTests.cs`.
- [x] 1.9 [P] Unit: `ReglaDeOfertas` — scope/benefit exclusivity (zero and
  multiple → rejected), range validation for all four numeric fields,
  `dias_semana` subset + no-duplicates. *(spec: ofertas / Domain guard
  rejects invalid shapes before the database)* —
  `tests/Ways.Domain.Tests/Ofertas/ReglaDeOfertasTests.cs`.
- [x] 1.10 Integration: raw-SQL backstop tests for the four 23514 CHECKs
  (SQLSTATE-asserted + translated code — honest defense-in-depth
  reachability per design's Backstop Map note, since `ReglaDeOfertas`
  pre-validates every normal write path). *(spec: ofertas / Scope CHECK
  rejects zero or multiple scope columns, Benefit CHECK rejects zero or
  multiple benefit columns; design: Backstop Map reachability note)* —
  `tests/Ways.IntegrationTests/OfertasCheckBackstopTests.cs`.
- [x] 1.11 Regression: existing Domain/Application/IntegrationTests suites
  unedited and green.

---

## Slice 2: CRUD/ABM Service + API (PR 2)

**Depends on**: Slice 1. **Start**: PR 1 merged/branch. **Finish**: oferta
CRUD, `ofertas_listas` replace-set management, tenant-scoped reference
checks and the `pk_ofertas_listas` race all live through the API, tests
green. **Rollback**: new routes only.

### 2A. Application

- [x] 2.1 Add `ServicioDeOfertas` (list/create/edit/soft-delete), **not**
  extending `ServicioDeCatalogo<T,TListado,TAlta>` (design decision 6):
  validates scope/benefit exclusivity + ranges via `ReglaDeOfertas` before
  write; tenant-scoped existence pre-check for `id_articulo`/`id_grupo`/
  `id_categoria`/`id_empresa` references (400 `referencia_invalida`);
  `GestionDeCatalogo` policy. *(spec: ofertas / Oferta ABM Lifecycle and
  Authorization, Invalid scope reference maps to 400)*
- [x] 2.2 Add `ofertas_listas` replace-set handling inside
  `ServicioDeOfertas`: delete-all + insert inside one transaction, ids
  `.Distinct()`ed, tenant-scoped existence pre-check on `id_lista_precio`
  references (400 `referencia_invalida`). *(spec: ofertas / Multi-Lista
  Targeting via ofertas_listas, all scenarios; design: Protection Rules)*
- [x] 2.3 Add contracts: `AltaOferta`/`EdicionOferta`/`OfertaListado` (incl.
  the lista-id-set field).

### 2B. API

- [x] 2.4 Add `OfertasEndpoints`: list/create/edit/soft-delete,
  `GestionDeCatalogo` policy (tenant admin only), ADR-8 uniform 404 for
  cross-tenant access.

### 2C. Tests

- [x] 2.5 [P] Unit (InMemory, transaction-blocked-provider caveat noted):
  required-field validation, invalid scope/benefit shape → rejected before
  DB, invalid clasificador reference → 400, cross-tenant 404. *(spec:
  ofertas / Domain guard rejects invalid shapes before the database)* —
  `tests/Ways.Application.Tests/Ofertas/ServicioDeOfertasTests.cs`.
- [x] 2.6 [P] Integration: admin create→soft-delete round trip; vendedor
  blocked on create/edit. *(spec: ofertas / Admin creates and soft-deletes
  an oferta, Vendedor blocked from writing)*
- [x] 2.7 [P] Integration: cross-tenant read/write → uniform 404 (ADR-8).
  *(spec: ofertas / Cross-tenant read/write is a uniform 404)*
- [x] 2.8 [P] Integration: `ofertas_listas` — zero rows targets every
  lista; rows restrict targeting; cross-tenant lista reference → 400
  `referencia_invalida` via tenant-scoped pre-check. *(spec: ofertas / No
  junction rows targets every lista, Junction rows restrict targeting,
  Junction row references must belong to the same tenant)*
- [x] 2.9 Integration: `pk_ofertas_listas` race — two concurrent PUTs
  replacing the same oferta's lista set → exactly one winner, the loser a
  translated 409/serialization outcome, never a 500, SQLSTATE-asserted.
  *(design: Backstop Map — pk_ofertas_listas race test)*
- [x] 2.10 [P] Integration: FK smoke tests for each new `fk_ofertas_*`/
  `fk_ofertas_listas_*` (cross-tenant/nonexistent id → 23503/400). *(backstop
  map)* — `tests/Ways.IntegrationTests/OfertasEndpointsTests.cs`.
- [x] 2.11 Regression: Slice 1 suites unedited and green.

---

## Slice 3: Resolution Engine (PR 3)

**Depends on**: Slice 1 + Slice 2. **Start**: PR 2 merged/branch. **Finish**:
pure resolver exhaustively tested, batch price path live, `/resolver`
endpoint live, constant-query-count and single-path parity proven, tests
green. **Rollback**: new routes/methods only — `PrecioVigenteAsync`/
`PreciosVigentesAsync` untouched.

### 3A. Domain

- [ ] 3.1 Add resolution contract record structs (`LineaAResolver`,
  `OfertaCandidata`, `AlcanceDeOferta`, `BeneficioDeOferta`,
  `OfertaAplicada`, `PrecioConOfertas`) in `Ways.Domain.Ofertas` per design's
  Resolution Contract shape.
- [ ] 3.2 Add `ResolvedorDeOfertas.Resolver(linea, candidatas)` — pure
  static: base selection (highest `prioridad` among `acumulable = false`,
  tie-break greater discount then lower `id_oferta`); additive-over-original
  stacking (each discount computed independently against `PrecioOriginal`,
  rounded 2 decimals `MidpointRounding.AwayFromZero`, summed, clamped to
  `[0, PrecioOriginal]`); `Aplicadas` ordered descending `prioridad` then
  ascending `id_oferta`. *(design decisions 2, 3; Resolution Contract
  arithmetic table; spec: resolucion-de-ofertas / Base Selection and
  Tie-Break, Additive-Over-Original Stacking, all scenarios)*
- [ ] 3.3 Add categoria ancestor-chain matching helper (builds the ancestor
  set in memory from one `id_categoria`/`id_categoria_padre` projection,
  reusing `ReglaDeCategorias.ProfundidadMaxima = 3`). *(design: Batch
  Boundary — Categoria scope matching; spec: resolucion-de-ofertas /
  Categoria-scoped oferta reaches subcategoria articulos)*

### 3B. Application — batch price path

- [ ] 3.4 Add `ServicioDePrecios.PreciosVigentesEnLoteAsync(ids articulo,
  ids lista, fecha, ct)` → `IReadOnlyDictionary<(int,int), decimal?>`: load
  requested listas by id (no `Activo` filter, matching
  `PrecioVigenteAsync`'s explicit-id semantics); load base listas of
  derivadas; ONE `precios` query with `= ANY` on both id sets plus the
  shipped date-window predicate, grouped in memory (`OrderByDescending
  (VigenteDesde)`, first per pair); derivadas resolved through the shipped
  `ResolvedorDePrecios.ResolverPrecioDerivado` (depth-1 + negative-price
  guards kept). Existing `PrecioVigenteAsync`/`PreciosVigentesAsync`
  signatures and semantics **unchanged** (design decision 5). *(spec:
  precios / Batch Current-Price Resolution, all 3 scenarios)*

### 3C. Application — resolution service

- [ ] 3.5 Add `ServicioDeOfertas.ResolverAsync(lineas)` — batch-first: one
  `articulos` query, one categorias ancestor-map query, one `ofertas`
  query, one `ofertas_listas` query, calls `PreciosVigentesEnLoteAsync`
  (3 `precios` queries), then calls the pure `ResolvedorDeOfertas` per line
  — 7 constant queries total, independent of N articles × M listas.
  Candidate matching (`activo`, scope incl. categoria ancestors,
  `id_empresa`, in-memory lista targeting, vigencia window,
  `cantidad_minima`) applied in memory before calling the resolver. *(design:
  Technical Approach — 7 constant queries; decision 4 — in-memory lista
  targeting; spec: resolucion-de-ofertas / Batch Input Shape, Candidate
  Matching, all scenarios)*
- [ ] 3.6 Add contracts: `LineaDeResolucion` (request DTO),
  `ResultadoDeResolucion` (response DTO incl. applied-ofertas list).

### 3D. API

- [ ] 3.7 Add `POST /api/ofertas/resolver`, query-only (writes nothing —
  doc-comment and endpoint summary MUST state "POST, no muta nada", design
  decision 7). *(spec: resolucion-de-ofertas / Applied Ofertas Are
  Reported, Never Persisted)*

### 3E. Tests

- [ ] 3.8 [P] Unit: `ResolvedorDeOfertas` — exhaustive, every spec scenario
  with the spec's concrete numbers (highest-prioridad base, both tie-breaks,
  acumulable-only-no-base, base+1 acumulable, multiple acumulables,
  `precio_unitario` as base, `precio_unitario` as acumulable, 100%-clamp,
  derivada-lista-as-original-base, no-candidate passthrough). *(spec:
  resolucion-de-ofertas / Base Selection and Tie-Break, Additive-Over-
  Original Stacking — all scenarios; design: Testing Strategy — "bulk of
  the stage's test mass")* —
  `tests/Ways.Domain.Tests/Ofertas/ResolvedorDeOfertasTests.cs`.
- [ ] 3.9 [P] Unit: candidate-matching helpers — categoria ancestor-chain
  reach, grupo match, empresa exclusion, empty-set-matches-all for lista and
  `dias_semana`. *(spec: resolucion-de-ofertas / Candidate Matching, all
  scenarios)*
- [ ] 3.10 Integration (parity): `PreciosVigentesEnLoteAsync` ==
  `PrecioVigenteAsync` for the same inputs (fija, derivada, missing price,
  inactive lista) — assert value equality per pair, both paths in one test.
  *(design: Testing Strategy — Integration (parity); spec: precios /
  Existing single-articulo methods are unaffected)*
- [ ] 3.11 Integration (batch query count): resolution over N articles
  issues a **constant** query count — count commands via an EF
  interceptor/`DbCommand` log, assert count is independent of N. *(spec:
  resolucion-de-ofertas / Batch resolves many articulos in one call;
  design: Testing Strategy — Integration (batch))*
- [ ] 3.12 Integration: `/resolver` end-to-end scenario mirroring a spec
  scenario (base + acumulable over real Postgres data), no-match
  passthrough, and a no-writes assertion (row counts unchanged across every
  affected table). *(spec: resolucion-de-ofertas / Result lists all applied
  ofertas, Resolution performs no writes)* —
  `tests/Ways.IntegrationTests/OfertasResolucionTests.cs`.
- [ ] 3.13 Regression: Slice 1 + Slice 2 suites unedited and green.

---

## Slice 4: Web — Ofertas Screen (PR 4)

**Depends on**: Slice 2 (CRUD API only — the screen never calls
`/resolver`; independent of Slice 3, see Slice Cut Rationale). **Start**: PR
2 branch (parallel to PR 3 per chosen chain strategy). **Finish**: dedicated
Ofertas ABM functional against the API, descriptor/component tests green
(`web-descriptor-tests` — smoke-only is **not** sufficient this stage,
vitest infra shipped in PR #28), `react-async-state` compliant. **Rollback**:
new route only.

### 4A. Pure mappers

- [x] 4.1 [P] Add `src/Ways.Web/src/api/ofertas.ts` pure mapping helpers:
  `aAltaOferta`, `aValoresOferta`, `opcionesDeLista`, `resumenDeBeneficio`
  (design decision 9 — mappers live outside the component so
  `web-descriptor-tests` stays applicable). *(spec: ofertas / Ofertas
  Schema At Rest, Multi-Lista Targeting via ofertas_listas)*
- [x] 4.2 [P] Unit: `ofertas.ts` mappers — coercion, `'' → null`, exclusive
  group forced to `null`, lista option filtering. *(design: Testing
  Strategy — Unit (Web))* — colocated
  `src/Ways.Web/src/api/ofertas.test.ts`.

### 4B. Screen

- [x] 4.3 Add dedicated `src/Ways.Web/src/paginas/Ofertas.tsx` (not the
  generic descriptor machine, design decision 9): identification (`nombre`,
  `prioridad`, `acumulable`, `activo`) + scope radio driving one of three
  pickers (articulo/grupo/categoria) + optional empresa picker (default
  tenant-wide) + vigencia block (dates, hours, weekday checkboxes) +
  `cantidad_minima` + benefit radio driving one of three numeric inputs +
  multi-select of listas (empty = all, stated in the UI copy).
  `react-async-state` applies in full — rule 9 (block supersede-during-write,
  do **not** token-reconcile) and rule 5 (per-entity busy flags, not a
  page-level boolean). *(design: ABM Composition; spec: ofertas / Oferta
  ABM Lifecycle and Authorization)*

### 4C. Component tests + wiring

- [x] 4.4 [P] Component: scope/benefit radio show-hide (the `visibleSi`
  analogue), multi-lista selector, disabled-window behavior. RTL +
  `user-event`, `vi.mock('../api/cliente')`. *(design: Testing Strategy —
  Component (Web); web-descriptor-tests skill)* — colocated
  `src/Ways.Web/src/paginas/Ofertas.test.tsx`.
- [x] 4.5 Wire `/ofertas` route + nav entry.
- [x] 4.6 Smoke-verify (`tsc -b`/`oxlint`/`vite build` clean); relies on
  Slice 2's integration coverage proving the exact contract shapes the
  screen consumes.
- [x] 4.7 Regression: Slice 1 + Slice 2 suites unedited and green
  (retarget/rebase against Slice 3 if it merged first, per chosen chain
  strategy).

---

## Dependency Summary

```
Slice 1 (schema + domain + migration)
   └─▶ Slice 2 (CRUD/ABM service + API)
            ├─▶ Slice 3 (resolution engine: pure resolver + batch price path + /resolver endpoint)
            └─▶ Slice 4 (web: Ofertas screen)
                     Slice 3, Slice 4 are independent — parallelizable
```

Within each slice, `[P]`-tagged tasks are parallelizable; all others are
sequential (schema → infra → application → tests; the DB CHANGE GATE always
blocks the migration-generation task). Slices 3 and 4 are the only two
slices that can run as parallel PR branches against the same parent (PR 2)
— every other slice is a hard sequential dependency per the write-path
ordering, with one deviation from the proposal's linear prose grouping (web
depends on CRUD only, not on resolution — see "Slice Cut Rationale").
