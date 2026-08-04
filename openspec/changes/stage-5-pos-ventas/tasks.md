# Tasks: Stage 5 — POS y ventas

## Orchestrator Decisions Recorded This Phase

1. **Migration-split kept as two separate PR slices, not one.** The launch
   prompt's suggested skeleton bundled both migrations into a single
   "schema+domain foundation" slice. This tasks.md keeps `NumeracionDeComprobantesEtapa5`
   (Slice 2) and `VentasStockYCuentaCorrienteEtapa5` (Slice 3) in separate PRs
   because bundling them back into one PR would defeat design's own stated
   reason for splitting into two migrations ("a single migration for seven
   tables would exceed the 400-line review budget on its own"). The DB CHANGE
   GATE STOP task (2.1) still presents **both** migrations together for **one**
   approval, satisfying design's "one gate covers both migrations" rule; Slice
   3 generates the second migration under that same approval without
   re-running the gate.
2. **Backstop code names pinned to avoid a collision.** Design's Backstop Map
   only says "400 via a new `ClasificarCheckDeVentas`" for the three new
   CHECKs, without naming their codes. This tasks.md pins
   `ck_pagos_comprobante_vuelto_no_negativo` → `vuelto_de_pago_negativo`
   instead of reusing `vuelto_invalido` — that code is already
   `ValidadorDePagos`'s domain-rejection code for a different rule (`Σ vuelto
   > max(0, Σ importe − total)`), and reusing it would make two different
   failure classes return the same code text. `numero_de_comprobante_invalido`
   and `movimiento_de_stock_sin_cantidad` are pinned for the other two CHECKs.
   Binding for `sdd-apply`.
3. **Web split into two sequential PRs on the same file.** The launch prompt
   flagged "heaviest web ever" and pre-authorized a split. Slice 6 ships cart
   + scan with checkout stubbed/disabled; Slice 7 completes payment + ticket
   + full checkout wiring on the same `Pos.tsx`. Slice 6 is genuinely
   parallelizable against the backend Slices 3–5 (different codebase area, no
   shared files) once its only two prerequisites (Slice 1's policy, Slice 2's
   escaneo endpoint) exist — it does **not** need Slice 4's checkout endpoint.
4. **No anulación control in the POS screen this stage.** Design's POS Screen
   Composition section scopes the screen to scan/cart/payment/ticket only.
   Anulación (Slice 5) ships API-only; a UI is a future-stage concern.

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | ~4,900–6,800 total (incl. EF migration boilerplate) |
| 400-line budget risk | High |
| Chained PRs recommended | Yes |
| Suggested split | Slice 1 ∥ Slice 2 → Slice 3 → Slice 4 → (Slice 5 ∥ Slice 6 → Slice 7) |
| Delivery strategy | chained PRs, stacked-to-main (resolved, cached decision) |
| Chain strategy | stacked-to-main |

Decision needed before apply: No — resolved: chained PRs, stacked-to-main,
`judgment-day` before every PR. Seven slices are forecast — the largest slice
count of the project so far, as flagged by the proposal. Slice 3 (schema
gate: six tables, three enums, six domain entities, four pure Domain rule
classes, backstops, RLS proofs) is the most likely `size:exception`
candidate, same precedent as every prior stage's schema slice. Slice 1 and
Slice 2 are independent of each other (no shared files) and can run as
parallel PR branches off the same base. Slice 6 (web cart+scan) is
independent of Slices 3–5 once Slices 1–2 merge.

400-line budget risk: High
Chained PRs recommended: Yes
Chain strategy: stacked-to-main

### Suggested Work Units

| Unit | Goal | Likely PR | Est. lines | Notes |
|------|------|-----------|-----------|-------|
| 1 | `OperacionDePos` policy + POS read-surface re-gating (no schema) | PR 1 | ~300–450 | Base: `main`. Independent of PR 2. |
| 2 | Numeración schema (DB CHANGE GATE, both migrations presented), atomic allocator, race tests, `ParserDeEscaneo` + `ServicioDeEscaneo` + scan endpoint | PR 2 | ~550–750 | Base: `main`. Independent of PR 1. Hosts the one-time gate approval. |
| 3 | Schema gate: 6 tables + 3 enums + RLS + 6 domain entities + 4 pure Domain rule classes + backstops (incl. `_numero` ordering trap) | PR 3 | ~1,500–2,000 | Depends on PR 2 (gate approval + migration sequencing). `size:exception` candidate. |
| 4 | Checkout: `ServicioDeVentas` transaction (decide-then-commit, pinned statement order), endpoints, atomicity/concurrency/budget/snapshot/parity tests | PR 4 | ~1,100–1,500 | Depends on PR 1 + PR 2 + PR 3. |
| 5 | Anulación + ajuste manual de stock | PR 5 | ~450–650 | Depends on PR 4 (same `ServicioDeVentas` class). Independent of PR 6/7. |
| 6 | Web: cart + scan (`Pos.tsx` skeleton, `carrito.ts`, checkout stubbed) | PR 6 | ~450–650 | Depends on PR 1 + PR 2 only — parallel to PR 3/4/5. |
| 7 | Web: payment + ticket + full checkout wiring | PR 7 | ~550–800 | Depends on PR 6 (same file) + PR 4 (real checkout endpoint). |

---

## Slice 1: Auth Policy & POS Read-Surface (PR 1)

**Start**: `main`. **Finish**: `OperacionDePos` gates every POS read path,
writes stay on `GestionDeCatalogo`, omission guard + inverted tests green.
**Rollback**: revert policy stacking — routes return to `GestionDeCatalogo`-only.

- [ ] 1.1 Add `Politicas.OperacionDePos` (Vendedor + Admin, Root excluded) in
  `src/Ways.Api/Seguridad/Politicas.cs`. *(design decision 6; spec:
  operacion-de-pos / OperacionDePos Policy Admits Vendedor and Admin)*
- [ ] 1.2 Re-gate `ArticulosEndpoints` group (incl. `/precios`,
  `/codigos-barra`, `/escaneo`) to `OperacionDePos`, stacking
  `GestionDeCatalogo` on POST/PUT/DELETE only. *(spec: articulos / Articulo
  ABM Lifecycle and Authorization; codigos-barra / Barcode Add/Remove/List
  Management)*
- [ ] 1.3 [P] Re-gate `ClientesEndpoints` group to `OperacionDePos`, stacking
  `GestionDeCatalogo` on writes. *(spec: clientes / Cliente ABM Lifecycle and
  Authorization)*
- [ ] 1.4 [P] Re-gate `CatalogosEndpoints` and `ParametrosEndpoints` groups to
  `OperacionDePos`, stacking `GestionDeCatalogo` on writes. *(spec:
  parametros-operativos / Read Access Under OperacionDePos For UI Preview)*
- [ ] 1.5 Relax `OfertasEndpoints` group gate to `OperacionDePos`; stack
  `GestionDeCatalogo` on POST `/`, PUT, DELETE — `POST /resolver` stays on
  the group gate only, closing the stage-4 carryover. *(spec:
  resolucion-de-ofertas / OperacionDePos Authorization For POST
  /api/ofertas/resolver)*
- [ ] 1.6 Update `Cliente.cs`'s `Saldo` doc-comment ("never moves" →
  activated by Slice 3), per proposal Affected Areas.
- [ ] 1.7 Add `SuperficieDeAutorizacionTests`: walk `EndpointDataSource`,
  assert every non-GET endpoint carries `GestionDeCatalogo` against an
  explicit allowlist (`POST /api/auth/*`, `POST /api/ofertas/resolver`,
  `POST /api/ventas`, `POST /api/ventas/{id}/anulacion`). *(design:
  Authorization Surface — omission guard, mandatory)*
- [ ] 1.8 Update the two inverting tests:
  `ClientesEndpointsTests.UnVendedorNoPuedeListarListasDePrecio` and
  `ArticulosEndpointsTests.UnVendedorNoPuedeListarCodigosDeBarra` become
  `…PuedeListar…`, with an explicit comment noting these are the two
  intentional inversions. *(design: Authorization Surface — "Two shipped
  tests invert")*
- [ ] 1.9 [P] Integration: Vendedor reads succeed on every re-gated group
  (articulos, clientes, catálogos, parámetros, `/resolver`); every other
  `UnVendedorNoPuede…` write test stays red. *(spec: articulos, clientes,
  codigos-barra, parametros-operativos — "Vendedor can …" scenarios)*
- [ ] 1.10 Regression: full existing suite green, no behavior change beyond
  the auth relaxation.

---

## Slice 2: Numeración Schema, Allocator & Escaneo (PR 2)

**Depends on**: nothing (parallel to Slice 1). **Start**: `main`. **Finish**:
gate approved, `numeraciones_comprobante` live with race-proven allocator,
scan resolution live end-to-end. **Rollback**: down-migration (drop
`numeraciones_comprobante`); `/escaneo` route removal.

### 2A. DB CHANGE GATE — BLOCKING (covers BOTH migrations)

- [ ] 2.1 **STOP.** Present the full model for approval before generating
  anything (CLAUDE.md gate), grouped by write path:
  - **Write path A** (Emisión): `numeraciones_comprobante` (this slice's
    migration) + `comprobantes_venta`/`items_comprobante_venta`/
    `pagos_comprobante` (Slice 3's migration).
  - **Write path B** (Stock): `stock` + `movimientos_stock` (Slice 3).
  - **Write path C** (Cuenta corriente): `movimientos_cuenta_corriente`
    (Slice 3).
  - **3 enums** (correction to the proposal's 2): `estado_comprobante`,
    `motivo_stock`, `tipo_movimiento_cc`.
  Must also surface for explicit confirmation: the 11 proposal assumptions
  (decisions 1–11), the no-advisory-locks deviation (design decision 1),
  recargo staying dormant (decision 11), `id_turno_caja` always NULL,
  `movimientos_stock.id_comprobante_compra` deferred to stage 8, and the
  two-migrations-one-gate process (this migration ships now; the six-table
  migration ships in Slice 3, both under this one approval). *(design: Table
  Shapes gate intro; Migration Sequencing)*

### 2B. Migration + domain (only after 2.1 approved)

- [ ] 2.2 Add `NumeracionDeComprobantesEtapa5` migration:
  `numeraciones_comprobante` (PK `(id_punto_venta, tipo_comprobante)`,
  `id_tenant` non-key, composite FK to `puntos_venta`),
  `HabilitarRlsDeTenant`. Confirm `has-pending-model-changes` clean. *(design
  decision 8; Migration Sequencing)*
- [ ] 2.3 Add `NumeracionComprobante` entity + EF config in
  `Ways.Domain/Ventas` / `Ways.Infrastructure`.
- [ ] 2.4 Add `AsignadorDeNumeroComprobante` (raw ADO, clone of
  `AsignadorDeNumeroCliente`): `INSERT … ON CONFLICT DO NOTHING` then
  `UPDATE … RETURNING proximo_numero - 1`. *(design decision 9; spec:
  comprobantes-venta / Numeración Allocation Is Atomic)*
- [ ] 2.5 Add pure `NumeroDeComprobante.Formatear(idPuntoVenta, numero)` →
  `PPPP-NNNNNNNN`. *(design: API Surface)*

### 2C. Backstop groundwork

- [ ] 2.6 Add `pk_numeraciones_comprobante` → 23505 → 409
  `numeracion_duplicada` to `ManejadorDeErrores` (documented exemption from a
  race test — the write goes through `ON CONFLICT`). *(design: Backstop Map)*

### 2D. Escaneo

- [ ] 2.7 Add pure `ParserDeEscaneo` (Domain): `<7` digits → `codigo_interno`,
  `>=7` → `codigos_barra`, `activo` only, `N*codigo` syntax, empty/`0`
  cantidad defaults to `1`. *(design decision 7; spec: codigos-barra / Scan
  Resolution Rule)*
- [ ] 2.8 Add `ServicioDeEscaneo` (Application, dedicated — not
  `ServicioDeArticulos`): parses then runs one identity-only query, no
  pricing. *(design decisions 7, 10)*
- [ ] 2.9 Add `GET /api/articulos/escaneo?entrada=` under `OperacionDePos`.
  *(design: API Surface)*

### 2E. Tests

- [ ] 2.10 [P] Unit: `NumeroDeComprobante.Formatear` — padding edge cases.
- [ ] 2.11 [P] Unit: `ParserDeEscaneo` exhaustive — 6/7/13-digit boundary,
  `N*codigo`, empty/`0` cantidad, garbage input. *(spec: codigos-barra / Scan
  Resolution Rule, all scenarios)*
- [ ] 2.12 Integration: RLS proof for `numeraciones_comprobante` (EF filter +
  raw-SQL/`IgnoreQueryFilters`).
- [ ] 2.13 Integration (concurrency, honest reachability): two concurrent
  counter allocations at the same punto de venta/tipo → consecutive numbers,
  no gap, no duplicate; a rolled-back allocation leaves an accepted gap.
  *(spec: comprobantes-venta / Numeración Allocation Is Atomic, both
  scenarios)*
- [ ] 2.14 Integration: `GET /api/articulos/escaneo` end-to-end — short code,
  long code, `N*` prefix, inactive articulo not resolved, unknown code
  rejected. *(spec: codigos-barra / Scan Resolution Rule, all scenarios)*
- [ ] 2.15 Regression: Slice 1 suites unedited and green.

---

## Slice 3: Schema Gate — Comprobantes / Stock / CC + Pure Rules (PR 3)

**Depends on**: Slice 2 (gate approval + migration sequencing — no FK
dependency between the two migrations, but the single-approval process
requires 2.1 to run first). **Start**: PR 2 merged/branch. **Finish**: six
tables + three enums live, RLS proven, all four pure Domain rule classes
exhaustively tested, backstops mapped incl. the `_numero` trap. **Rollback**:
down-migration (drop all six tables + three enums). **`size:exception`
candidate.**

### 3A. Migration

- [ ] 3.1 Add `VentasStockYCuentaCorrienteEtapa5` migration:
  `comprobantes_venta`, `items_comprobante_venta`, `pagos_comprobante`,
  `stock`, `movimientos_stock`, `movimientos_cuenta_corriente` + enums
  `estado_comprobante`, `motivo_stock`, `tipo_movimiento_cc`, all hand-named
  snake_case `pk_*`/`ix_*`/`fk_*`, `HabilitarRlsDeTenant` on all six tables
  in this same migration. Confirm `has-pending-model-changes` clean.
  *(design: Table Shapes A/B/C; Migration Sequencing)*
- [ ] 3.2 Update `docs/10-modelo-de-datos.md` in the same PR: record the
  `movimientos_stock.id_comprobante_compra` deferral to stage 8. *(design:
  Table Shapes — write path B note)*

### 3B. Domain entities

- [ ] 3.3 [P] Add `ComprobanteVenta`, `ItemComprobanteVenta`,
  `PagoComprobante` (operativa scope, `id_punto_venta`) in
  `Ways.Domain/Ventas`. *(spec: comprobantes-venta / Comprobante Schema At
  Rest, Snapshot Immutability of Items)*
- [ ] 3.4 [P] Add `Stock`, `MovimientoStock`, `MotivoStock` enum in
  `Ways.Domain/Stock`. *(spec: stock / Stock Schema At Rest)*
- [ ] 3.5 [P] Add `MovimientoCuentaCorriente`, `TipoMovimientoCc` enum in
  `Ways.Domain/CuentaCorriente`. *(spec: consumo-cuenta-corriente /
  Movimiento Schema At Rest)*

### 3C. Pure Domain rules

- [ ] 3.6 Add `ValidadorDePagos` (pure): rejection order 1–8 per design's
  parametrized table (no literal `10`/`20`), `tolerancia_pago`/
  `vuelto_maximo` as parameters, CF exclusion, `LimiteCredito`/
  `CreditoIlimitado`. *(design decision 5; spec: comprobantes-venta /
  Payment Validation Rejection Order, Cuenta Corriente Payment Gating;
  consumo-cuenta-corriente / Credit-Limit Evaluation)*
- [ ] 3.7 Add `CalculadorDeTotales` (pure): pinned rounding order
  (`MidpointRounding.AwayFromZero`), `total == Σ item.total` assertion,
  negative NCX lines. *(design: Checkout Contract — CalculadorDeTotales)*
- [ ] 3.8 Add `ReglaDeComprobantes` (pure): sign vs `tipos_comprobante.signo`
  (TX ⇒ positive, NCX ⇒ negative), estado transitions,
  `id_comprobante_asociado` rules. *(design decision 4; spec:
  comprobantes-venta / Devoluciones As NCX Comprobantes)*
- [ ] 3.9 Add `ResolvedorDeLetraComprobante` (pure, dormant — no endpoint
  wiring). *(design decision 8; spec: comprobantes-venta /
  Comprobante-Letter Resolution Stays Dormant)*

### 3D. Backstop mapping — ordering trap

- [ ] 3.10 Add `ux_comprobantes_venta_numero` → 23505 → 409
  `numero_de_comprobante_duplicado` to `ManejadorDeErrores`, inserted
  **before** the existing `_numero` branch (`numero_duplicado`, cliente).
  Same work unit adds the test proving the new branch wins. *(design:
  Backstop Map — "Ordering trap")*
- [ ] 3.11 Add `pk_stock` → 23505 → 409 `stock_duplicado` (documented
  exemption — `ON CONFLICT` write, raw-SQL test only). *(design: Backstop
  Map)*
- [ ] 3.12 Add `ClasificarCheckDeVentas` (exact-name switch, appended after
  `ClasificarCheckDeOfertas`): `ck_comprobantes_venta_numero_positivo` → 400
  `numero_de_comprobante_invalido`;
  `ck_pagos_comprobante_vuelto_no_negativo` → 400 `vuelto_de_pago_negativo`
  (distinct from the domain code `vuelto_invalido` — Orchestrator Decision
  2); `ck_movimientos_stock_cantidad_no_cero` → 400
  `movimiento_de_stock_sin_cantidad`. *(design: Backstop Map)*
- [ ] 3.13 Confirm (comment only, no code change) the existing generic `fk_`
  prefix branch covers all seven tables' FKs.

### 3E. Tests

- [ ] 3.14 [P] Unit: `ValidadorDePagos` — every rejection rule and its order
  (a payload violating rules 2 and 6 reports 2), tolerancia/vuelto
  boundaries, CF exclusion, `CreditoIlimitado`. *(spec: comprobantes-venta /
  Payment Validation Rejection Order, all 7 scenarios; consumo-cuenta-corriente
  / Credit-Limit Evaluation, both scenarios)*
- [ ] 3.15 [P] Unit: `CalculadorDeTotales` — rounding order, discount clamp,
  negative NCX lines, `total == Σ item.total`.
- [ ] 3.16 [P] Unit: `ReglaDeComprobantes` — sign vs `signo`, estado
  transitions, asociado optional/populated. *(spec: comprobantes-venta /
  Devoluciones As NCX Comprobantes, both scenarios)*
- [ ] 3.17 [P] Unit: `ResolvedorDeLetraComprobante` — full condición-fiscal
  cross, no side effects. *(spec: comprobantes-venta / Comprobante-Letter
  Resolution Stays Dormant, both scenarios)*
- [ ] 3.18 Integration: RLS proofs for all six new tables. *(spec:
  comprobantes-venta / Tenant and Punto de Venta Isolation)*
- [ ] 3.19 Integration: raw-SQL backstop tests for the three new CHECKs +
  the `_numero` ordering-trap proof + `pk_stock` exemption test.
- [ ] 3.20 Regression: Slice 1 + Slice 2 suites unedited and green.

---

## Slice 4: Checkout Write Path (PR 4)

**Depends on**: Slice 1 (policy) + Slice 2 (numeración) + Slice 3
(schema/rules). **Start**: PR 3 merged/branch. **Finish**: a sale completes
end-to-end, all-or-nothing on failure, invariants hold under concurrency,
query budget constant. **Rollback**: new routes/service only. The biggest
slice — expect its own review round to run long.

### 4A. Application — the transaction

- [ ] 4.1 Add `ServicioDeVentas.EmitirAsync` — decide-then-commit: resolve
  `momento`, `tipo`, `cliente`/`puntoVenta`, run `ServicioDeOfertas.
  ResolverAsync` (7 queries), snapshot articulos/codigos_barra/alicuotas (2
  queries), `CalculadorDeTotales.Materializar` (pure), resolve
  `tolerancia_pago`/`vuelto_maximo` (2 queries), load medios (1 query),
  `ValidadorDePagos.Validar` (pure) — all **outside** the transaction,
  building an immutable `PlanDeVenta`. *(design: Technical Approach —
  "decide, then commit"; The Sale Transaction)*
- [ ] 4.2 Add the transactional half inside `CreateExecutionStrategy`: steps
  1–6 in the pinned order (numeración → comprobante → items → pagos → stock
  loop ascending `id_articulo` → CC), every entity built fresh from the plan
  on each retry attempt, step 6 raw ADO. *(design: The Sale Transaction —
  binding statement order, Retry contract)*
- [ ] 4.3 Add `LineaDeVenta`/`PagoDeVenta`/`SolicitudDeVenta` contracts — no
  money fields on the request. *(design decision 3; spec: operacion-de-pos /
  Checkout Orchestration Contract)*
- [ ] 4.4 Wire `movimientos_stock` INSERT + `stock` upsert (`ON CONFLICT DO
  UPDATE … RETURNING`) per line, ascending `id_articulo`. *(design decisions
  1, 2; spec: stock / Sale Decrement Inside The Checkout Transaction)*
- [ ] 4.5 Wire `movimientos_cuenta_corriente` consumo + `Cliente.Saldo`
  `UPDATE … RETURNING` + post-check, only when a pago's medio is
  `CuentaCorriente`. *(spec: consumo-cuenta-corriente / Consumo Is Written
  Inside The Sale Transaction)*

### 4B. API

- [ ] 4.6 Add `POST /api/ventas` — 201 + `Location`, body = comprobante
  emitido with `numeroVisible`, `OperacionDePos`. *(spec: operacion-de-pos /
  Checkout Orchestration Contract)*
- [ ] 4.7 [P] Add `GET /api/ventas/{id}` (reprint, reads the snapshot only)
  and `GET /api/ventas` (filtros + paginado), both `OperacionDePos`. *(spec:
  comprobantes-venta / Snapshot Immutability of Items)*

### 4C. Tests

- [ ] 4.8 Integration (atomicity): force a failure at each of the six
  statements, assert nothing persisted except the consumed número. *(spec:
  comprobantes-venta / A failure after stock decrement rolls back
  everything; design: Testing Strategy — Integration (atomicity))*
- [ ] 4.9 Integration (concurrency): two concurrent sales of the same
  articulo/punto de venta → `stock.cantidad = Σ movimientos_stock`. *(spec:
  stock / Concurrent sales of the same articulo do not corrupt the cache)*
- [ ] 4.10 Integration (concurrency): two concurrent CC sales near the limit
  → limit never exceeded, `saldo = Σ movimientos_cc`. *(spec:
  consumo-cuenta-corriente / Credit-Limit Evaluation)*
- [ ] 4.11 Integration (concurrency): two concurrent sales at the same
  punto de venta racing the counter → consecutive numbers.
- [ ] 4.12 Integration (budget): checkout with 2/20/50 lines issues the same
  command count (≤16 + writes), `DbCommand` interceptor. *(design: Testing
  Strategy — Integration (budget))*
- [ ] 4.13 Integration (snapshot): sell, mutate the articulo's catalog
  fields, re-read the comprobante ⇒ byte-identical items. *(spec:
  comprobantes-venta / Reprint is unaffected by a later catalog change)*
- [ ] 4.14 Integration (parity): legacy B6 rejection order end-to-end; grep
  assertion — no literal `10`/`20` in the checkout path. *(spec:
  parametros-operativos / No hardcoded tolerancia or vuelto value exists)*
- [ ] 4.15 [P] Integration: devolución as standalone NCX and as NCX
  referencing an original TX. *(spec: comprobantes-venta / Devoluciones As
  NCX Comprobantes, both scenarios)*
- [ ] 4.16 Regression: Slices 1–3 suites unedited and green.

---

## Slice 5: Anulación & Ajuste Manual de Stock (PR 5)

**Depends on**: Slice 4 (same `ServicioDeVentas` class; needs an emitted
comprobante to anular). **Start**: PR 4 merged/branch. **Finish**: anulación
reverses stock/CC exactly, double-anulación blocked, admin ajuste live.
**Rollback**: new routes/methods only.

- [ ] 5.1 Add `ServicioDeVentas.AnularAsync` — one transaction: conditional
  `UPDATE … SET estado='anulado' WHERE estado='emitido'` (0 rows ⇒ 409
  `comprobante_ya_anulado`), inverse `movimientos_stock` per item, CC
  contramovimiento if used. *(spec: comprobantes-venta / Anulación Reverses
  Stock and CC, Never Restores by Editing; stock / Anulación Inverse
  Movement; consumo-cuenta-corriente / Anulación Produces A
  Contramovimiento)*
- [ ] 5.2 Add `POST /api/ventas/{id}/anulacion` (POST, not DELETE),
  `OperacionDePos`. *(design: API Surface)*
- [ ] 5.3 Add `ServicioDeStock.AjustarAsync` — `motivo = ajuste`, requires
  non-empty `observaciones`, `GestionDeCatalogo` only. *(spec: stock /
  Manual Ajuste Path Is Admin-Only)*
- [ ] 5.4 Add `POST /api/stock/ajustes` (`OperacionDePos` ∧
  `GestionDeCatalogo`) and `GET /api/stock?idPuntoVenta=&idArticulo=`
  (`OperacionDePos`). *(design: API Surface)*
- [ ] 5.5 [P] Integration: anulación reverses stock and CC exactly (spec
  numbers), idempotent-safe against double-anulación, no `restaurar`
  endpoint (404). *(spec: comprobantes-venta, all 4 scenarios of the
  Anulación requirement)*
- [ ] 5.6 Integration (concurrency): two concurrent anulaciones of the same
  comprobante → exactly one 200 + one 409. *(design: Backstop Map —
  reachability #3)*
- [ ] 5.7 [P] Integration: admin ajuste loads initial stock; Vendedor
  blocked; empty `observaciones` rejected. *(spec: stock / Manual Ajuste
  Path Is Admin-Only, all 3 scenarios)*
- [ ] 5.8 Unit: `stock.cantidad = Σ movimientos_stock` after a mixed
  venta/ajuste/anulación sequence (spec's concrete numbers). *(spec: stock /
  Cantidad Is Always The Sum Of Its Movimientos)*
- [ ] 5.9 Regression: Slices 1–4 suites unedited and green.

---

## Slice 6: Web — Cart & Scan (PR 6)

**Depends on**: Slice 1 (policy) + Slice 2 (escaneo endpoint) only —
parallelizable against Slices 3–5. **Start**: PR 2 branch. **Finish**:
scan-to-cart flow functional, cart reducer + mappers tested, checkout
stubbed. **Rollback**: new route only.

- [ ] 6.1 Add pure `src/Ways.Web/src/api/carrito.ts` reducer: scan-add sums
  quantity on re-scan, manual quantity edit, line removal, NCX negative
  lines. *(design decision 12; spec: codigos-barra / Re-scanning sums
  quantity)*
- [ ] 6.2 [P] Add `src/Ways.Web/src/api/ventas.ts` request/response mappers
  (checkout request shape, comprobante response, scan response).
- [ ] 6.3 Add `src/Ways.Web/src/paginas/Pos.tsx` skeleton: scan input wired
  to `GET /api/articulos/escaneo`, cart table driven by `reducirCarrito`,
  cliente selector — `react-async-state` rules 1–4 apply
  (`generacionEscaneoRef`/`tokenEscaneoRef`, functional updaters only).
  "Cobrar" disabled/stubbed — wired in Slice 7. *(design: POS Screen
  Composition; rules 1–4)*
- [ ] 6.4 [P] Unit: `carrito.ts` — re-scan sums, `N*` prefix pass-through,
  removal, NCX negative lines. *(design: Testing Strategy — Unit (Web);
  web-descriptor-tests)*
- [ ] 6.5 [P] Unit: `ventas.ts` mappers.
- [ ] 6.6 Component: scan → line, re-scan sums instead of duplicating,
  cliente picker. `Pos.test.tsx`, RTL + `user-event`. *(design: Testing
  Strategy — Component (Web))*

---

## Slice 7: Web — Payment, Checkout & Ticket (PR 7)

**Depends on**: Slice 6 (same file) + Slice 4 (real checkout endpoint).
**Start**: PR 6 branch. **Finish**: full checkout functional, all 9
`react-async-state` rules enforced, double-submit impossible. **Rollback**:
new route only.

- [ ] 7.1 Add pure `src/Ways.Web/src/api/pagos.ts`: vuelto/mezcla math
  mirroring `ValidadorDePagos` for instant UX — never authoritative. *(design
  decision 12; POS Screen Composition)*
- [ ] 7.2 Complete `Pos.tsx`: payment panel (medios, vuelto, referencia),
  totals, `POST /api/ventas` wiring, ticket view. Implement
  `react-async-state` rules 5–9 in full — rule 9 (block every superseding
  action during the outstanding POST, plus a first-line `if (cobrando)
  return` guard) is the hard requirement. *(design: POS Screen Composition;
  rules 5–9)*
- [ ] 7.3 [P] Unit: `pagos.ts` — vuelto math, CC disabled for Consumidor
  Final.
- [ ] 7.4 Component: CC option hidden/disabled for Consumidor Final, vuelto
  input disabled on a medio without `AdmiteVuelto`, **double-click on
  "Cobrar" issues exactly one POST**. *(design: Testing Strategy — Component
  (Web), rule 9)*
- [ ] 7.5 Component: a 2xx checkout is never reported as failure even if the
  post-write ticket fetch fails (rule 6); medios/parámetros load failure
  shows an aviso and an actually-disabled "Cobrar" (rule 7). *(design: rules
  6–7)*
- [ ] 7.6 Wire `/pos` route + nav entry.
- [ ] 7.7 Smoke-verify (`tsc -b`/`oxlint`/`vite build` clean); relies on
  Slice 4's integration coverage for the exact contract shapes.
- [ ] 7.8 Regression: Slice 6 suite unedited and green (retarget/rebase
  against Slice 5 if it merged first).

---

## Dependency Summary

```
Slice 1 (auth policy, no schema)  ─┐
Slice 2 (numeración schema + DB   ─┤  Slice 1 ∥ Slice 2 — independent, no shared files
  CHANGE GATE + allocator +          │
  escaneo service)                   │
        │                            │
        ▼                            │
Slice 3 (schema gate: 6 tables +     │
  3 enums + domain + pure rules)     │
        │  gate approval from 2.1    │
        │  covers both migrations    │
        ▼                            ▼
Slice 4 (checkout write path) ◀──────┘
        │
        ├──▶ Slice 5 (anulación + ajuste)         — same ServicioDeVentas class, sequential
        │
Slice 2 ──▶ Slice 6 (web: cart + scan) ──▶ Slice 7 (web: payment + ticket) ◀── Slice 4
        Slice 6 needs only Slice 1 + Slice 2 — parallel to Slice 3/4/5
        Slice 5 ∥ Slice 7 — independent once Slice 4 and Slice 6 are both merged
```

Within each slice, `[P]`-tagged tasks are parallelizable; all others are
sequential (schema → domain → application → API → tests; the DB CHANGE GATE
always blocks migration generation). Slice 1 ∥ Slice 2 and Slice 5 ∥ Slice 7
are the two genuine parallel-PR opportunities; every other edge is a hard
sequential dependency, per proposal.md § Rollback Plan (all-additive).
