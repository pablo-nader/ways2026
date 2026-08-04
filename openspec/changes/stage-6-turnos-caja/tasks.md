# Tasks: Stage 6 — Turnos de caja, arqueos, tesorería y gastos

## Orchestrator Decisions Recorded This Phase

1. **Web split into two PR slices, not one.** The proposal's indicative
   order lists "6. Web caja screens" as a single slice and flags it as
   "likely the biggest slice and splittable". Following the stage-5
   precedent (cart/scan vs. payment/ticket), this tasks.md splits it into
   Slice 6 (apertura + movimientos + resumen, `Caja.tsx`) and Slice 7
   (cierre + `Pos.tsx` gate seam, `CierreDeCaja.tsx`) — the cierre screen
   carries the heaviest `react-async-state` obligation count (rules 5–9,
   irreversibility) and deserves its own review round.
2. **Spec/design wording conflict on the closed-turno cierre rejection,
   flagged for correction.** `specs/arqueo-de-cierre/spec.md`'s "Closing an
   already-closed turno is rejected" scenario says `409 turno_no_abierto`;
   `design.md`'s binding cierre transaction (statement 1) says `409
   turno_ya_cerrado` for the same 0-rows-but-exists case. Design is more
   specific (distinguishes "never existed" / "already closed" from "no
   turno open for a new write") and is treated as binding per the design
   doc's own transaction pseudocode. Slice 4 implements `turno_ya_cerrado`;
   `sdd-verify` should flag the spec scenario for a wording correction.
3. **`ResolverTurnoAbiertoAsync` extracted once, reused three times.**
   Slice 2 (turno lifecycle) owns the canonical open-turno resolver;
   Slice 3 (gastos) and Slice 5 (checkout) both depend on it instead of
   each writing their own query — not spelled out as a shared component in
   the proposal's indicative order, but avoids three copies of the same
   `409 turno_no_abierto` lookup.
4. **`EstrategiaSinReintento` is reused verbatim from stage 5**, no new
   abstraction — `src/Ways.Application/Abstracciones/EstrategiaSinReintote.cs`
   already exists and covers apertura/cierre/retiro/refuerzo/gasto (manual,
   rare, no idempotency key), per proposal Approach §2.

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | ~5,200–7,500 total (incl. EF migration boilerplate) |
| 400-line budget risk | High |
| Chained PRs recommended | Yes |
| Suggested split | Slice 1 → (Slice 2 → Slice 3) → Slice 4 → Slice 5 → Slice 6 → Slice 7 |
| Delivery strategy | auto-chain (cached decision) |
| Chain strategy | stacked-to-main |

Decision needed before apply: No — resolved: chained PRs, stacked-to-main,
`judgment-day` before every PR. Seven slices forecast. Slice 1 (schema
gate: 5 tables, 4 enums, partial unique index, FK, RLS, 5 entity skeletons,
backstop groundwork) is the `size:exception` candidate, same precedent as
every prior stage's schema slice. Slice 4 (the derivation + cierre
transaction — the stage's centerpiece) is the largest backend slice and
carries the heaviest test surface (exhaustive `CalculadorDeArqueo` unit
tests + 5 integration categories). Slice 5 (checkout wiring) is
deliberately small in diff size but demands its own full judgment-day
round because it touches `ServicioDeVentas`, the most-guarded transaction
in the project.

400-line budget risk: High
Chained PRs recommended: Yes
Chain strategy: stacked-to-main

### Suggested Work Units

| Unit | Goal | Likely PR | Est. lines | Notes |
|------|------|-----------|-----------|-------|
| 1 | Schema gate (5 tables, 4 enums, partial unique index, FK, RLS, EF configs, entity skeletons, backstop groundwork) | PR 1 | ~1,100–1,500 | Base: `main`. `size:exception` candidate. Hosts the DB Change Gate approval. |
| 2 | Turno lifecycle: apertura + `movimientos_caja` + pure rules | PR 2 | ~700–950 | Base: PR 1. |
| 3 | Gastos write path | PR 3 | ~350–500 | Base: PR 2 (reuses turno resolver). |
| 4 | Derivation + cierre (centerpiece) | PR 4 | ~1,400–1,900 | Base: PR 3. |
| 5 | Checkout wiring (surgical) | PR 5 | ~350–550 | Base: PR 4. Own full judgment-day round. |
| 6 | Web: caja apertura / movimientos / resumen | PR 6 | ~500–700 | Base: PR 2 (needs apertura/movimiento/resumen endpoints only — parallel to PR 3/4 in principle, sequenced here for review load). |
| 7 | Web: cierre + `Pos.tsx` gate seam | PR 7 | ~550–800 | Base: PR 6 (same files) + PR 5 (real checkout 409). |

---

## Slice 1: Schema Gate (PR 1)

**Start**: `main`. **Finish**: 5 tables + 4 enums + partial unique index +
`comprobantes_venta` FK/index + RLS live, backstops mapped, entities
compile. **Rollback**: down-migration (drop all 5 tables + 4 enums + the
FK). **`size:exception` candidate.**

- [ ] 1.1 **DB CHANGE GATE — STOP.** Present the model summary grouped by
  write path (A: turno lifecycle, B: movimientos_caja, C: gastos, D:
  tesorería, E: checkout wiring FK) and WAIT for explicit approval before
  generating anything. Surface: (a) proposal decision 2 (cierre role —
  offer Supervisor+Admin tightening), (b) design decision 8 (`motivo NOT
  NULL length >= 5` uniformized across all three `tipo_movimiento_caja`,
  stricter than proposal decision 9), (c) proposal decision 1 (`gastos`
  ships without the compra FK, deferred to stage 8), (d) design decision 1
  (declared deviation: cierre closes the turno FIRST + `FOR SHARE` on every
  turno-writing path — changes the checkout's transaction order), (e)
  design decision 2 (vuelto lands on the cash anchor as a total, not
  per-medio), (f) design decision 3 (hard stop on a non-unique efectivo
  medio), (g) design decision 9 (tesorería `egreso` = all gastos, legacy
  parity), (h) `diferencia` as a `GENERATED ALWAYS` column. *(design: Table
  Shapes gate intro; proposal: Approach §7)*
- [ ] 1.2 Add `TurnosCajaYGastosEtapa6` migration: `turnos_caja`,
  `movimientos_caja`, `arqueos_turno`, `movimientos_tesoreria`, `gastos` +
  enums `estado_turno`, `tipo_movimiento_caja`, `tipo_movimiento_tesoreria`,
  `categoria_gasto`; `ux_turnos_caja_abierto (id_punto_venta) WHERE estado
  = 'abierto'`; `comprobantes_venta.id_turno_caja` FK +
  `ix_comprobantes_venta_turno`; `HabilitarRlsDeTenant` on all 5 new tables
  in this same migration. Confirm `has-pending-model-changes` clean.
  *(design: Table Shapes A–E; Migration/Rollout)*
- [ ] 1.3 [P] Add Domain entity skeletons: `TurnoCaja`, `MovimientoCaja`,
  `ArqueoTurno`, `MovimientoTesoreria` (`Ways.Domain/Caja`), `Gasto`
  (`Ways.Domain/Gastos`) + the 4 enums. *(spec: turnos-de-caja / Turno
  Schema At Rest; movimientos-de-caja / Movimiento Schema At Rest;
  arqueo-de-cierre / Arqueo Schema At Rest; tesoreria / Movimiento
  Tesorería Schema At Rest; gastos / Gasto Schema At Rest)*
- [ ] 1.4 Add 5 EF configs in `Ways.Infrastructure/Persistencia/Configuraciones`;
  update `ComprobanteVentaConfiguration` with the turno FK + index. *(design:
  Table Shapes — EF scope column per table)*
- [ ] 1.5 Update `WaysDbContext`: 5 new `DbSet`s; manual `id_tenant` filters
  for `movimientos_caja` / `arqueos_turno` / `movimientos_tesoreria`
  (append-only, not `EntidadTenant`). *(design: Table Shapes — EF scope)*
- [ ] 1.6 Update `docs/10-modelo-de-datos.md`: `gastos.id_comprobante_compra`
  deferred-FK twin note (proposal decision 1). *(design: Migration/Rollout)*
- [ ] 1.7 Backstop groundwork: `ux_turnos_caja_abierto` → 23505 → `409
  turno_ya_abierto` (new branch in `ClasificarUnicidad`); `ux_arqueos_turno_medio`
  → 23505 → `409 arqueo_duplicado`; add `ClasificarCheckDeCaja` (exact-name
  switch, appended after `ClasificarCheckDeVentas`) for the 6 CHECKs:
  `ck_turnos_caja_fondo_inicial_no_negativo`,
  `ck_turnos_caja_cierre_consistente`, `ck_movimientos_caja_importe`,
  `ck_movimientos_caja_motivo_minimo`, `ck_gastos_importe_positivo`,
  `ck_movimientos_tesoreria_cadena`; confirm (comment only) the generic
  `fk_` prefix branch covers all 5 tables' FKs. *(design: Backstop Map)*
- [ ] 1.8 [P] Integration: RLS proofs for all 5 new tables (EF filter +
  raw-SQL/`IgnoreQueryFilters`). *(spec: turnos-de-caja, movimientos-de-caja,
  arqueo-de-cierre, tesoreria, gastos — implicit tenant isolation)*
- [ ] 1.9 Integration: raw-SQL backstop tests for the 6 new CHECKs + the 2
  new unique-index 23505 mappings, incl. the documented exemption for
  `ux_arqueos_turno_medio` (no race test — cierre derives the row set
  inside its own exclusive lock). *(design: Backstop Map)*

**Verify**: `dotnet test --filter FullyQualifiedName~Ways.IntegrationTests.Caja|FullyQualifiedName~Ways.IntegrationTests.Gastos`

---

## Slice 2: Turno Lifecycle (PR 2)

**Depends on**: Slice 1. **Start**: PR 1 merged/branch. **Finish**:
apertura live behind the partial unique index with a proven race,
`movimientos_caja` live with motivo rules, shared turno-resolution helper
in place. **Rollback**: new routes/service only.

- [ ] 2.1 [P] Add `ReglaDeTurnos` (pure Domain): `estado` transitions
  (`abierto → cerrado` only, no reapertura). *(design decision 10; spec:
  turnos-de-caja / Turno Schema At Rest)*
- [ ] 2.2 [P] Add `ReglaDeMovimientosDeCaja` (pure Domain): `importe` rule
  (`apertura_cajon` ⇒ exactly `0`, else `> 0`), `motivo` rule (`NOT NULL`,
  `length(btrim) >= 5`, uniform across all 3 `tipo`). *(design decisions 8,
  10; spec: movimientos-de-caja / Motivo Required For Retiro And Refuerzo,
  Apertura De Cajón Follows Legacy F12 Parity)*
- [ ] 2.3 Add `ServicioDeTurnos.AbrirAsync`: plain INSERT behind
  `ux_turnos_caja_abierto`, `EstrategiaSinReintento`, `23505` → `409
  turno_ya_abierto`. *(design decision 7; spec: turnos-de-caja / One Open
  Turno Per Punto De Venta)*
- [ ] 2.4 Add `ServicioDeTurnos.ResolverTurnoAbiertoAsync` (shared resolver,
  reused by Slice 3 and Slice 5): resolve the open turno from
  `idPuntoVenta`; `409 turno_no_abierto` when none exists. *(spec:
  turnos-de-caja / Turno Is Always Server-Resolved, Never Client-Supplied)*
- [ ] 2.5 Add `ServicioDeTurnos.RegistrarMovimientoAsync`: resolve open
  turno, apply `ReglaDeMovimientosDeCaja`, insert `movimientos_caja` row.
  *(spec: movimientos-de-caja / Movimiento Schema At Rest, Movimiento
  Requires An Open Turno)*
- [ ] 2.6 Add `CajaEndpoints`: `POST /api/caja/turnos`, `GET
  /api/caja/turnos/abierto`, `GET /api/caja/turnos`, `GET
  /api/caja/turnos/{id}`, `POST /api/caja/turnos/{id}/movimientos` — all
  `OperacionDePos`. *(design: API Surface)*
- [ ] 2.7 [P] Unit: `ReglaDeTurnos` transitions; `ReglaDeMovimientosDeCaja`
  exhaustive — importe/motivo per tipo incl. the 5-char boundary. *(spec:
  movimientos-de-caja, all 3 motivo/importe scenarios)*
- [ ] 2.8 Integration (concurrency): rendezvous race — two concurrent
  aperturas at the same punto de venta ⇒ exactly one `201` + one `409
  turno_ya_abierto`; aperturas at different puntos de venta are
  independent. *(spec: turnos-de-caja / Concurrent aperturas race to
  exactly one winner, Aperturas at different puntos de venta)*
- [ ] 2.9 Integration: retiro/refuerzo without motivo → `400
  movimiento_de_caja_sin_motivo`; retiro with motivo accepted; apertura_cajon
  non-zero importe rejected; apertura_cajon short motivo → `400
  motivo_de_apertura_cajon_invalido`; apertura_cajon valid accepted;
  movimiento with no open turno → `409 turno_no_abierto`. *(spec:
  movimientos-de-caja, all 7 remaining scenarios)*
- [ ] 2.10 [P] Integration: authorization — Vendedor opens a turno and
  records a movimiento; `RolConocido.Root` rejected from both. *(spec:
  turnos-de-caja / Apertura And Cierre Authorization (apertura half);
  movimientos-de-caja / Movimiento Authorization)*
- [ ] 2.11 Regression: Slice 1 suite unedited and green.

**Verify**: `dotnet test --filter FullyQualifiedName~ServicioDeTurnos|FullyQualifiedName~CajaEndpoints`

---

## Slice 3: Gastos (PR 3)

**Depends on**: Slice 2 (`ResolverTurnoAbiertoAsync`). **Start**: PR 2
merged/branch. **Finish**: gasto capture live against the open turno, no
retiro-equivalent representable. **Rollback**: new routes/service only.

- [ ] 3.1 Add `ServicioDeGastos.RegistrarAsync`: resolve open turno via the
  shared resolver, reject `importe <= 0`, insert `gastos` row with
  `id_turno_caja` populated server-side. *(spec: gastos / Gasto Requires
  An Open Turno, Importe Must Be Positive)*
- [ ] 3.2 Add `GastosEndpoints`: `POST /api/gastos`, `GET /api/gastos` —
  `OperacionDePos`. *(design: API Surface)*
- [ ] 3.3 Integration: gasto persists with categoría/medio; gasto rejected
  with no open turno; gasto succeeds with `id_turno_caja` populated
  server-side (never client-supplied); zero-importe gasto rejected before
  reaching the database. *(spec: gastos / Gasto Schema At Rest, all 4
  scenarios)*
- [ ] 3.4 [P] Reflection/static test: `categoria_gasto` enum contains no
  retiro-equivalent value. *(spec: gastos / No Magic Tipo Encodes A Retiro
  As A Gasto)*
- [ ] 3.5 [P] Integration: Vendedor records a gasto (authorization). *(spec:
  gastos / Gasto Authorization)*
- [ ] 3.6 Regression: Slices 1–2 suites unedited and green.

**Verify**: `dotnet test --filter FullyQualifiedName~ServicioDeGastos|FullyQualifiedName~GastosEndpoints`

---

## Slice 4: Derivation + Cierre (PR 4) — the centerpiece

**Depends on**: Slice 3 (needs turnos, movimientos_caja, and gastos ledgers
to derive against). **Start**: PR 3 merged/branch. **Finish**: resumen
parcial and cierre share one derivation, cierre is one atomic irreversible
transaction, all 3 racy surfaces this stage introduces are proven.
**Rollback**: new routes/service only.

- [ ] 4.1 Add `CalculadorDeArqueo` (pure Domain): `ActividadDeMedio`,
  `InsumosDeArqueo`, `LineaDeArqueo` records; per-medio formula
  `pagos(m) − gastos(m) + [m=ancla]×(fondo + refuerzos − retiros −
  vueltosTotales)`; arqueables by row existence, never by value; CC medios
  excluded. *(design: The Derivation; Interfaces/Contracts)*
- [ ] 4.2 Add `ResolvedorDeMedioDeCajaFisica` (pure): resolve the tenant's
  unique `Comportamiento = Efectivo` medio over all rows regardless of
  `activo`; `409 caja_sin_medio_efectivo_unico` on 0 or 2+. *(design
  decision 3)*
- [ ] 4.3 Add `LectorDeMovimientosDelTurno` (Application): 7 fixed grouped
  queries (pagos por medio, vueltos por medio de comprobantes `emitido`,
  gastos por medio, refuerzos, retiros, fondo, catálogo de medios) → build
  `InsumosDeArqueo`, query count constant in ticket volume. *(design
  decision 5; arqueo-de-cierre / Anulados Are Excluded From The
  Derivation)*
- [ ] 4.4 Add `ValidadorDeConteos` (Application): declared counts vs.
  `arqueables` — missing medio → `400 arqueo_incompleto`; extra medio →
  `400 medio_sin_actividad_en_el_turno`; CC medio declared → `400
  medio_no_arqueable`. *(design: The Derivation — "Which medios get a
  row")*
- [ ] 4.5 Add `ServicioDeTurnos.CerrarAsync`: pin `momento` outside the
  transaction; `EstrategiaSinReintento`; the 6-step transaction — (1)
  `UPDATE turnos_caja SET estado='cerrado' … WHERE estado='abierto'
  RETURNING` (exclusive lock, first statement; 0 rows ⇒ 404 or `409
  turno_ya_cerrado`), (2) `LectorDeMovimientosDelTurno`, (3)
  `ResolvedorDeMedioDeCajaFisica`, (4) `CalculadorDeArqueo` +
  `ValidadorDeConteos`, (5) INSERT `arqueos_turno`, (6) chain one
  `movimientos_tesoreria` row (`inicio` = last `final` for the punto de
  venta, `ingreso` = Σ retiros, `egreso` = Σ gastos). *(design: The Cierre
  Transaction — binding statement order; decision 1's declared deviation)*
- [ ] 4.6 Add `ServicioDeResumenDeTurno`: calls the same
  `LectorDeMovimientosDelTurno` + `CalculadorDeArqueo` pair, read-only, no
  writes. *(spec: arqueo-de-cierre / Resumen Parcial Uses The Same
  Derivation As Cierre)*
- [ ] 4.7 Add endpoints: `GET /api/caja/turnos/{id}/resumen`, `POST
  /api/caja/turnos/{id}/cierre` — `OperacionDePos`. *(design: API Surface)*
- [ ] 4.8 Update `SuperficieDeAutorizacionTests` allowlist with the 5 new
  caja/gastos non-GET routes (`POST /api/caja/turnos`, `POST
  …/movimientos`, `POST …/cierre`, `POST /api/gastos`). *(design: API
  Surface — omission guard note)*
- [ ] 4.9 [P] Unit: `CalculadorDeArqueo` exhaustive over a synthetic
  turno — vuelto on the anchor and on an electronic medio, anulados
  excluded, NCX negatives, gastos per medio, retiros/refuerzos/fondo
  anchor-only, a medio netting to exactly 0 that still gets a row, a CC
  medio never getting one, zero-activity medios absent; collapse test
  against doc 10's literal one-medio formula under `AdmiteVuelto` true
  only on efectivo. *(design: Testing Strategy — Unit (Domain); spec:
  arqueo-de-cierre / Importe Esperado Derivation Per Medio, all 3
  scenarios; Arqueo Rows Only For Medios With Activity, both scenarios)*
- [ ] 4.10 [P] Unit: `ResolvedorDeMedioDeCajaFisica` — 0 / 1 / 2 efectivo
  medios.
- [ ] 4.11 Integration (atomicity): force a failure at each of the 6 cierre
  steps ⇒ turno still `abierto`, zero `arqueos_turno`, zero
  `movimientos_tesoreria`. *(spec: arqueo-de-cierre / A failed cierre
  leaves the turno open with no side effects; tesoreria / A failed cierre
  leaves no tesorería row)*
- [ ] 4.12 Integration (concurrency): two concurrent cierres of the same
  turno ⇒ exactly one success + one `409 turno_ya_cerrado`. *(spec:
  arqueo-de-cierre / Closing an already-closed turno is rejected — bound to
  `turno_ya_cerrado` per Orchestrator Decision 2 above)*
- [ ] 4.13 Integration (derivation identity): `GET …/resumen` immediately
  before a cierre returns per-medio expectations byte-identical to the
  `arqueos_turno.importe_esperado` written by that cierre. *(spec:
  arqueo-de-cierre / Resumen parcial matches what cierre would compute)*
- [ ] 4.14 Integration (budget): resumen over turnos with 2/50/200 tickets
  issues the same command count; `DbCommand` interceptor. *(design: Testing
  Strategy — Integration (budget))*
- [ ] 4.15 Integration (parity/shape): grep assertion — no cierre request
  DTO field named `total`/`esperado`/`importe_esperado`. *(spec:
  arqueo-de-cierre / No request shape accepts a total; proposal: Success
  Criteria)*
- [ ] 4.16 [P] Integration: cierre writes one `arqueos_turno` row per medio
  with activity, none for a zero-activity medio, none for cuenta corriente;
  tesorería chains correctly across a first-ever and a second cierre;
  cierre payload with only declared counts accepted. *(spec:
  arqueo-de-cierre / Cierre writes one row per medio with activity, A
  medio with no activity gets no row, Cuenta corriente never produces a
  row, A cierre request with only declared counts is accepted; tesoreria /
  First-ever cierre starts from zero, A second cierre chains from the
  first's final, Cierre never writes more than one tesorería row)*
- [ ] 4.17 Regression: Slices 1–3 suites unedited and green.

**Verify**: `dotnet test --filter FullyQualifiedName~CalculadorDeArqueo|FullyQualifiedName~ServicioDeTurnos.CerrarAsync|FullyQualifiedName~Arqueo`

---

## Slice 5: Checkout Wiring (PR 5) — surgical, own judgment-day round

**Depends on**: Slice 4 (turno lock semantics + resolver established).
**Start**: PR 4 merged/branch. **Finish**: every new comprobante carries a
non-NULL `id_turno_caja`, anulación respects the closed-turno gate, the
entire stage-5 integration suite stays green except turno-precondition
fixtures. **Rollback**: precondition + one field + one assignment only —
revert restores stage-5 behaviour exactly.

- [ ] 5.1 Modify `ServicioDeVentas.EmitirAsync`: call
  `ResolverTurnoAbiertoAsync` immediately after `ResolverPuntoVentaAsync`
  (so a bogus PV still yields the ADR-8 404 before `409 turno_no_abierto`);
  add `IdTurnoCaja` to the frozen `PlanDeVenta`. *(design decision 11;
  spec: operacion-de-pos / Selling with no open turno fails before any
  pricing work)*
- [ ] 5.2 Modify `EjecutarTransaccionAsync`: add `SELECT … FROM
  turnos_caja … FOR SHARE` as the first statement of the write
  transaction; replace the `IdTurnoCaja = null` hardcode at
  `ServicioDeVentas.cs:459` with `plan.IdTurnoCaja`. *(design decisions 1,
  11; spec: comprobantes-venta / Every new sale carries the resolved open
  turno)*
- [ ] 5.3 Modify `AnularAsync`: add `SELECT t.estado … JOIN
  comprobantes_venta … FOR SHARE OF t` before the existing atomic `UPDATE …
  WHERE estado='emitido' RETURNING`; 0 rows (NULL turno) ⇒ proceed;
  `'cerrado'` ⇒ `409 turno_cerrado`. *(design decision 4; spec:
  comprobantes-venta / Anulación rejected when the comprobante's turno is
  closed, Stage-5 NULL-turno comprobante stays anulable)*
- [ ] 5.4 Update `ComprobanteVenta.cs`'s `IdTurnoCaja` doc-comment: the
  stage-5 promise is fulfilled. *(proposal: Affected Areas)*
- [ ] 5.5 Integration: every comprobante emitted after this stage carries a
  non-NULL `id_turno_caja`; selling with no open turno rejected before any
  oferta resolution or price lookup runs (assert via command-count/mock
  spy, not just the response). *(spec: comprobantes-venta / Every new sale
  carries the resolved open turno; operacion-de-pos / Selling with no open
  turno fails before any pricing work)*
- [ ] 5.6 Integration (concurrency, 3rd racy surface): a sale racing a
  cierre ⇒ the pago is either counted in the arqueo or the sale receives
  `409 turno_no_abierto`, never neither. *(design: Backstop Map —
  "Genuinely racy surfaces… three")*
- [ ] 5.7 [P] Integration: anulación rejected with `409 turno_cerrado` when
  the comprobante's turno is closed; stage-5 NULL-turno comprobante stays
  anulable. *(spec: comprobantes-venta, both scenarios)*
- [ ] 5.8 Integration (budget): checkout read count stays ≤ 17 regardless
  of line count (was ≤ 16 pre-stage-6). *(design decision 11)*
- [ ] 5.9 Regression: entire stage-5 integration suite green, updating only
  fixtures that now need an open turno opened first; confirm no other
  assertion changed. *(proposal: Risks — "the entire stage-5 integration
  suite must stay green unchanged except for the new turno precondition")*
- [ ] 5.10 Run a **dedicated full judgment-day round** on this slice's diff
  alone before opening the PR — the most-guarded transaction in the
  project. *(proposal: Approach §6; Risks)*

**Verify**: `dotnet test --filter FullyQualifiedName~ServicioDeVentas` (must show 271+N Domain / 209+N Application / 393+N Integration, all green)

---

## Slice 6: Web — Caja Apertura, Movimientos & Resumen (PR 6)

**Depends on**: Slice 2 (apertura/movimiento endpoints) + Slice 4 (resumen
endpoint). **Start**: PR 4 branch (or PR 2 branch if run in parallel —
sequenced here after PR 5 for review load). **Finish**: apertura,
movimientos, and live resumen parcial functional; `Caja.tsx` compiles and
is tested. **Rollback**: new route only.

- [ ] 6.1 Add pure `src/Ways.Web/src/api/caja.ts`: request/response mappers
  for apertura, movimiento, and resumen. *(design: Web Composition)*
- [ ] 6.2 Add `src/Ways.Web/src/paginas/Caja.tsx`: turno status, apertura
  form (fondo inicial + observaciones), movimientos form (retiro / refuerzo
  / apertura de cajón with motivo), resumen parcial (áreas, medios,
  tickets, egresos por categoría). `react-async-state` rules 1
  (`conteos`-style state N/A here, applies fully in Slice 7), 2
  (`generacionResumenRef` gates every resumen response), 3 (a movimiento
  or gasto bumps the resumen generation before the write), 8
  (`key={idTurno ?? 'sin-turno'}` on the caja subtree). *(design: Web
  Composition; react-async-state obligations 2, 3, 8)*
- [ ] 6.3 Wire `/caja` route + nav entry.
- [ ] 6.4 [P] Unit: `caja.ts` mappers. *(web-descriptor-tests)*
- [ ] 6.5 Component: apertura flow; registering a movimiento bumps and
  refetches the resumen generation; opening a new turno does not inherit
  the previous turno's displayed state (`key` reset). RTL + `user-event`,
  `vi.mock('../api/cliente')`. *(design: Testing Strategy — Component
  (Web); react-async-state obligations 2, 3, 8)*

**Verify**: `npx vitest run src/paginas/Caja.test.tsx src/api/caja.test.ts`

---

## Slice 7: Web — Cierre & Pos.tsx Gate Seam (PR 7)

**Depends on**: Slice 6 (same web area) + Slice 5 (real `409
turno_no_abierto`). **Start**: PR 6 branch. **Finish**: full cierre flow
functional, irreversible-by-design, double-submit impossible; `Pos.tsx`
offers to open a turno instead of surfacing a raw error. **Rollback**: new
route + `Pos.tsx` guard only.

- [ ] 7.1 Add pure `src/Ways.Web/src/api/arqueo.ts`: client-side
  `diferencia` preview — mirrors the server, never authoritative. *(design:
  Web Composition)*
- [ ] 7.2 Add `src/Ways.Web/src/paginas/CierreDeCaja.tsx`: resumen display
  + per-medio count inputs (one `conteos` record, functional updater only
  — rule 1) + irreversibility confirmation + "Finalizar cierre" wiring to
  `POST …/cierre`. Implement rules 4 (`finally` clearing
  `cerrando`/`registrando` is generation-gated), 5 (disabled window from
  click to Z render, per-action busy flags), 6 (a 2xx cierre is never
  reported as failure — the post-close Z fetch has its own try/catch), 7
  (medios/resumen load failure ⇒ visible aviso + an actually-disabled
  "Finalizar cierre"), 9 (every superseding action blocked while the
  cierre POST is outstanding, plus a first-line `if (cerrando) return`).
  *(design: Web Composition; react-async-state obligations 1, 4, 5, 6, 7,
  9)*
- [ ] 7.3 Modify `Pos.tsx`: `409 turno_no_abierto` from `POST /api/ventas`
  renders a blocking panel offering "Abrir turno" (fondo inicial +
  observaciones); after a successful apertura the checkout is **never**
  auto-resubmitted — the cashier presses Cobrar again. *(design: Web
  Composition — "Pos.tsx gate seam"; react-async-state obligation 9)*
- [ ] 7.4 [P] Unit: `arqueo.ts` — `diferencia` preview sign.
  *(web-descriptor-tests)*
- [ ] 7.5 Component: double-click on "Finalizar cierre" issues exactly one
  POST (rule 9); gate seam renders on `409` and does not auto-resubmit the
  sale; a 2xx cierre is never reported as failure even if the post-close Z
  fetch fails (rule 6); medios/resumen failing to load shows an aviso and
  an actually-disabled "Finalizar cierre" (rule 7). RTL + `user-event`.
  *(design: Testing Strategy — Component (Web))*
- [ ] 7.6 Wire `/caja/cierre` route.
- [ ] 7.7 Smoke-verify (`tsc -b` / `oxlint` / `vite build` clean).
- [ ] 7.8 Regression: full `npx vitest run` green (165 baseline + this
  stage's new tests, no unrelated assertion changed).

**Verify**: `npx vitest run` (full suite) && `npx tsc -b` && `npx vite build`

---

## Dependency Summary

```
Slice 1 (schema gate — DB CHANGE GATE, size:exception)
        │
        ▼
Slice 2 (turno lifecycle: apertura + movimientos_caja + shared resolver)
        │
        ├──▶ Slice 3 (gastos — reuses the shared resolver)
        │           │
        │           ▼
        └──────▶ Slice 4 (derivation + cierre — the centerpiece)
                        │
                        ▼
                  Slice 5 (checkout wiring — surgical, own judgment-day)
                        │
Slice 2 ──▶ Slice 6 (web: apertura/movimientos/resumen)
                        │
                        ▼ (needs Slice 5's real 409)
                  Slice 7 (web: cierre + Pos.tsx gate seam)
```

Within each slice, `[P]`-tagged tasks are parallelizable; all others are
sequential (schema → domain → application → API → tests; the DB CHANGE
GATE always blocks migration generation). No two slices in this stage are
fully independent of each other — every write path after Slice 1 shares
either the turno row, the resolver, or the derivation, per design's
lock-order invariant. Chained PRs, stacked-to-main, `judgment-day` before
every PR (per `protocolo-pr-solo-dev`); Slice 5 gets a dedicated full
judgment-day round.
