# Tasks: Stage 15 — Cuenta corriente de proveedores (ledger)

## Orchestrator Decisions Recorded This Phase

1. **6 slices, 6 PRs, stacked-to-main** — design.md's ratified breakdown
   (re-confirmed, not re-scoped, from the proposal's own tentative plan).
   Merge order: `1 → 2 → 3 → 4 → 5 → 6`, exactly as both proposal.md and
   design.md's Slicing sections commit to. Slice 1 blocks everything (it
   owns the only migration); the real dependency graph is looser than the
   linear chain (4 and 5 need only 1+2, not 3), but `chain_strategy:
   stacked-to-main` (state.yaml) commits to sequential integration, so
   every slice branches off the previous slice's merged tip. Format
   reference: the archived `2026-08-17-stage-14-auditoria-trazabilidad/
   tasks.md` structure — per-slice Start/Finish/Rollback, hierarchical
   task numbering, `[P]` for parallelizable test tasks, a Verify line, and
   a closing Review Workload Forecast.
2. **DB gate is `UNA-MIGRACION-APROBADA`** (`state.yaml`) — slice 1 carries
   **exactly one** new migration, named `CuentaCorrienteDeProveedoresEtapa15`,
   matching the proposal's gate §A-§D verbatim (1 enum type/4 values, 1
   table/12 columns/1 PK/6 FKs/1 CHECK/6 indexes/standard RLS, 2 ALTERs,
   2 idempotent data statements, RLS ordered last). Slices 2-6 each carry a
   gate-guard task requiring `dotnet ef migrations has-pending-model-changes`
   clean **and** zero new files under
   `src/Ways.Infrastructure/Persistencia/Migraciones/` in `git diff --stat`.
   Any slice that finds itself needing schema change STOPs and reopens the
   gate. **Binding count**: the migration's total new index count MUST be
   **7** (6 named on the new table + 1 implicit from the `gastos` alternate
   key) — anything else reopens the gate per `state.yaml`'s
   `db_gate_approval` "CRITERIO DE VERIFY VINCULANTE".
3. **Pre-authorized cut points**, inherited verbatim from design.md's
   Slicing section:
   - `1a`/`1b` — split at the DDL/proof boundary if slice 1 overflows:
     `1a` (migration + entity + config + RLS/CHECK tests), `1b` (fidelity +
     idempotency tests + doc-10). Keeps **one** migration — the invariant
     that must not be degraded.
   - `2a`/`2b` — split at the write-path boundary if slice 2 overflows:
     `2a` (writer class + widened `RETURNING`s + the `compra` movement on
     confirm), `2b` (the anulación contramovimiento, its pre-cutover
     fallback and its races).
   - `4a`/`4b` — split at the read/re-sourcing boundary if slice 4
     overflows: `4a` (estado de cuenta), `4b` (the `/saldo` re-sourcing
     and its byte-compatibility proof).
   - Slice 6's ajuste modal is droppable (the endpoint still serves the
     operation) — a documented reduction, never silent.
   **Never degraded**: the backfill fidelity proof, the single-write-
   authority containment, the anulación contramovimiento, and the
   pre-cutover `impaga`/`parcial` cases (decision 4 below). A coverage
   slice splits, it is never trimmed.
4. **CONFLICT FOUND AND RESOLVED #1 — per-compra payment status formula.**
   `spec.md` and `design.md` ran **in parallel** during this planning pass.
   `cuenta-corriente-de-proveedores/spec.md`'s "Per-Compra Payment Status
   Is Derived From Imputed Movements" and `saldo-de-proveedor/spec.md`'s
   MODIFIED requirement both transcribe the **proposal's** formula
   (`SUM(importe) WHERE id_comprobante_compra = X` over the compra's own
   `+total` movement plus every movement imputed to it, `<= 0 ⇒ pagada`).
   `design.md` decision 8 (`design.md:60`) uses a **different** formula
   (`pagado = −Σ importe WHERE tipo <> 'compra'`), and its own Open
   Questions section (`design.md:485-492`) already flags the disagreement
   without resolving it, asking `sdd-tasks` to reconcile. **Both were
   arbitrated and REJECTED during the design phase** (`state.yaml` OD7):
   the proposal's formula reads a pre-cutover compra (no own `compra`
   movement — its debt lives inside the `apertura`, decision 1) as
   `pagada` when it should be `impaga`; the design's formula loses a
   pre-cutover **partial** payment because it never queries `gastos` at
   all. **Binding formula (state.yaml OD7)**:
   `pagado(X) = Σ importe of gastos linked to X (WHERE id_comprobante_compra
   = X AND deleted_at IS NULL — the retired mechanism, valid for ALL time
   because the payment is STILL a gasto, proposal decision 2)
   + Σ(−importe) of movimientos tipo = 'ajuste' with id_comprobante_compra
   = X (imputed contramovimientos and manual ajustes)`, fed to the
   existing, unchanged `ResolverEstadoPago(pagado, total)`. Movimientos
   tipo `'pago'` are **never** counted in the ledger term — they would
   double-count a gasto already counted in the first term. `sdd-apply`
   MUST implement this formula (task 4.5), not spec.md's literal SQL nor
   design.md's decision-8 shape. The spec's OBSERVABLE outcomes (a fully
   imputed compra reads `pagada`, a partial one `parcial`, an unimputed
   payment reduces the total saldo without settling any compra) still
   hold under OD7 — only the query differs, and it additionally fixes the
   two pre-cutover cases neither prior formula got right (tasks 4.12,
   4.13). Mutation target #24 (design.md's slice-4 row) is **redefined**
   accordingly — see decision 8 below.
5. **CONFLICT FOUND AND RESOLVED #2 — estado de cuenta pagination.**
   `cuenta-corriente-de-proveedores/spec.md`'s "Estado De Cuenta Lists
   Movements With A Running Balance And Date Filter" requirement describes
   an **unpaginated** movement list (stage-7's shape: "MUST return every
   movement ordered by fecha DESCENDING" — no page/tamanio/`COUNT`).
   `design.md` decision 10 (`design.md:62`) mandates **paginación**
   (`PaginaDe*`, `OFFSET`, `ORDER BY fecha DESC, id_movimiento DESC`
   tiebreaker), citing the stage-13/14 unbounded-growth criterion and the
   tied-`fecha` hazard a `RelojFijo` fixture creates by construction.
   `design.md`'s own Open Questions (`design.md:496-499`) flags this
   exact risk and asks for reconciliation here. **`state.yaml` OD9 is
   authoritative: the estado de cuenta ships PAGINATED.** Tasks 4.2-4.3
   build `PaginaDeEstadoDeCuentaDeProveedor` (Header, Items, Total,
   Pagina, Tamanio, Historico, Desde, Hasta); task 4.8 proves the
   `id_movimiento DESC` tiebreaker is load-bearing under a tied-`fecha`
   `RelojFijo` fixture — precisely the scenario an unpaginated read cannot
   fail (and therefore cannot prove).
6. **Anulación pre-cutover fallback — ratified, registered so its absence
   from spec.md is not read as an omission.** `proposal.md` decision 5
   pins the contramovimiento's `importe = −(the compra movement's
   importe)` and never states the zero-rows case. `design.md` decision 6
   supplies the fallback (`importe = −total` from the widened `RETURNING`,
   with a naming `detalle`) for a compra confirmed before the cutover
   (no own `compra` movement — the whole population the backfill exists
   for). `state.yaml` OD8 approves this fallback outright — **not a
   conflict**, but neither `comprobantes-compra/spec.md` nor
   `cuenta-corriente-de-proveedores/spec.md`'s anulación requirement
   states it (both describe only "of magnitude equal to the original
   compra movement"). Tasks 2.8 and 2.14 implement and test it.
7. **Confirm × pago rendezvous sequencing — registered, not a conflict.**
   `design.md`'s Slicing table (`design.md:432`) lists "confirm × pago
   rendezvous" in slice 2's test plan, even though the real gasto-driven
   pago write path (`ServicioDeGastos.InsertarGastoAsync`) does not exist
   until slice 3. Resolved by reading it literally: task 2.17 proves
   `EjecutarConfirmarAsync`'s OWN lock placement (mutation target #19) by
   racing it against a **direct call to the slice-2 writer class**
   (`EscriturasDeCuentaCorrienteProveedor`) shaped like a payment — not
   the full `ServicioDeGastos` business wiring, which slice 3 does not yet
   expose. Slice 3 additionally proves `pago × pago` and
   `anulación × pago` through the REAL call site once it exists (tasks
   3.8-3.9). No business claim changes — only which call proves which
   mutation, at which slice.
8. **`mutation-proof-tests` compliance**: the **28** named mutation
   targets in design.md's table are each placed in exactly one slice
   below, per design's own "Slice" column: 1 → 11 targets (1-11), 2 → 9
   (12-20), 3 → 3 (21-23), 4 → 3 (24-26), 5 → 1 (27), 6 → 1 (28); sum
   11+9+3+3+1+1 = 28. **Target #24 is REDEFINED** per decision 4 above:
   design.md's literal clause (`pagado = −Σ importe WHERE tipo <>
   'compra'`) is the REJECTED formula, so the target now proves the
   binding formula's `tipo = 'ajuste'` filter (excluding `'pago'`
   movements from the ledger term, to avoid double-counting the `gastos`
   sum) — widening it to `tipo <> 'compra'` (re-including `pago`) must
   make a discriminating double-count test fail (task 4.13's shape).
   Every target requires apply-time evidence (mutation applied → named
   failing test → reverted → green) recorded in its slice's PR body. The
   non-regression `VentasCheckoutTests` row (design's unnumbered "—" row)
   is a binding verify criterion, task 2.27, **not** one of the 28.
9. **`db-error-backstops` applies at three slices**: slice 1 (the
   `fk_..._tenant`/`fk_..._empleado`/`fk_..._gasto` exemptions and the
   `ck_..._apertura` CHECK, all raw-insert provable with no call site yet
   — the stage-14 task-1.28 precedent), slice 3 (`fk_..._comprobante_compra`,
   reachable via the gasto link, already pre-checked by
   `ExigirCompraLigableAsync` under `FOR SHARE` — a race test), and slice
   5 (`fk_..._proveedor` and `fk_..._punto_venta`, both reachable from the
   ajuste endpoint's client input). No new `23505` family exists in this
   stage (the `gastos` alternate key is structurally unviolable) —
   `ManejadorDeErrores.cs` stays unmodified.
10. **`react-async-state` + `web-descriptor-tests` apply to slice 6 only**
    — the single web-touching slice in this stage.
11. **`dto-contract-honesty` applies at slices 4 and 5** — the two slices
    that introduce a data contract: slice 4's `ContratosDeProveedor.cs`
    (`MovimientoDeCuentaDeProveedor`/`EstadoDeCuentaDeProveedorHeader`/
    `PaginaDeEstadoDeCuentaDeProveedor`), and slice 5's
    `SolicitudDeAjusteDeProveedor` — explicitly **without** a `tipo` or
    `saldoResultante` field (design decision 15): no field is accepted and
    silently dropped, and no endpoint accepts a client-computed saldo or
    delta.
12. **`work-unit-commits` applies to every slice** — each slice's
    implementation tasks land as logical, reviewable commits, not one
    monolithic diff per branch.
13. **Test dates are fixed, never wall-clock-relative.** Every date-bearing
    test pins the clock at `RelojFijo(2026-08-17T12:00:00Z)` (mediodía
    UTC — matches the `desde`/`hasta` scenario's own fixed seed in
    `cuenta-corriente-de-proveedores/spec.md`), so `fecha`/`saldo_resultante`
    assertions are exact equalities, never range checks, stable in UTC and
    `America/Argentina/Buenos_Aires`. **Exception, registered**: the
    migration's `apertura` row stamps `fecha` with Postgres `now()` — no
    `IRelojDelSistema` exists in migration context (proposal.md:605,
    "NOTA ACEPTADA"); slice 1's fidelity/idempotency tests assert
    `importe`/`saldo_resultante` equality, never `fecha` against
    `RelojFijo`. Every OTHER movement (compra, pago, ajuste, manual
    ajuste) is written by `EscriturasDeCuentaCorrienteProveedor` under
    `IRelojDelSistema` and IS asserted against `RelojFijo` exactly (tasks
    2.15, 3.x, 5.x). At least one estado-de-cuenta boundary test sends the
    **client's real `-03:00` offset**, never `Z` (`mutation-proof-tests`
    rule 10), asserting both the returned rows and the displayed período —
    the only shape that can see a raw-ADO UTC-normalization regression
    (stage-14 verify W2, PR #129's own lesson).
14. **Process rule (stage-12 discipline, carried forward): every deviation
    `sdd-apply` takes from this plan is registered IN `tasks.md`** — as a
    task-level note or a new numbered decision here — never left to
    verify-phase archaeology.

**Not a new conflict, no action required** (already resolved in earlier
phases, restated for continuity): the `/export` sibling is OUT of scope
(state.yaml OD6/T1, spec phase); `POST /ajustes` mapped top-level is
APPROVED (design decision 12, T5); the design's word-budget overage on the
new capability's spec is advisory (state.yaml T2); the MODIFIED-not-RENAMED
convention for the per-compra requirement's title is approved (state.yaml
T3); `EscriturasDeCuentaCorriente.cs`'s own un-migrated `AgregarParametro`
is explicitly OUT of this stage's scope (design.md:511-515) — no task below
touches it.

---

## Suggested Work Units

| Unit | Goal | Branch | Depends on | ~Lines |
|---|---|---|---|---|
| 1 | Migration `CuentaCorrienteDeProveedoresEtapa15` (enum, table, 6 FKs, 6 indexes, CHECK, both ALTERs, both data statements, RLS last) + entity + EF config + `MapEnum` in both builders + cloned tenant filter + doc-10 | `feat/stage15-slice1-ledger-schema` | none | ~450 |
| 2 | `EscriturasDeCuentaCorrienteProveedor` (writer + validator) + the `compra` movement on confirm + the reversing `ajuste` on anulación (pre-cutover fallback) + pinned lock order | `feat/stage15-slice2-escrituras-y-deuda` | 1 | ~410 |
| 3 | The `pago` movement inside `InsertarGastoAsync` + imputación + predicate scenarios + races | `feat/stage15-slice3-pago-por-gasto` | 2 | ~330 |
| 4 | `ServicioDeCuentaCorrienteDeProveedor` read half + `GET .../cuenta-corriente` (paginated) + `ServicioDeSaldoDeProveedor` re-sourced with the OD7 formula | `feat/stage15-slice4-estado-de-cuenta` | 3 (chained; needs only 1+2) | ~420 |
| 5 | `SupervisionDeCuentaDeProveedor` policy + `POST .../ajustes` (top-level) | `feat/stage15-slice5-ajuste-manual` | 4 | ~260 |
| 6 | `CuentaCorrienteDeProveedor.tsx` + ajuste modal + `ResumenSaldoDeProveedor.tsx` + entry points | `feat/stage15-slice6-web` | 5 (needs 4+5) | ~380 |

Total ≈ **2 250 lines**. `Decision needed before apply: No` —
`auto-chain` + `stacked-to-main` already resolved in `state.yaml`.

---

## Slice 1: Ledger schema (PR 1)

**Start**: `main`. **Finish**: `movimientos_cuenta_corriente_proveedor`
exists with standard RLS and 7 total new indexes; `proveedores.saldo` and
the `gastos` alternate key exist; the backfill has run and is proven
faithful to the retired formula and idempotent; doc-10 carries the table.
No write path calls the writer yet (it doesn't exist until slice 2).
**Rollback**: revert the branch — `DROP TABLE
movimientos_cuenta_corriente_proveedor` → `ALTER TABLE proveedores DROP
COLUMN saldo` → `ALTER TABLE gastos DROP CONSTRAINT
ak_gastos_id_gasto_id_tenant` → `DROP TYPE tipo_movimiento_cc_proveedor`;
no dependent object, no rewritten row (proposal Rollback Plan).
**Done** = tests green + `judgment-day` clean round + PR merged.

**Budget note**: pre-authorized split `1a`/`1b` if this slice overflows —
see decision 3 above. The migration must not ship in a slice that might be
dropped.

**Deviations registered during `sdd-apply` (stage-12 discipline, decision 14
above):**

15. **Task 1.10's literal order for `ALTER TABLE gastos` is SQL-infeasible —
    corrected to follow the proposal instead, per the task's own escape
    clause ("si el proposal internamente ordena distinto ALTER gastos, seguí
    al proposal").** Task 1.10 as written places `ALTER gastos` AFTER `ALTER
    proveedores` + statement 2 and BEFORE RLS. But
    `fk_movimientos_cuenta_corriente_proveedor_gasto` is declared INSIDE the
    same `CREATE TABLE` that comes earlier in the same ordering — Postgres
    requires the referenced composite unique constraint
    (`ak_gastos_id_gasto_id_tenant`) to exist BEFORE the FK that references
    it is created, or the migration fails with `42830` ("there is no unique
    constraint matching given keys for referenced table 'gastos'") —
    reproduced empirically while running the fidelity/idempotency tests
    against `WaysApiFixture`. proposal.md's own explicit ordering note
    (`proposal.md:663-668`) does not mention `ALTER TABLE gastos` in its
    list at all, so there is no literal proposal text to contradict.
    Resolution: `ak_gastos_id_gasto_id_tenant` is added immediately after
    the enum `AlterDatabase` and BEFORE `CREATE TABLE` — the same position
    `dotnet ef migrations add` chose on its own (correct topological order:
    prerequisites before dependents). Binding order actually shipped:
    `CREATE TYPE` → `ALTER TABLE gastos ADD CONSTRAINT ak_...` → `CREATE
    TABLE` (with all 6 FKs/indexes) → backfill statement 1 (1.6) → `ALTER
    TABLE proveedores` + statement 2 (1.7) → `HabilitarRlsDeTenant` (LAST).
    The invariant the gate actually cares about — RLS enabled LAST, the
    backfill running before RLS, and the final index count = 7 — is
    unaffected by this reordering.
16. **Mutation target #4 (task 1.28) is a provably equivalent mutant at
    runtime — resolved with a source-text assertion instead of a behavioral
    one, per `mutation-proof-tests` rule 3 exhausted first.** The backfill's
    `derivado` CTE is rooted at `proveedores` (`FROM proveedores p LEFT JOIN
    (...) g ON g.id_proveedor = p.id_proveedor`); under SQL NULL semantics no
    real (NOT NULL) `p.id_proveedor` can ever match a `g.id_proveedor IS
    NULL` group, so deleting `id_proveedor IS NOT NULL` from the gastos
    subquery changes NOTHING observable in ANY artifact the migration
    produces (no ledger row, no `proveedores.saldo` change) — confirmed
    empirically twice: (1) the end-to-end fidelity test passes unchanged
    under the mutation; (2) a first attempt at a "routed below the confound"
    test (mutation-proof-tests rule 3) that reconstructed the subquery
    fragment BY HAND inside the test also passed under the mutation, because
    it executed the correct hand-written SQL, never the actual (mutated)
    migration text — a test-authoring mistake caught by re-running the
    mutation against it. Since the predicate lives inside one embedded SQL
    string literal with no separately-invokable component, there is no
    runtime seam to route the test below. Final resolution:
    `CuentaCorrienteProveedorBackfillTests.ElTextoFuenteDeLaMigracionConservaElFiltroIdProveedorNoNuloTarget4`
    reads the actual migration `.cs` file from disk and asserts the literal
    predicate text is present — a source-text (golden-text style) assertion,
    the only mechanism that can detect this specific deletion. Both mutation
    runs (behavioral attempt + final source-text test) are recorded as
    apply-time evidence for target #4.
17. **Mutation target #11 (task 1.35) is also a provably equivalent mutant at
    runtime against `WaysApiFixture` — resolved with a source-text ordering
    assertion instead of a behavioral one, per `mutation-proof-tests` rule 3
    exhausted first.** `ways_owner` (the connection `WaysApiFixture` uses to
    run migrations) is created as a Postgres SUPERUSER inside the
    Testcontainers container, and a Postgres superuser ALWAYS bypasses RLS —
    even with `FORCE ROW LEVEL SECURITY` — regardless of statement order.
    The migration's own comment already names this exact risk ("depending on
    RLS bypass to write the backfill would make the migration's correctness
    rest on `ways_owner` being a superuser — a known carryover weakness, not
    a foundation") — moving `HabilitarRlsDeTenant` before the backfill
    changes NO observable artifact under this fixture (confirmed
    empirically: both the fidelity and idempotency tests pass unchanged
    under the mutation). Final resolution:
    `CuentaCorrienteProveedorBackfillTests.ElTextoFuenteDeLaMigracionOrdenaRlsDespuesDelBackfillTarget11`
    reads the actual migration `.cs` file and asserts `HabilitarRlsDeTenant`'s
    text index is AFTER both backfill statements' text indices — the only
    mechanism that can detect this specific reordering given the documented
    superuser carryover.
18. **`fk_movimientos_cuenta_corriente_proveedor_tenant`'s exemption test
    (task 1.24) cannot assert the exact constraint name.** Because
    `id_proveedor` is `NOT NULL` and its FK is composite
    (`id_proveedor, id_tenant`), any row with a bogus `id_tenant` also
    necessarily breaks `fk_..._proveedor` (no `(id_proveedor, id_tenant)`
    pair can match). Postgres reports exactly one violated constraint per
    statement — empirically `fk_..._proveedor`, matching this table's FK
    declaration order — so isolating `fk_..._tenant` specifically is
    structurally impossible on a composite-keyed table. The test
    (`CuentaCorrienteProveedorSchemaTests.UnIdTenantInexistenteViolaAlgunaFkGenerica23503`)
    asserts SQLSTATE `23503` and the `fk_movimientos_cuenta_corriente_proveedor_`
    prefix instead — the actual thing `ManejadorDeErrores.cs`'s generic
    mapping classifies on.

- [x] 1.1 Create the migration `CuentaCorrienteDeProveedoresEtapa15`:
  `CREATE TYPE tipo_movimiento_cc_proveedor AS ENUM ('apertura', 'compra',
  'pago', 'ajuste')`, declaration order = C# member order. *(proposal §A,
  `proposal.md:499-508`)*
- [x] 1.2 Same migration: `CREATE TABLE
  movimientos_cuenta_corriente_proveedor` — 12 columns exactly per §B
  (`id_movimiento` `integer GENERATED BY DEFAULT AS IDENTITY`, `id_tenant`,
  `id_proveedor`, `fecha timestamptz NOT NULL` **no DEFAULT**,
  `id_punto_venta NULL`, `id_empleado NULL`, `tipo`,
  `id_comprobante_compra NULL`, `id_gasto NULL`, `importe numeric(14,2)`,
  `saldo_resultante numeric(14,2)`, `detalle NULL`);
  `pk_movimientos_cuenta_corriente_proveedor`. *(proposal §B,
  `proposal.md:524-541`)*
- [x] 1.3 Same migration: 6 named FKs exactly per §B's table —
  `fk_..._tenant`, `fk_..._proveedor` (composite, AK already exists),
  `fk_..._punto_venta` (composite, MATCH SIMPLE, nullable),
  `fk_..._empleado` (simple, not composite — the platform-staff NULL
  sentinel reason), `fk_..._comprobante_compra` (composite, nullable, AK
  already exists), `fk_..._gasto` (composite, nullable, **requires §D's
  new AK**) — all `RESTRICT`. *(proposal.md:552-557)*
- [x] 1.4 Same migration: `CHECK ck_movimientos_cuenta_corriente_proveedor_apertura`
  — `(tipo = 'apertura' AND id_punto_venta IS NULL AND id_empleado IS
  NULL) OR (tipo <> 'apertura' AND id_punto_venta IS NOT NULL AND
  id_empleado IS NOT NULL)`. *(proposal.md:558)*
- [x] 1.5 Same migration: 6 named indexes exactly per §B's table —
  `ix_..._tenant`, `ix_..._proveedor_fecha` (covers FK2 by prefix),
  `ix_..._comprobante_compra` (covers FK5), `ix_..._punto_venta` (covers
  FK3), `ix_..._empleado` (simple, covers FK4), `ix_..._gasto` (covers
  FK6). Zero EF-autogenerated FK-support index beyond these 6 — declare
  all six explicitly, per design decision 16 (the stage-14 gate-amendment
  lesson: `ForeignKeyIndexConvention` re-adds a support index for any
  uncovered FK even if removed by hand). *(proposal.md:570-585, design
  decision 16)*
- [x] 1.6 Same migration, in order after the table/FKs/indexes: the
  backfill's statement 1 — `WITH derivado AS (...) INSERT INTO
  movimientos_cuenta_corriente_proveedor (...) SELECT ... FROM derivado d
  WHERE d.saldo <> 0 AND NOT EXISTS (...)`, exactly the SQL of proposal §C
  — `deleted_at IS NULL` on `comprobantes_compra`/`gastos`/`proveedores`,
  `estado = 'confirmada'`, `categoria = 'proveedor' AND id_proveedor IS
  NOT NULL`, `now()` (no `IRelojDelSistema` in migration context, accepted
  per decision 13 above). *(proposal.md:606-635)*
- [x] 1.7 Same migration: the backfill's statement 2 — `UPDATE proveedores
  p SET saldo = m.saldo_resultante FROM
  movimientos_cuenta_corriente_proveedor m WHERE ... AND m.tipo =
  'apertura' AND p.saldo <> m.saldo_resultante` — the cache derived FROM
  the row statement 1 just wrote, never recomputed independently.
  *(proposal.md:637-645)*
- [x] 1.8 Same migration: `ALTER TABLE proveedores ADD COLUMN saldo
  numeric(14,2) NOT NULL DEFAULT 0` (metadata-only, no CHECK — a negative
  saldo is a legitimate credit, decision 5). *(proposal §C,
  `proposal.md:589-596`)*
- [x] 1.9 Same migration: `ALTER TABLE gastos ADD CONSTRAINT
  ak_gastos_id_gasto_id_tenant UNIQUE (id_gasto, id_tenant)` — verified
  absent from `GastoConfiguration.cs`, structurally unviolable (`id_gasto`
  already unique via `pk_gastos`). *(proposal §D, `proposal.md:670-684`)*
- [x] 1.10 Migration ordering is part of the contract, verify in the
  generated file: `CREATE TYPE` → `CREATE TABLE` → FKs/indexes →
  **backfill (1.6, 1.7)** → `ALTER TABLE proveedores` + statement 2 →
  `HabilitarRlsDeTenant("movimientos_cuenta_corriente_proveedor")` **LAST**
  — the policy is `FORCE`d and the migration connection has no
  `app_tenant_actual()` GUC set; RLS running before the backfill would
  make correctness rest on the `ways_owner`-superuser carryover.
  *(proposal.md:663-668)*
- [x] 1.11 Create `src/Ways.Domain/CuentaCorriente/TipoMovimientoCcProveedor.cs`
  — `enum { Apertura, Compra, Pago, Ajuste }`, member order = the native
  type's declared order (`npgsql.MapEnum<T>()`). *(design.md:123-125)*
- [x] 1.12 Create
  `src/Ways.Domain/CuentaCorriente/MovimientoCuentaCorrienteProveedor.cs`
  — immutable, no `EntidadBase` inheritance (no `updated_at`, no soft
  delete), no `EntidadTenant` (the `IdTenant` is written explicitly, never
  `EstamparTenant()`). *(design.md:127-129, mirrors
  `MovimientoCuentaCorriente.cs:17-56`)*
- [x] 1.13 Create
  `src/Ways.Infrastructure/.../Configuraciones/MovimientoCuentaCorrienteProveedorConfiguration.cs`
  — mirrors `MovimientoCuentaCorrienteConfiguration.cs:18-133` minus the
  alternate key, the self-FK and its support index; declares all 6
  support indexes by hand with doc-10 names. *(design decision 16,
  `design.md:68, 323`)*
- [x] 1.14 Modify `GastoConfiguration.cs` —
  `HasAlternateKey(g => new { g.Id, g.IdTenant })`. *(design.md:324, gate
  §D)*
- [x] 1.15 Modify `ProveedorConfiguration.cs` — `saldo numeric(14,2)`.
  *(design.md:325)*
- [x] 1.16 Modify `src/Ways.Infrastructure/Persistencia/WaysDbContext.cs`
  — `DbSet<MovimientoCuentaCorrienteProveedor>` +
  `AplicarFiltroDeTenantEnMovimientoCuentaCorrienteProveedor`, cloned from
  the `MovimientoStock` one (stage-14 decision-7 pattern: `id_tenant`
  written explicitly by the writer). *(design.md:326)*
- [x] 1.17 Modify `WaysDbContextFactory.cs` **and**
  `DependencyInjection.cs` — `MapEnum<TipoMovimientoCcProveedor>` in BOTH
  option builders, never also `HasPostgresEnum`. *(design.md:327, proposal
  §A)*
- [x] 1.18 Modify `src/Ways.Application/Abstracciones/IWaysDbContext.cs` —
  `DbSet<MovimientoCuentaCorrienteProveedor>`. *(design.md:328)*
- [x] 1.19 Modify `docs/10-modelo-de-datos.md` — the new table (a
  §8-adjacent subsection), `proveedores.saldo` in §2, the "Estado (Etapa
  15)" annotation, and the retirement of the "saldo derivado,
  deliberadamente simple" note at doc-10:832-834. Landed from inside this
  slice (stage-12 task-1.17 discipline). *(proposal In Scope,
  `proposal.md:66-68`)*
- [x] 1.20 [P] Integration — **backfill fidelity BY DATA (the stage's
  flagship)**: a fixture mixing, per proveedor: borrador/confirmada/anulada
  compras, linked/unlinked gastos, a `categoria = proveedor` gasto with
  `id_proveedor IS NULL`, a soft-deleted compra, a soft-deleted gasto, a
  soft-deleted proveedor, and a proveedor with no history. Capture
  `ServicioDeSaldoDeProveedor.ObtenerAsync` **before** the migration per
  proveedor, migrate, assert `proveedores.saldo == saldoPrevio` per
  proveedor **and** the `apertura` row's `importe == saldo_resultante ==
  saldoPrevio`. *(design.md:373)*
- [x] 1.21 [P] Integration — idempotency/no-op: re-running the migration
  writes **no** additional `apertura` row and changes **no** saldo; a
  proveedor with zero derived saldo gets **no** row and keeps `saldo = 0`.
  *(design.md:374)*
- [x] 1.22 [P] Integration — RLS on `ways_app` (NOSUPERUSER NOBYPASSRLS):
  cross-tenant `SELECT` returns **0** rows; an `INSERT` with a foreign
  `id_tenant` is refused `42501`. *(design.md:375, mutation-proof-tests
  rule 5)*
- [x] 1.23 [P] Integration — the CHECK: a raw `INSERT` of `tipo =
  'apertura'` **with** a PV/empleado, and of `tipo = 'compra'` **without**
  them, both rejected `23514`. *(design.md:376, mutation-proof-tests rule
  4)*
- [x] 1.24 [P] `db-error-backstops` — exemption tests for `fk_..._tenant`
  (session-derived), `fk_..._empleado` (server-derived, `usuarios`
  soft-deleted so never physically removed), `fk_..._gasto` (id of a row
  the same transaction just inserted, once slice 2/3 wire it — provable
  now via a raw insert against a non-existent `id_gasto`): raw-insert
  SQLSTATE `23503` proven per FK, the generic `fk_`/`23503` → `400
  referencia_invalida` mapping (`ManejadorDeErrores.cs:224`) confirmed
  unmodified. *(proposal §E)*
- [x] 1.25 [P] **Mutation target #1** — `deleted_at IS NULL` on
  `comprobantes_compra` in the backfill → delete it → the fidelity test's
  soft-deleted-compra case (1.20) must fail.
- [x] 1.26 [P] **Mutation target #2** — `deleted_at IS NULL` on `gastos`
  in the backfill → delete it → the fidelity test's soft-deleted-gasto
  case (1.20) must fail.
- [x] 1.27 [P] **Mutation target #3** — `deleted_at IS NULL` on
  `proveedores` in the backfill → delete it → the fidelity test's
  soft-deleted-proveedor case (1.20, "gets no row") must fail.
- [x] 1.28 [P] **Mutation target #4** — `id_proveedor IS NOT NULL` in the
  backfill's `gastos` predicate → delete it → the fidelity test's
  `id_proveedor IS NULL` gasto case (1.20) must fail.
- [x] 1.29 [P] **Mutation target #5** — `estado = 'confirmada'` in the
  backfill → widen to any estado → the fidelity test's borrador+anulada
  case (1.20) must fail.
- [x] 1.30 [P] **Mutation target #6** — `WHERE d.saldo <> 0` → delete it
  → "a proveedor with no history gets no row" (1.21) must fail.
- [x] 1.31 [P] **Mutation target #7** — the `NOT EXISTS (...)` idempotency
  guard → delete it → re-run writes a second `apertura` row (1.21) must
  fail.
- [x] 1.32 [P] **Mutation target #8** — statement 2 deriving the cache
  FROM the row of statement 1 → recompute it independently → the fidelity
  test (1.20, "both must agree by construction") must fail.
- [x] 1.33 [P] **Mutation target #9** —
  `HabilitarRlsDeTenant("movimientos_cuenta_corriente_proveedor")` →
  delete the line → the cross-tenant row count **and** the `42501` INSERT
  test (1.22) must both fail.
- [x] 1.34 [P] **Mutation target #10** — the `ck_..._apertura` CHECK in
  the migration → delete it → the raw-insert `23514` test (1.23, both
  directions) must fail.
- [x] 1.35 [P] **Mutation target #11** — RLS ordered LAST in the migration
  → move it before the backfill → the migration fails or the backfill
  writes zero rows (1.20/1.21 regress).
- [x] 1.36 Gate guard (**VINCULANTE**, `state.yaml` db_gate_approval):
  `git diff --stat main -- src/Ways.Infrastructure/Persistencia/Migraciones/`
  shows **exactly one** new file, named for
  `CuentaCorrienteDeProveedoresEtapa15`; `dotnet ef migrations
  has-pending-model-changes` reports no pending changes; the final new
  index count on `movimientos_cuenta_corriente_proveedor` + the implicit
  `gastos` AK index = **7**. Any deviation reopens the gate.
- [x] 1.37 Run `judgment-day` on the slice diff; fix confirmed issues;
  re-judge until clean.
- [x] 1.38 Branch `feat/stage15-slice1-ledger-schema` off `main`; PR;
  merge stacked-to-main.

**Test plan**: fidelity by data (1.20), idempotency (1.21), RLS + `42501`
(1.22), `23514` both directions (1.23), FK exemptions (1.24), 11 mutation
targets (1.25-1.35).

**Verify**: `dotnet test --filter FullyQualifiedName~CuentaCorrienteProveedor`

---

## Slice 2: Escrituras + deuda (PR 2)

**Start**: slice 1 merged. **Finish**: `EscriturasDeCuentaCorrienteProveedor`
exists (both statements + validator, UTC-normalized); confirming a compra
writes exactly one `compra` movement; anulándola writes exactly one
reversing `ajuste` (with the pre-cutover fallback); `proveedores` is
verified the LAST row lock in both paths. **Rollback**: revert the branch —
the call sites disappear, the table stays intact and unused for these
paths (append-only, nothing to repair). **Done** = tests green +
`judgment-day` clean round + PR merged.

**Budget note**: pre-authorized split `2a`/`2b` if this slice overflows —
see decision 3 above.

**Deviations registered during `sdd-apply` (stage-12 discipline, decision 14
above):**

19. **`MarcarAnuladaAsync`'s `RETURNING` is corrected to include `total`,
    not only `id_punto_venta, id_proveedor`.** Design decision 4's literal
    column list (`design.md:56`) omits `total`, but the Transactions
    section's own step 5 for anulación (`design.md:201`) reads
    `importeOriginal := total del RETURNING` for the pre-cutover fallback
    — there is no other RETURNING in that transaction to read it from. The
    same widening rationale decision 4 gives for `ConfirmarHeaderAsync`
    ("zero extra round trips") applies identically here: `total` does not
    change once a compra is confirmed (spec `comprobantes-compra`:
    "confirmada... MUST be immutable"), so reading it under this same lock
    is exactly as authoritative as `id_punto_venta`/`id_proveedor`. Shipped
    as `RETURNING id_punto_venta, id_proveedor, total`.
20. **Three pre-existing integration test files were missing
    `MapEnum<TipoMovimientoCcProveedor>` in their manually-curated
    `DbContextOptionsBuilder<WaysDbContext>`** — a gap opened by slice 1
    (which added the enum) but not exercised until this slice's own code
    path (`ConfirmarAsync`/`AnularAsync`) started writing to the ledger:
    `ComprasAnulacionYConcurrenciaTests.CrearContextoConContador` (that
    suite's own command-budget harness, inherited from an earlier stage —
    surfaced immediately as an
    `InvalidCastException` writing the enum parameter), and, discovered
    only by running the FULL integration suite once (per the "una sola
    corrida" rule) rather than a filtered subset:
    `ComprasTipoSeedTests.LosTiposDeCompraAterrizanEnUnaBaseYaMigradaDesdeStage7...`
    and
    `CuentaCorrienteEtapa7BackstopTests.RcResuelveEnUnaBaseYaMigradaDesdeStage6SinDuplicar`
    (both migrate a fresh database to HEAD and hit
    `PendingModelChangesWarning` because their hand-built model diverges
    from the real migration snapshot without the mapping). Fixed in this
    slice's diff — leaving them red would violate the "tests green" gate
    criterion, and none of the three files needed anything beyond the one
    missing `MapEnum` line.
21. **Mutation target #17's literal runtime scenario is structurally
    unreachable through the real call sites — resolved with a source-text
    assertion, `mutation-proof-tests` rule 3 exhausted first**, same
    criterion as slice 1's targets #4/#11.
    `ServicioDeCompras.ConfirmarAsync`/`AnularAsync` both wrap their
    transactional core in `FabricaDeEstrategiaSinReintento.
    CrearEstrategiaSinReintento` (`maxRetryCount: 0`, `ShouldRetryOn`
    always `false`) — its own doc-comment states this exists PRECISELY so
    a `CreateExecutionStrategy` replay never reaches these statements.
    Design's mutation description ("double-count under a forced
    execution-strategy retry") therefore cannot be forced through either
    real call site without defeating the very isolation this class
    provides. Resolution:
    `EscriturasDeCuentaCorrienteProveedorTests.
    ElTextoFuenteDeActualizarSaldoProveedorAsyncUsaElUpdateAditivoCrudoTarget17`
    reads `EscriturasDeCuentaCorrienteProveedor.cs` from disk and asserts
    both the literal additive SQL text and the absence of any tracked
    `Proveedores`/`SaveChangesAsync` access in that method.
22. **Mutation target #19 could not be proven with a live rendezvous —
    resolved with a deterministic source-text ordering assertion instead**,
    after two runtime attempts were exhausted (`mutation-proof-tests` rule
    2/3). First, a `DbCommandInterceptor` capturing the SQL statement
    sequence was tried and discarded EMPIRICALLY: the raw-ADO statements of
    `EjecutarConfirmarAsync`/`EjecutarAnulacionAsync` are created via
    `conexion.CreateCommand()` directly on `db.Database.GetDbConnection()`,
    bypassing EF Core's command pipeline entirely — `interceptor.Orden`
    stayed empty in the real run. Second, a genuine timing-forced
    "deadlock" is structurally impossible to construct here: design's own
    Concurrency guarantees (`design.md:239-244`) state confirm × pago share
    only ONE lockable resource (the proveedor row) and "neither holds
    anything the other needs after taking it" — with a single shared
    resource there is no second resource to invert for an actual PostgreSQL
    deadlock, under either the correct or the mutated ordering. Resolution:
    `ServicioDeComprasLockOrderTests` (new file,
    `tests/Ways.Application.Tests/Compras/`) reads `ServicioDeCompras.cs`
    from disk and asserts, by text-index order inside each method, that
    the proveedor lock (`ActualizarSaldoProveedorAsync`) appears strictly
    after every stock/costo statement and strictly before the commit, with
    the ledger `INSERT` immediately following it — proving task 2.9's
    pinned order directly and discriminating target #19 (moving the lock
    to "step 1.5" moves its text index before the stock statements,
    confirmed by mutation). Task 2.17's real concurrency test
    (`ConfirmarYUnPagoDirectoAlMismoProveedorSeSerializanSinDeadlock`)
    still ships as the SPEC-level proof that confirm × pago serialize
    without error — it is a behavioral coverage test, not target #19's
    mutation discriminator.
23. **Tasks 2.13 and 2.17's "payment" is simulated by calling
    `EscriturasDeCuentaCorrienteProveedor` directly**, per decision 7 above
    — `ServicioDeGastos`'s real write path does not exist until slice 3. A
    `gastos` row is seeded only to satisfy `id_gasto`'s FK; the ledger
    write itself never goes through `InsertarGastoAsync`.
24. **Judgment-day judge B found task 2.22 / mutation target #16's original
    evidence insufficient — a VALUE-class mutant survived the recorded
    proof.** The only `SaldoResultante` assertion in
    `CuentaCorrienteProveedorEscriturasTests.cs`
    (`ConfirmarUnaCompraEscribeExactamenteUnMovimientoCompraYSubeElSaldo`)
    ran against a FRESH proveedor (saldo previo 0), where
    `saldo_resultante == encabezado.Total` by pure coincidence: replacing
    `nuevoSaldoProveedor` with `encabezado.Total` in
    `EjecutarConfirmarAsync` (`ServicioDeCompras.cs:~482`) passed all 9
    tests. The anulación path (`EjecutarAnulacionAsync:~635-643`) had NO
    assertion on the ajuste movement's `SaldoResultante` at all. Closed
    tests-only (production code is correct — the gap was coverage, not a
    defect): added
    `ConfirmarConDeudaPreviaEscribeElSaldoResultanteDelReturningNoElTotalDeEstaCompra`
    (real prior debt from a previously confirmed compra, ≠ 0 and ≠ this
    compra's total) and widened `AnulandoUnaCompraImpagaReviertaSoloLaDeuda`
    /`AnulandoUnaCompraPreCutoverEscribeElAjusteConElFallback` with a second,
    untouched confirmed compra so the ajuste's resulting saldo is neither 0
    nor equal to the reverted importe. Mutation evidence (two cycles, this
    round): (1) `nuevoSaldoProveedor` → `encabezado.Total` in
    `EjecutarConfirmarAsync` — the new confirm test failed (`2300` expected
    vs `1500` actual), reverted; (2) the ajuste's `saldoResultante` →
    `-importeOriginal` (a local value) in `EjecutarAnulacionAsync` — both
    widened anulación tests failed (`300`/`400` expected vs `-1000`/`-2000`
    actual), reverted. Full `CuentaCorrienteProveedorEscriturasTests` green
    (10/10) after revert.

- [x] 2.1 Create
  `src/Ways.Application/CuentaCorriente/EscriturasDeCuentaCorrienteProveedor.cs`
  — static class, `ActualizarSaldoProveedorAsync`: raw `UPDATE proveedores
  SET saldo = saldo + $1 WHERE id_proveedor = $2 AND id_tenant = $3
  RETURNING saldo` — never a tracked `proveedor.Saldo +=`. *(design.md:
  74-95, decisions 1-2)*
- [x] 2.2 Same file: `InsertarMovimientoCcProveedorAsync` — the ONE
  ledger `INSERT ... RETURNING id_movimiento`. *(design.md:90-107)*
- [x] 2.3 Same file: `ValidarFormaPorTipo` — the 4×3 shape matrix
  (`apertura`/`compra`/`pago`/`ajuste`), `InvalidOperationException`
  (never `ErrorDominio` — a call-site defect, not a client error).
  *(design.md:109-118)*
- [x] 2.4 Every raw-ADO parameter through `ParametrosDeComando.Agregar` /
  `AgregarNulo` (normalizes any `DateTimeOffset` to UTC) — no private
  `AgregarParametro` clone; `EscriturasDeCuentaCorriente.cs`'s own private
  copy is explicitly NOT refactored in this stage. *(design decision 3,
  `design.md:55, 511-515`)*
- [x] 2.5 Modify `ServicioDeCompras.cs`'s `ConfirmarHeaderAsync` (`:663-685`)
  — widen `RETURNING` to `id_punto_venta, id_tipo_comprobante,
  id_proveedor, total`. *(design decision 4, `design.md:56, 184`)*
- [x] 2.6 Modify `ServicioDeCompras.cs`'s `MarcarAnuladaAsync` (`:687-700`)
  — widen `RETURNING` to `id_punto_venta, id_proveedor`. *(design.md:196)*
- [x] 2.7 Modify `ServicioDeCompras.cs`'s `EjecutarConfirmarAsync`
  (`:312-470`), after the costo loop (`:462-465`), immediately before
  commit (`:467`) — step 5: `ActualizarSaldoProveedorAsync(+total)`, the
  LAST lock for update; step 6: `INSERT` the `compra` movement
  (`id_comprobante_compra` = the id, `id_gasto` NULL, `importe = +total`).
  *(design decision 5, `design.md:57, 189-192`)*
- [x] 2.8 Modify `ServicioDeCompras.cs`'s `EjecutarAnulacionAsync`
  (`:504-600`), after the informational `gastosLigados` count (`:594`,
  unchanged) — step 5: `importeOriginal := SUM(importe)` of this compra's
  `compra` movement(s); **0 filas ⇒ pre-cutover fallback**
  `importeOriginal := total` from the widened `RETURNING`, with a naming
  `detalle` (decision 6 above); step 6:
  `ActualizarSaldoProveedorAsync(−importeOriginal)`, the LAST lock; step
  7: `INSERT` the reversing `ajuste` (`id_comprobante_compra` = the id).
  *(design decision 6, OD8 ratified fallback, `design.md:58, 200-204`)*
- [x] 2.9 Confirm the pinned lock order holds unchanged for both paths —
  no existing statement in `EjecutarConfirmarAsync`/`EjecutarAnulacionAsync`
  moves; the proveedor lock lands strictly after the header/lotes/stock
  locks and immediately before the commit. *(design.md:229-230)*
- [x] 2.10 [P] Domain unit — `ValidarFormaPorTipo`: the 4×3 shape matrix,
  one fact per illegal combination. *(design.md:372)*
- [x] 2.11 [P] Integration — confirming a compra writes exactly one
  `compra` movement, `importe = total`, `proveedores.saldo` increases.
  *(spec `cuenta-corriente-de-proveedores`: "Confirming a compra increases
  the proveedor's saldo")*
- [x] 2.12 [P] Integration — anulando an unpaid compra reverses only the
  debt. *(spec scenario: "Anulando an unpaid compra reverses only the
  debt")*
- [x] 2.13 [P] Integration — anulando a fully-paid compra leaves a saldo a
  favor; the linked gasto and its `pago` movement remain untouched
  (requires slice-3's write path — co-locate or defer to slice 3's
  coverage if the pago path isn't testable yet; if deferred, register the
  deferral here per decision 14). *(spec scenario: "Anulando a fully-paid
  compra leaves a saldo a favor")*
- [x] 2.14 [P] Integration — anulando a **pre-cutover** compra (its only
  `compra`-equivalent debt lives in the `apertura` backfill row, no own
  `compra` movement) writes an `ajuste` of `−total` with the fallback
  detalle. *(OD8, design decision 6 fallback — decision 6 above)*
- [x] 2.15 [P] Integration — the `-03:00` offset test: everything under
  `RelojFijo(2026-08-17T12:00:00Z)`, the `compra`/`ajuste` movement's
  `fecha` equals the fixed instant exactly at offset zero AND at a real
  `-03:00` write (mutation-proof-tests rule 10 — a `Z` fixture cannot see
  a raw-ADO UTC-normalization regression, stage-14 verify W2/PR #129).
- [x] 2.16 [P] Integration — fault point: a failure forced at the ledger
  write of `EjecutarConfirmarAsync` leaves `proveedores.saldo`, the
  ledger, **and** the compra's `estado` (still `borrador`) untouched.
- [x] 2.17 [P] Integration — **confirm × pago rendezvous** on the same
  proveedor: race `EjecutarConfirmarAsync` against a direct call to
  `EscriturasDeCuentaCorrienteProveedor` shaped like a payment (decision 7
  above — the real `ServicioDeGastos` wiring lands in slice 3); both
  commit, serialized on the proveedor row, no deadlock.
- [x] 2.18 [P] **Mutation target #12** — `ValidarFormaPorTipo`, `compra`
  requires a comprobante → delete the arm → the Domain fact (2.10) must
  fail.
- [x] 2.19 [P] **Mutation target #13** — `ValidarFormaPorTipo`, `apertura`
  forbids actor/PV → delete the arm → the Domain fact (2.10) must fail.
- [x] 2.20 [P] **Mutation target #14** — `ParametrosDeComando.Agregar` on
  `fecha` → replace with a hand-built parameter without
  `ToUniversalTime()` → the `-03:00` offset test (2.15) must fail.
- [x] 2.21 [P] **Mutation target #15** — `id_proveedor, total` added to
  `ConfirmarHeaderAsync`'s `RETURNING` → read them from `preLectura`
  instead → a confirm-under-concurrent-`PUT` test (the movement's
  importe diverges) must fail.
- [x] 2.22 [P] **Mutation target #16** — the saldo `UPDATE` placed
  **before** the ledger `INSERT` → swap them → `saldo_resultante` no
  longer equals the post-update saldo (2.11 regresses).
- [x] 2.23 [P] **Mutation target #17** — `saldo = saldo + $1` raw →
  replace with a tracked `proveedor.Saldo +=` → double-count under a
  forced `CreateExecutionStrategy` retry.
- [x] 2.24 [P] **Mutation target #18** — `AND id_tenant = $3` in the
  saldo `UPDATE` → delete it → a cross-tenant update test routed BELOW
  RLS (mutation-proof-tests rule 3) must fail.
- [x] 2.25 [P] **Mutation target #19** — the proveedor lock placed
  **after** the stock loop → move it to step 1.5 → the confirm × pago
  rendezvous (2.17) deadlocks/times out.
- [x] 2.26 [P] **Mutation target #20** — the pre-cutover fallback of the
  contramovimiento → remove the fallback (always the ledger sum) →
  annulling a pre-cutover compra (2.14) leaves the debt on the books.
- [x] 2.27 **Non-regression (binding verify criterion, design.md:45-47,
  468-470)**: `tests/Ways.IntegrationTests/VentasCheckoutTests.cs` is
  ABSENT from the stage's diff entirely; no file under
  `src/Ways.Application/Ventas/` appears in this slice's diff. Confirmed
  by `git diff --stat`.
- [x] 2.28 Gate guard: `dotnet ef migrations has-pending-model-changes`
  clean; zero new files under `Migraciones/`.
- [x] 2.29 Run `judgment-day`; fix confirmed issues; re-judge until clean.
- [ ] 2.30 Branch `feat/stage15-slice2-escrituras-y-deuda` off `main`
  (parent: slice 1); PR; merge stacked-to-main.

**Test plan**: validator matrix (2.10), confirm/anulación coverage
(2.11-2.14), the `-03:00` offset test (2.15), fault points (2.16),
confirm × pago rendezvous (2.17), 9 mutation targets (2.18-2.26),
checkout non-regression (2.27).

**Verify**: `dotnet test --filter
FullyQualifiedName~EscriturasDeCuentaCorrienteProveedor|FullyQualifiedName~ServicioDeCompras`

---

## Slice 3: Pago por gasto (PR 3)

**Start**: slice 2 merged. **Finish**: a `categoria = proveedor` gasto with
`id_proveedor` writes exactly one imputed `pago` movement inside
`InsertarGastoAsync`'s existing transaction; `pago × pago` and
`anulación × pago` are proven deadlock-free through the real call site.
**Rollback**: revert the branch — the call site disappears, no schema
change, nothing to repair. **Done** = tests green + `judgment-day` clean
round + PR merged.

- [ ] 3.1 Modify `ServicioDeGastos.cs`'s `InsertarGastoAsync` (`:134-174`),
  after `SaveChangesAsync` (`:169`), before the commit (`:171`): if
  `categoria = proveedor` AND `id_proveedor IS NOT NULL` →
  `ActualizarSaldoProveedorAsync(−importe)` (the LAST lock) →
  `InsertarMovimientoCcProveedorAsync(pago, id_gasto = the row just
  flushed, id_comprobante_compra = the gasto's link or NULL, importe =
  −importe)`. *(design decision 7, `design.md:59, 207-215`)*
- [ ] 3.2 Confirm the turno guard (`:140`) and the arqueo egress term stay
  untouched — no new derivation, no new term. *(design "What does NOT
  change")*
- [ ] 3.3 [P] Integration — a linked proveedor gasto writes one imputed
  `pago`. *(spec `gastos`: "A proveedor-categoria gasto with id_proveedor
  writes one pago movement")*
- [ ] 3.4 [P] Integration — an unlinked proveedor gasto reduces the saldo
  without imputación. *(spec `cuenta-corriente-de-proveedores`: "An
  unlinked proveedor gasto reduces the saldo without imputación")*
- [ ] 3.5 [P] Integration — a non-proveedor categoria, or a proveedor
  categoria with `id_proveedor IS NULL`, writes ZERO movements (both
  directions). *(spec `gastos`: "A proveedor-categoria gasto with no
  id_proveedor writes no movement")*
- [ ] 3.6 [P] Integration — a gasto still requires an open turno
  regardless of the ledger write; `409 turno_no_abierto` writes no
  movement. *(spec `gastos`: "A gasto still requires an open turno
  regardless of the ledger write")*
- [ ] 3.7 [P] Integration — arqueo no-regression: a proveedor payment
  still appears in the turno's arqueo with NO new term.
- [ ] 3.8 [P] Integration — `pago × pago` rendezvous on the same
  proveedor: both commit, serialized, `proveedores.saldo` correct, no
  lost update. *(spec scenario: "Two concurrent payments to the same
  proveedor serialize")*
- [ ] 3.9 [P] Integration — `anulación × pago` rendezvous on the same
  compra: the payment holds `FOR SHARE` on the header so the anulación's
  `FOR UPDATE` waits and computes its reversal over a ledger that already
  contains the payment. *(spec scenario: "Anulación and a payment to the
  same proveedor race without deadlock")*
- [ ] 3.10 [P] Integration — fault point: a failure forced at the ledger
  write of `InsertarGastoAsync` leaves saldo, ledger, and the `gastos` row
  all untouched.
- [ ] 3.11 [P] **Mutation target #21** — `categoria = proveedor &&
  id_proveedor is not null` → drop either conjunct → the zero-movement
  tests (3.5) must fail.
- [ ] 3.12 [P] **Mutation target #22** — the ledger write placed AFTER
  `SaveChangesAsync` → move it before → `id_gasto` is `0` or an FK
  violation.
- [ ] 3.13 [P] **Mutation target #23** — `importe = −gasto.Importe` (the
  sign) → drop the negation → the invariant test (`saldo == Σ importe`)
  must fail.
- [ ] 3.14 [P] `db-error-backstops` — `fk_..._comprobante_compra` race:
  imputar a payment to a compra being annulled concurrently — already
  pre-checked under `FOR SHARE` by `ExigirCompraLigableAsync`
  (`:187-230`), the TOCTOU guard.
- [ ] 3.15 Gate guard: `dotnet ef migrations has-pending-model-changes`
  clean; zero new files under `Migraciones/`.
- [ ] 3.16 Run `judgment-day`; fix confirmed issues; re-judge until clean.
- [ ] 3.17 Branch `feat/stage15-slice3-pago-por-gasto` off `main` (parent:
  slice 2); PR; merge stacked-to-main.

**Test plan**: predicate scenarios (3.3-3.6), arqueo no-regression (3.7),
two real races (3.8-3.9), fault point (3.10), 3 mutation targets
(3.11-3.13), the `fk_..._comprobante_compra` race backstop (3.14).

**Verify**: `dotnet test --filter FullyQualifiedName~ServicioDeGastos`

---

## Slice 4: Estado de cuenta (PR 4)

**Start**: slice 3 merged (stacked-to-main order; functionally needs only
1+2). **Finish**: `GET /api/proveedores/{id}/cuenta-corriente` returns a
PAGINATED, running-balance movement list; `ServicioDeSaldoDeProveedor` is
re-sourced from the ledger using the OD7 formula (decision 4 above), its
three response records and `ResolverEstadoPago` byte-identical.
**Rollback**: revert the branch — the read surface disappears, the ledger
stays intact. **Done** = tests green + `judgment-day` clean round + PR
merged.

**Budget note**: pre-authorized split `4a`/`4b` if this slice overflows —
see decision 3 above.

- [ ] 4.1 Create
  `src/Ways.Domain/CuentaCorriente/CalculadorDeEstadoDeCuentaDeProveedor.cs`
  — `EtiquetarAjuste(idComprobanteCompra)`: non-null ⇒ Contramovimiento,
  null ⇒ Manual, pure. `ResolverEstadoPago(pagado, total)` is REUSED
  unchanged from `ServicioDeSaldoDeProveedor.cs:69-77`. *(design.md:
  131-134)*
- [ ] 4.2 Create `src/Ways.Application/CuentaCorriente/ContratosDeProveedor.cs`
  — `MovimientoDeCuentaDeProveedor`, `EstadoDeCuentaDeProveedorHeader`,
  `PaginaDeEstadoDeCuentaDeProveedor(Header, Items, Total, Pagina,
  Tamanio, Historico, Desde, Hasta)` — **PAGINATED** shape, reconciling
  spec.md's unpaginated prose per decision 5 above. *(design.md:139-149)*
- [ ] 4.3 Create
  `src/Ways.Application/CuentaCorriente/ServicioDeCuentaCorrienteDeProveedor.cs`
  — `ObtenerEstadoDeCuentaAsync`: `CountAsync` +
  `Skip((pagina-1)*tamanio).Take(tamanio)`, `pagina = Max(pagina, 1)`,
  `tamanio = Clamp(tamanio, 1, 200)`; `ORDER BY fecha DESC,
  id_movimiento DESC` (the tiebreaker — decision 5 above); `historico`
  overrides `desde`/`hasta`; no filter ⇒ last-month default. *(design.md:
  63, 152-154, 174-177)*
- [ ] 4.4 Same file: `ConstruirQuery` private helper with the 4 named
  clauses under `mutation-proof-tests`: `Where(m => m.IdProveedor ==
  idProveedor)`, `ThenByDescending(IdMovimiento)`, the
  `historico`-vs-default-range branch, each `if (desde/hasta is { } x)`.
  *(design.md:164-172)*
- [ ] 4.5 Modify `ServicioDeSaldoDeProveedor.cs` — `Saldo` sourced from
  `proveedores.saldo`; the per-compra `pagadoPorCompra` re-sourced
  applying the **BINDING OD7 formula** (decision 4 above): `pagado(X) =
  SUM(gastos.importe) WHERE gastos.id_comprobante_compra = X AND
  deleted_at IS NULL` (the retired mechanism's own predicate — no
  `categoria` filter here, distinct from the total-saldo predicate) `+
  SUM(−importe) WHERE movimientos_cuenta_corriente_proveedor.
  id_comprobante_compra = X AND tipo = 'ajuste'` (imputed
  contramovimientos and manual ajustes; `tipo = 'pago'` movements are
  EXCLUDED — they would double-count the `gastos` sum), fed to the
  UNCHANGED `ResolverEstadoPago(pagado, total)`. Response DTOs and
  `ResolverEstadoPago` stay byte-identical. **NOT** design.md's rejected
  `−Σ importe WHERE tipo <> 'compra'` shape. *(design decision 8/9,
  OVERRIDDEN by state.yaml OD7 per decision 4 above)*
- [ ] 4.6 Confirm `GET /api/proveedores/{id}/cuenta-corriente` under the
  `OperacionDePos` group; `GET /api/proveedores/{id}/saldo` stays
  top-level, unchanged route/policy/DTOs. *(design.md:255-257)*
- [ ] 4.7 [P] Integration — filters with asymmetric seeds (distinct
  dates, tipos, importes, imputaciones); order asserted as a sequence.
- [ ] 4.8 [P] Integration — pagination with `fecha` TIED on every row
  (`RelojFijo`) ⇒ page 2 repeats and skips nothing — proves the OD9
  pagination reconciliation (decision 5 above).
- [ ] 4.9 [P] Integration — `historico` overrides `desde`/`hasta`; no
  filter ⇒ last-month default; empty ledger ⇒ empty page with the header
  still populated.
- [ ] 4.10 [P] Integration — `/saldo` byte-compatibility: the response is
  byte-identical over the same data (all 3 records, all fields, per row —
  mutation-proof-tests rule 6).
- [ ] 4.11 [P] Integration — a fully-imputed compra ⇒ `pagada`; a
  partially-imputed one ⇒ `parcial`; an unimputed payment reduces the
  total saldo without settling any compra. *(spec + MODIFIED
  `saldo-de-proveedor` scenarios)*
- [ ] 4.12 [P] Integration — a PRE-CUTOVER confirmed compra with NO
  payments (its debt lives only in the `apertura` asiento) ⇒ `impaga` —
  the discriminating case both the proposal's and the design's original
  formulas got wrong (decision 4 above).
- [ ] 4.13 [P] Integration — a PRE-CUTOVER compra PARTIALLY paid via a
  linked gasto before the cutover ⇒ `parcial` with the correct remaining
  amount — the case OD7's arbitration names explicitly ("un pago parcial
  pre-cutover se pierde" under design's rejected formula, decision 4
  above). Also the DISCRIMINATOR for redefined mutation target #24.
- [ ] 4.14 [P] Integration — authorization: Vendedor `200` on estado de
  cuenta and `/saldo`; tenant B never sees tenant A's movements.
- [ ] 4.15 [P] **Mutation target #24 (REDEFINED per decision 4 above)** —
  the `tipo = 'ajuste'` filter on the ledger-imputed sum → widen to `tipo
  <> 'compra'` (re-including `pago`) → the double-count discriminator
  (4.13's shape: a partially-paid post-cutover compra whose `pagado`
  would double if `pago` movements were counted alongside the `gastos`
  sum) must fail.
- [ ] 4.16 [P] **Mutation target #25** — `ThenByDescending(IdMovimiento)`
  → delete it → the tied-`fecha` pagination test (4.8) must fail.
- [ ] 4.17 [P] **Mutation target #26** — each `if (desde/hasta/historico
  …)` in `ConstruirQuery` → delete one → that filter's asymmetric-seed
  test (4.7) must fail.
- [ ] 4.18 `dto-contract-honesty`: every field of
  `ContratosDeProveedor.cs` traced to its read/use point — no
  accepted-and-dropped field.
- [ ] 4.19 Gate guard: `dotnet ef migrations has-pending-model-changes`
  clean; zero new files under `Migraciones/`.
- [ ] 4.20 Run `judgment-day`; fix confirmed issues; re-judge until clean.
- [ ] 4.21 Branch `feat/stage15-slice4-estado-de-cuenta` off `main`
  (parent: slice 3); PR; merge stacked-to-main.

**Test plan**: filters (4.7), tied-`fecha` pagination (4.8), defaults
(4.9), `/saldo` byte-compatibility (4.10), payment-status coverage
including both pre-cutover cases (4.11-4.13), authorization (4.14), 3
mutation targets (4.15-4.17).

**Verify**: `dotnet test --filter
FullyQualifiedName~ServicioDeCuentaCorrienteDeProveedor|FullyQualifiedName~ServicioDeSaldoDeProveedor`

---

## Slice 5: Ajuste manual (PR 5)

**Start**: slice 4 merged. **Finish**: `SupervisionDeCuentaDeProveedor`
gates `POST /api/proveedores/{id}/cuenta-corriente/ajustes` (top-level);
Vendedor is rejected, Supervisor/Admin succeed. **Rollback**: revert the
branch — the route and policy disappear, nothing else changes. **Done** =
tests green + `judgment-day` clean round + PR merged.

- [ ] 5.1 Modify `src/Ways.Api/Seguridad/Politicas.cs` —
  `SupervisionDeCuentaDeProveedor` (`supervision_cuenta_proveedor`),
  Supervisor + Admin, same claim shape as `SupervisionDeCuentaCorriente`
  (`:117-122`), own name (the `LecturaDeAuditoria` precedent).
  *(design decision 8/12, `design.md:260-266`)*
- [ ] 5.2 Same file/registration: the `SuperficieDeAutorizacionTests`
  allowlist gains the one new non-GET route. *(design.md:268)*
- [ ] 5.3 Modify `ContratosDeProveedor.cs` —
  `SolicitudDeAjusteDeProveedor(IdPuntoVenta, Importe, Detalle)` — NO
  `tipo`, NO `saldoResultante` field (design decision 15). *(design.md:
  159-160)*
- [ ] 5.4 Modify `ServicioDeCuentaCorrienteDeProveedor.cs` —
  `RegistrarAjusteAsync`: outside the transaction —
  `ReglaDeAjusteDeCuenta.Validar(importe, detalle)` reused unchanged
  (`importe ≠ 0`, `length(btrim(detalle)) >= 5`) → `ResolverProveedorAsync`
  404 → `ResolverPuntoVentaAsync` 404 (**before** the transaction —
  `ServicioDeGastos.cs:28-31`'s ordering rule) → NO turno required →
  `EstrategiaSinReintento` BEGIN → `ActualizarSaldoProveedorAsync(importe)`
  (the only lock) → `InsertarMovimientoCcProveedorAsync(ajuste,
  id_comprobante_compra = NULL, id_gasto = NULL, detalle)` → COMMIT.
  *(design decisions 13-14, `design.md:155-156, 217-222`)*
- [ ] 5.5 Create
  `src/Ways.Api/Endpoints/CuentaCorrienteDeProveedorEndpoints.cs` — `POST
  /api/proveedores/{idProveedor:int}/cuenta-corriente/ajustes` mapped
  TOP-LEVEL under `SupervisionDeCuentaDeProveedor` alone — NOT stacked
  inside the `OperacionDePos` group (design decision 12: proposal decision
  8's rejection of the AND-composition; the `/saldo` top-level precedent).
  *(design.md:64, 256, 335)*
- [ ] 5.6 Confirm `apertura` is unreachable from this endpoint at three
  layers: no `tipo` field on the DTO; `ValidarFormaPorTipo` throws if ever
  called with `apertura` from a non-migration caller; the CHECK backs
  both. *(design decision 15)*
- [ ] 5.7 [P] Integration — 403/200 matrix: Vendedor `403`, Supervisor
  `200`, Admin `200` on `POST /ajustes`; Root `403`. *(spec
  `cuenta-corriente-de-proveedores` + `operacion-de-pos` scenarios)*
- [ ] 5.8 [P] Integration — detalle/importe rejections: empty `detalle`
  rejected before any write; `importe = 0` rejected
  (`ajuste_importe_invalido`/`ajuste_detalle_requerido`). *(spec scenario:
  "A manual ajuste with no detalle is rejected")*
- [ ] 5.9 [P] Integration — PV `404` raised BEFORE the turno-less
  transaction begins.
- [ ] 5.10 [P] Integration — Supervisor posts an ajuste of `importe =
  -200` → succeeds, proveedor saldo decreases by `200`. *(spec scenario:
  "Supervisor posts a manual ajuste")*
- [ ] 5.11 [P] `db-error-backstops` — `fk_..._proveedor` (route value):
  `ResolverProveedorAsync` 404 pre-check + generic `23503` mapping,
  integration asserting the TRANSLATED domain code; `fk_..._punto_venta`
  (PV provenance): `ResolverPuntoVentaAsync` 404 pre-check + generic
  mapping. *(proposal §E, design Backstop Map)*
- [ ] 5.12 [P] **Mutation target #27** —
  `.RequireAuthorization(Politicas.SupervisionDeCuentaDeProveedor)` →
  delete the line → Vendedor ⇒ `403` (5.7) must fail.
- [ ] 5.13 Gate guard: `dotnet ef migrations has-pending-model-changes`
  clean; zero new files under `Migraciones/`.
- [ ] 5.14 Run `judgment-day`; fix confirmed issues; re-judge until
  clean.
- [ ] 5.15 Branch `feat/stage15-slice5-ajuste-manual` off `main` (parent:
  slice 4); PR; merge stacked-to-main.

**Test plan**: 403/200 matrix (5.7), rejections (5.8), PV ordering (5.9),
coverage (5.10), 2 backstop tests (5.11), 1 mutation target (5.12).

**Verify**: `dotnet test --filter
FullyQualifiedName~SupervisionDeCuentaDeProveedor|FullyQualifiedName~RegistrarAjuste`

---

## Slice 6: Web (PR 6)

**Start**: slice 5 merged (needs slices 4 and 5). **Finish**:
`/proveedores/:id/cuenta-corriente` renders the movement list with running
balance, filters and the ajuste modal; `ResumenSaldoDeProveedor.tsx` shows
the saldo-a-favor state and links to the new screen. **Rollback**: revert
the branch — no schema/API change, purely additive UI. **Done** = tests
green + `judgment-day` clean round + PR merged.

**Pre-approved degradation**: if this slice overflows, ship the list, the
running balance and the filters, and DROP the ajuste modal (the endpoint
still serves the operation) — a documented reduction, never silent
(decision 3 above).

- [ ] 6.1 Create `src/Ways.Web/src/api/cuentaCorrienteDeProveedor.ts` —
  client + pure mappers (movement mapper, filter builder, `etiquetarAjuste`).
- [ ] 6.2 Create `src/Ways.Web/src/paginas/CuentaCorrienteDeProveedor.tsx`
  — route `/proveedores/:id/cuenta-corriente`, built from
  `CuentaCorriente.tsx`'s ledger half + `HistoricoDeCajas.tsx`'s pager.
  `key={idProveedor}` on the subtree (react-async-state rule 8);
  `generacionRef` on every fetch (rule 2), bumped BEFORE the write (rule
  3); post-write refetch has its own `try/catch` and its own copy (rule
  6); first-line re-entrancy guard + full-window disable on the ajuste
  (rule 9). *(design.md:286-299)*
- [ ] 6.3 Filters `desde`/`hasta`/"ver histórico" built with
  `fechaIsoConOffset` (the browser's own offset, never `Z`) — the same
  helper `cuentaCorriente.ts` already duplicates.
- [ ] 6.4 Columns: `Fecha · Tipo · Comprobante/Gasto · Detalle · Importe ·
  Saldo resultante`. A negative saldo renders "saldo a favor", NEVER
  clamped to zero.
- [ ] 6.5 Modify `ResumenSaldoDeProveedor.tsx` — retire the "compras
  confirmadas menos gastos ligados" caption and the "aproximación, no
  invariante" callout (both retired by this stage); add the saldo-a-favor
  state + the link to the new screen; stays presentational (`saldo:
  number` prop). *(design.md:304-307)*
- [ ] 6.6 Modify `App.tsx`, `Proveedores.tsx`, `Compras.tsx` — one route +
  two entry points (per-row action in `Proveedores.tsx`, filtered header
  in `Compras.tsx`).
- [ ] 6.7 React-async-state rule 10: grep any error-recovery/data-honesty
  pattern introduced here across `src/paginas` and replicate in every
  sibling surface, in this same PR.
- [ ] 6.8 [P] Colocated unit tests: `etiquetarAjuste` (both directions),
  the movement mapper, the filter builder — no DOM.
- [ ] 6.9 [P] Component test: a stale response is discarded — resolved
  INSIDE `act`, asserted synchronously after the flush (rule 7).
- [ ] 6.10 [P] Component test: the pager is disabled at the edges.
- [ ] 6.11 [P] Component test: a negative saldo renders "saldo a favor".
- [ ] 6.12 [P] Component test: a double click on "Registrar ajuste"
  issues exactly ONE POST (rule 9).
- [ ] 6.13 [P] **Mutation target #28** — the saldo-a-favor branch in
  `ResumenSaldoDeProveedor.tsx` → delete it → the colocated descriptor
  test on a negative saldo (6.11) must fail.
- [ ] 6.14 Run `judgment-day`; fix confirmed issues; re-judge until
  clean.
- [ ] 6.15 Branch `feat/stage15-slice6-web` off `main` (parent: slice 5);
  PR; merge stacked-to-main.

**Test plan**: mutation target (6.13), descriptor tests (6.8), stale
inside `act` (6.9), pager edges (6.10), saldo a favor (6.11), single POST
on double click (6.12).

**Verify**: `npm run test -- CuentaCorrienteDeProveedor`

---

## Global Cross-Slice Tasks

- **`dto-contract-honesty` compliance**: enforced at slices 4 (4.18) and
  5 (5.3, decision 15 — no accepted-and-dropped field, no client-computed
  saldo/delta accepted anywhere).
- **`mutation-proof-tests` compliance**: all 28 named mutation targets are
  placed exactly once (decision 8 above), each with apply-time evidence
  required in its slice's PR body; target #24 is redefined per decision 4;
  the checkout non-regression (design's unnumbered "—" row) is a binding
  verify criterion, task 2.27, not counted among the 28.
- **`db-error-backstops`**: applies at slices 1 (tasks 1.24, exemptions +
  CHECK), 3 (task 3.14, `fk_..._comprobante_compra` race), and 5 (task
  5.11, `fk_..._proveedor`/`fk_..._punto_venta`). No new `23505` family;
  `ManejadorDeErrores.cs` stays unmodified across the whole stage.
- **`react-async-state`/`web-descriptor-tests` compliance**: slice 6 is
  the only web-touching slice; every new pure helper ships a colocated
  descriptor test in that same slice.
- **Checkout-budget protection** (design.md:45-47): no task in any slice
  touches `ServicioDeVentas.EjecutarTransaccionAsync`;
  `VentasCheckoutTests.cs` is absent from the stage's diff (task 2.27) —
  binding, confirmed by `sdd-verify`.
- **`ManejadorDeErrores.cs` untouched** (gate §B/§E): no task in any
  slice modifies it — the generic `fk_`/`23503` mapping already covers
  this stage.
- **`EscriturasDeCuentaCorriente.cs`'s legacy `AgregarParametro`** stays
  unmigrated — out of this stage's scope (design.md:511-515), no task
  touches it.
- **Formula/pagination reconciliation** (decisions 4-5 above): recorded
  here so `sdd-verify` reads the binding OD7 formula and the paginated
  estado de cuenta as the RATIFIED shape, not a deviation from spec.md's
  literal text.

---

## Dependency Summary

```
Slice 1 (ledger-schema)
  └─ Slice 2 (escrituras-y-deuda)
       └─ Slice 3 (pago-por-gasto)
            └─ Slice 4 (estado-de-cuenta)   ← functionally needs only 1+2
                 └─ Slice 5 (ajuste-manual)
                      └─ Slice 6 (web)      ← needs 4 (read model) + 5 (ajuste)
```

Merge order (ratified, `chain_strategy: stacked-to-main`): `1 → 2 → 3 → 4
→ 5 → 6`, strictly linear. The underlying dependency graph is looser (4
and 5 need only 1+2, not 3) but the stacked-to-main strategy commits to
sequential integration regardless — no slice is skipped or reordered.

---

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | ~2 250 total (6 slices: 450/410/330/420/260/380) |
| 400-line budget risk | Medium — slices 1, 2 and 4 sit at or above the cap; pre-authorized cuts named for `1a`/`1b`, `2a`/`2b`, `4a`/`4b` (decision 3 above) |
| Chained PRs recommended | Yes |
| Suggested split | 6 PRs, stacked-to-main, per the Suggested Work Units table above |
| `size:exception` anticipated | No — the migration is one table + two ALTERs and its pre-authorized `1a`/`1b` split keeps it under budget without an exception |
| Delivery strategy | `auto-chain` (already resolved, `state.yaml`) |
| Chain strategy | `stacked-to-main` |
| Decision needed before apply | No — already resolved |

Per-slice budget risk: 1 **Medium (~450)** · 2 **Medium (~410)** · 3 Low
(~330) · 4 **Medium (~420)** · 5 Low (~260) · 6 Low (~380). As in prior
stages, overflow is expected to come from **test depth**, not scope creep:
slice 1 carries a 7-scenario fidelity fixture plus 11 mutation targets;
slice 2 carries three race/fault-point families plus 9 mutation targets;
slice 4 carries two pre-cutover discriminator tests (4.12, 4.13) that
exist specifically because OD7 rejected both prior formulas. The backfill
fidelity proof, the single-write-authority containment, the anulación
contramovimiento, and the pre-cutover `impaga`/`parcial` cases are NEVER
degraded (decision 3 above) — a coverage slice splits, it is never
trimmed.

Decision needed before apply: No
Chained PRs recommended: Yes
Chain strategy: stacked-to-main
400-line budget risk: Medium
