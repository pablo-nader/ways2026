# Archive Report: Stage 14 — Auditoría y trazabilidad de operaciones sensibles

**Archived**: 2026-08-17
**Status**: PASS WITH WARNINGS at verify (W1 remediated pre-archive, W2 informational/
already-fixed-on-main) — archived as **clean with one remediated warning**.

## Executive Summary

Stage 14 closes the actor-attribution gap the explore phase found concentrated in
precios (zero actor) and role changes (zero actor, zero before-image): a single
append-only `auditoria` table, written inside the SAME transaction as the business
operation it records, fail-closed end to end. Twelve actions across six services
(`precio.cambio`; `venta.anulacion`; `compra.anulacion`; `stock.ajuste`,
`stock.decomiso`, `stock.conteo`; `cc.reliquidacion`; `usuario.alta`,
`usuario.actualizacion`, `usuario.baja`, `usuario.desbloqueo`, `usuario.password`)
each write exactly one row per operation — `stock.transferencia` structurally
excluded (a transfer has two puntos de venta, a single `id_punto_venta` column
cannot express that without lying). The payload is a bounded, per-action field set
built by pure factories that never accept an entity, with a key-subset invariant and
a recursive secret denylist — a full-row dump (and a `hash_password` leak) is
structurally unrepresentable, not merely policy. A new `LecturaDeAuditoria` policy
(Admin-only, not stacked over `LecturaDeReportes`) gates a filtered/paginated
`GET /api/auditoria` and an `/export` sibling verbatim over the etapa-11 contract,
and a new `Auditoria.tsx` screen (filters, pager, change-detail panel, download)
surfaces it. Zero migrations beyond the one gate-contracted `auditoria` table; the
checkout emission path was never opened, and `VentasCheckoutTests`'s `16`-query
guard stayed byte-identical across the whole stage and the three post-stage chip
PRs.

## Artifacts Read (traceability)

Openspec mode — filesystem retrieval only (`openspec/changes/stage-14-auditoria-
trazabilidad/*`); Engram MCP tools were used for this report's own persistence
step but the source artifacts below were read directly off disk, matching the
convention already established by the stage-13 archive.

- `openspec/changes/stage-14-auditoria-trazabilidad/explore.md`
- `openspec/changes/stage-14-auditoria-trazabilidad/proposal.md` (8 autonomous
  decisions, DB gate §A/§B, capability contract, tentative 7-slice plan)
- `openspec/changes/stage-14-auditoria-trazabilidad/specs/{auditoria-de-
  operaciones,precios,comprobantes-venta,comprobantes-compra}/spec.md` (1 new
  capability + 3 delta specs)
- `openspec/changes/stage-14-auditoria-trazabilidad/design.md` (17 architecture
  decisions, 11 call sites, 28 mutation targets)
- `openspec/changes/stage-14-auditoria-trazabilidad/tasks.md` (15 orchestrator
  decisions, 7 slices, 175/175 tasks checked — including the two spec/design
  conflict resolutions and the DB gate amendment)
- `openspec/changes/stage-14-auditoria-trazabilidad/verify-report.md` (PASS WITH
  WARNINGS, 2026-08-17, HEAD `8c262ba`)
- `openspec/changes/stage-14-auditoria-trazabilidad/state.yaml` (per-phase notes,
  DB gate approval + amendment 1 text)
- Repository `git log`/`gh pr view` (PR merge commits #123-#132, dates, bodies,
  judgment-day ledger comments) used to corroborate and extend the delivery
  record for the three post-slice chip PRs (#129, #130, #131), whose full
  judgment-day findings live only in PR comments, not in `tasks.md`

## Spec Merge Summary

All four merges verified byte-identical against their delta source blocks via
`diff` (see the accompanying phase result for verbatim mechanical-copy /
block-diff evidence — every diff below returned empty).

| Domain | Action | Requirements moved | Scenarios moved |
|---|---|---|---|
| `auditoria-de-operaciones` | Created (new capability, mechanical `cp`) | 7 | 25 |
| `precios` | Updated (ADDED requirement appended) | 1 | 2 |
| `comprobantes-venta` | Updated (ADDED requirement appended) | 1 | 2 |
| `comprobantes-compra` | Updated (ADDED requirement appended) | 1 | 2 |

Total landed in `openspec/specs/`: 10 requirements / 31 scenarios, matching
`verify-report.md`'s own measured count exactly ("`auditoria-de-operaciones` 7
requirements / 25 scenarios; `precios`, `comprobantes-venta`, `comprobantes-compra`
1 req / 2 scen each → 10 requirements / 31 scenarios, all traced to passing
tests"). `git diff` against each of the three pre-existing spec files shows a
pure trailing addition — zero lines of pre-existing content touched. All other
main specs (`stock`, `conteo-de-inventario`, `lotes-y-vencimientos`,
`reliquidacion-a-precio-del-dia`, `usuarios-*`, `exportacion-de-reportes`) were
left untouched, per the proposal's explicit capability contract.

## Delivery Record — 7 Slices, PRs #123-#132, 2026-08-16..17

| Slice | Content | Branch | PR | Merged | Judgment-day |
|---|---|---|---|---|---|
| 1 | Migration `AuditoriaEtapa14` (table + standard RLS), Domain contracts (`AccionAuditada`/`RegistroDeAuditoria`/`PayloadDeAuditoria`), `SerializadorDeAuditoria`, `ServicioDeAuditoria` writer (both EF and ADO modes, no call site yet), doc-10 | `feat/stage14-slice1-tabla-auditoria` | #123 | 2026-08-16 | CLEAN. Round 1 Judge B: 0 severos, 3 WARNING fixed (recursive denylist over nested dicts, tenant-of-subject-vs-session discriminated via platform mode, catalog doc honesty) + 2 no-fix dispositions (Npgsql-driver equivalent mutant; gate-§B-contracted CHECKs). Re-round B: R2-B-1 residual closed (nested `IDictionary` case). Judge A fresh: 0 findings |
| 2 | `precio.cambio` + the five `usuario.*` call sites (EF transactions) | `feat/stage14-slice2-precios-usuarios` | #126 | 2026-08-16 | CLEAN. Round 1 Judge B: 1 MAJOR (cross-tenant username scoping was unfalsifiable after an InMemory test removal — closed with a platform-actor cross-tenant test) + 4 WARNING closed + 1 suggestion applied. Judge A round 2: 1 inferential WARNING (double-INSERT-on-retry risk from building the entity inside the `ExecutionStrategy` lambda) — resolved by **reverting** the round-1 suggestion, not by adding a guard |
| 3 | `venta.anulacion` + `compra.anulacion` (ADO transactions) + checkout non-regression | `feat/stage14-slice3-anulaciones` | #124 | 2026-08-16 | CLEAN. Round 1 Judge B: 0 severos, 2 WARNING closed (fail-closed test on the spec's literal 3-lines+CC composition; actor asserted by equality, not just non-zero). Judge A fresh: 0 findings |
| 4 | `stock.ajuste`/`decomiso`/`conteo` (one row per counting **operation**, Orchestrator Decision #1) + `cc.reliquidacion` | `feat/stage14-slice4-stock-cc` | #125 | 2026-08-16 | CLEAN. Round 1 Judge B: 2 MAJOR closed (aggregate-conteo-with-difference had zero coverage; `id_entidad` accidentally matched `id_movimiento_stock` from sequence alignment, masking a real discriminator) + 1 equivalent-mutant survivor registered without a fix. Judge A: 1 WARNING closed (decomiso payload asserted only 2 of 4 keys) |
| 5 | `LecturaDeAuditoria` policy + `ConstruirQuery`/`ConsultarAsync` + `GET /api/auditoria` | `feat/stage14-slice5-consulta` | #127 | 2026-08-16 | CLEAN. Round 1 Judge B: 0 severos, 3 WARNING closed (`hasta` boundary pinned with a row AT the instant; pagina/tamanio clamps; payload content asserted on both sides incl. null-ness) + 1 suggestion applied (`CrearContextoDeOwner` hoisted to the shared fixture). Judge A fresh: 0 findings |
| 6 | `GET /api/auditoria/export` + `ExportacionDeAuditoria` mapper | `feat/stage14-slice6-export` | #128 | 2026-08-16 | CLEAN. Round 1 Judge B: 0 severos, 3 WARNING closed (tope+2 seed discriminating the real count on the FIRST `Exigir`; `formato=pdf` tested per route; success-side exact-cap test). Judge A fresh: 0 severos, 1 pre-existing repo-wide WARNING registered without a fix (`DateOnly.FromDateTime(hasta.UtcDateTime)` runs the displayed "Período" one day for negative UTC offsets — same class as the etapa-11 filename bug, out of scope for this slice, became the seed for PR #130) |
| 7 | `Auditoria.tsx` (filters, pager, `PanelDeCambio`, download, nav) | `feat/stage14-slice7-web` | #132 | 2026-08-16 | CLEAN. Round 1 Judge B: **3 MAJOR** — see Judgment-Day Summary below (the production date-desync bug is the headline) — + 4 WARNING + 2 suggestions, all closed in one fix commit (`a249dab`) around a single shared scope-query builder. Round 2 Judge A: 0 severos, 2 WARNING closed (an inert `FiltrosDeAuditoria` type mirror with an over-declaring doc-comment, removed; the `etiquetaDeAccion` fallback branch had no test) + 2 suggestions closed |

**Post-slice chip PRs — 3 mergeados después de los 7 slices (registro obligatorio)**:

- **#130** (`test/barrido-gaps-export`, 2026-08-16): swept the 3 export-guard test
  gaps judgment-day found in slice 6 (tope-off-by-one seed, single-test-for-18-
  routes `formato` parsing, missing exact-cap success test) across **all 18
  `/export` routes** (11 files) — plus the 8 `ExportacionDeReportesTests` routes
  and the turno detail export, which had **no tope test at all**. Tests 48 → 108,
  all green, zero production changes. Judgment-day: both judges independently
  converged on the same single WARNING (`Row(n).IsEmpty()` as an end-of-data
  marker) — clean round.
- **#131** (`fix/export-generado-el-zona`, 2026-08-16): the third occurrence of the
  same defect class (etapa-11's filename bug, slice 6's "Período" bug, now the
  "Generado el" header line) — `ExportadorXlsx` printed the generation instant in
  UTC next to a label naming the punto de venta's resolved zone. Fixed with a
  single chokepoint (`InstanteDeGeneracion.En`), applied in
  `ContextoDeExportacionHttp.Construir` so all ~20 `/export` routes pass through
  it; a `TryFindSystemTimeZoneById` fallback protects the one report whose zone
  is the `"N/A"` sentinel from turning into a 500.
- **#129** (`fix/exportacion-offset-fechas`, 2026-08-16): the real production
  defect underlying slice 6's registered WARNING. Npgsql rejects writing a
  `DateTimeOffset` with a non-zero offset against `timestamptz` **as a query
  parameter too**, not only as a stored value — so any date filter sent from the
  web with the browser's real offset (`...T23:59:59.999-03:00`) 500'd across
  **~13 endpoints** (7 `DateTimeOffset`-filtered columns, JSON listings and
  exports alike). Fixed in two layers: a global EF `ConfigureConventions`
  normalization to UTC (a re-expression, not a zone conversion — the instant
  never moves) plus `FechaDelRango`, a new helper that derives the *displayed*
  date from the client's own offset instead of `.UtcDateTime`, applied at the six
  export sites. Judgment-day found a **CRITICAL** (below) that widened the fix to
  14 files' worth of a raw-ADO parameter helper. Added `mutation-proof-tests`
  rule 10.

**Final suites, post-final-merge** (per `verify-report.md`'s own method note and
the orchestrator's parallel full-suite run, cited here per the Final-State
Authority hierarchy — `verify-report.md` ran only filtered spot-checks because
Docker was occupied by the parallel full run): **Domain 490 · Application 270 ·
Integration ~1200 · vitest 693/693**. Gate `UNA-MIGRACION-APROBADA` held for the
whole stage — slice 1 shipped exactly one migration (`AuditoriaEtapa14`), and
every other slice's gate-guard task confirmed `dotnet ef migrations
has-pending-model-changes` clean with zero new files under `Migraciones/`.

## Decisions Log

### Proposal — 8 autonomous decisions (`proposal.md`, delegated technical authority)

1. **One `auditoria` table**, `id_tenant` NOT NULL of the audited **subject** (not
   the actor) + `id_punto_venta` NULL for tenant-wide events — one table, not two.
2. **Bounded, per-action payload, never a row dump** — the decisive argument is
   security (a `usuarios` dump would put `hash_password` into an append-only
   Admin-readable table); key-subset rule (`valor_anterior ⊆ valor_nuevo`),
   testable.
3. **No retention or partitioning**, with 3 measurable tripwires and the deferral
   cost made explicit (Postgres cannot convert a populated table to partitioned
   in place).
4. **Same transaction, fail-closed**: the operation that cannot be audited does
   not happen; checkout is OUT OF SCOPE by decision (emitting is not sensitive;
   the doc names anulaciones) — corrected a factual explore claim: the
   interceptor only counts `ReaderExecuting*`, so `ExecuteScalarAsync` is ALSO
   invisible to the 16-query guard.
5. **First pass = 12 actions across 6 services** (the doc's 5 + the user
   lifecycle); `stock.transferencia` EXCLUDED for a structural reason (origin and
   destination don't fit one `id_punto_venta`).
6. **New Admin-only `LecturaDeAuditoria` policy, not stacked** (precedent
   `LecturaDeRentabilidad`): a Supervisor doesn't read the log that records
   Supervisores.
7. **jsonb with snake_case keys** via the writer's own `JsonSerializerOptions`
   (NOT global); `cuenta_corriente.detalle text` is NOT migrated.
8. **`accion` is `text` + a non-empty CHECK with the catalog in the application,
   NOT a native enum** — `ALTER TYPE ADD VALUE` is irreversible (proven in stage
   12) and the catalog grows every future stage.

Gate verdict proposed: ONE migration (`AuditoriaEtapa14`), ONE new table, zero
ALTERs on existing tables/enums, zero backfill. `db-error-backstops` resolved:
no new `23505` (no unique index); the 3 FKs covered by the existing generic
`fk_*`/`23503` → `referencia_invalida` mapping (`ManejadorDeErrores.cs:224`),
exemption documented (`id_actor` always server-derived, `usuarios` is soft-delete).

### Design — 17 architecture decisions (`design.md`), zero new DDL

1. **Two modes, one contract**: `Registrar(...)` (sync, EF, enqueues into the
   caller's own `SaveChangesAsync`) and `RegistrarAsync(conexion, transaccion,
   ...)` (one raw `INSERT`, no `RETURNING`, on the caller's own ADO connection
   and transaction) — the writer never opens a transaction, calls
   `SaveChanges`, or commits. Decisive reason: 4 of 5 `ServicioDeUsuarios` paths
   have no explicit transaction (only `SaveChangesAsync`), so a raw INSERT there
   would autocommit and break fail-closed in both directions.
2. `id_actor`/`creado_el` are NEVER parameters — the writer stamps them from
   context/clock, making the gate §B exemption structural.
3. Invariants (subset, denylist, snake_case) live in `RegistroDeAuditoria`'s
   Domain constructor — an illegal record is not constructible.
4. `AccionAuditada` = `sealed record (Accion, Entidad)` with 12 constants +
   `Todas` — the catalog fixes the PAIR, not the verb.
5. Payload = a dictionary built by 12 pure factories; NONE accepts an entity —
   a row dump is unrepresentable by type, the denylist is a backstop.
6. One shared `JsonSerializerOptions` with `DictionaryKeyPolicy` (NOT
   `PropertyNamingPolicy`, a no-op over a dictionary that looks like a decision)
   + `JsonStringEnumConverter` snake_case.
7. `Auditoria` does NOT inherit `EntidadTenant` (would drag in `EntidadBase`,
   forbidden by the gate) — a filter cloned from `MovimientoStock`, `id_tenant`
   written explicitly (`EstamparTenant()` would silently overwrite it with the
   session's tenant, inverting decision 1).
8. `MarcarAnuladoAsync`/venta returns `RETURNING id_punto_venta`/`int?` (exact
   precedent: `ServicioDeTurnos.MarcarCerradoAsync`) — same atomic UPDATE stays
   the sole race-safe authority, now also answers "in which PV", zero extra
   round trips; compras already returned it.
9. Before-images come from the authoritative `RETURNING` or the already-taken
   `FOR UPDATE` lock, NEVER a second SELECT another transaction could answer
   differently.
10. Fail-closed proven BY DATA (a non-existent `contexto.UsuarioId` ⇒ real
    `23503` on `fk_auditoria_actor`), no `IEscritorDeAuditoria` seam — the same
    test covers the gate §B `23503` → `400 referencia_invalida` mapping.
11. `usuario.alta` is the only call site that restructures its caller's
    transaction (explicit tx, two `SaveChangesAsync` calls — the id doesn't
    exist before the first flush).
12. OFFSET pagination (`PaginaDe<T>`, 7 repo precedents, zero keyset) with a
    MANDATORY `id_auditoria DESC` tiebreak — `creado_el` comes from one
    `reloj.Ahora` per operation, so ties are structural and `RelojFijo` ties the
    whole fixture.
13. One `ConstruirQuery` feeds both JSON and export — export is the LISTADO
    shape (count-first + cap + anti-race second `Exigir`), mapped from the SAME
    `FilaDeAuditoria` so parity is structural.
14. LEFT JOIN to `usuarios` with `IgnoreQueryFilters(["BajaLogica"])` — an inner
    join would erase a root actor's rows (invisible to a tenant session) and a
    soft-deleted actor's rows from the log.
15. `accion`/`entidad` are NOT validated against the catalog on read — a retired
    action must stay queryable.
16. `idEntidad` requires `entidad` ⇒ `400` (polymorphic id).
17. `Auditoria.tsx` = `HistoricoDeCajas.tsx` (filters + pager) +
    `BotonDeDescarga` from `Vencimientos.tsx`, with the change-detail panel as
    an isolated, cleanly droppable component.

11 call sites located file:line with payload and "what does NOT change" (lock
order, decide-then-commit, `UPDATE...RETURNING` as authority). 28 mutation
targets named. **Two conflicts flagged in Open Questions, both resolved by the
orchestrator at `sdd-tasks`** (see below).

### Orchestrator decisions recorded at tasks/apply (`tasks.md`, 15 total)

1. 7 slices/7 PRs stacked-to-main, design.md's ratified breakdown re-confirmed
   (not re-scoped). Merge order: `1` blocks everything → `{2,3,4}` parallel
   (disjoint service files) → `5` → `6` → `7`.
2. DB gate `UNA-MIGRACION-APROBADA` — slice 1 carries exactly one migration; a
   gate-guard task on every other slice.
3. No `size:exception` anticipated; 4 pre-authorized cut points named
   (`1a`/`1b`, `5a`/`5b`, drop `PanelDeCambio`/`compararPayloads` at slice 7).
   Coverage of the 12 actions and fail-closed are NEVER degraded — a coverage
   slice splits, it is never trimmed.
4. `judgment-day` runs once per slice, 7 independent rounds.
5. **CONFLICT FOUND AND RESOLVED #1 — `stock.conteo` row cardinality.**
   `design.md`'s call-site table read "one row per ledger movement written"
   (N rows for a conteo por lote touching N lotes); `spec.md:97-98` is
   unambiguous: "Each operation MUST write exactly one row", no per-action
   carve-out. **The spec's letter is authoritative** — the audit row is a
   seal-plus-pointer, never the arithmetic; the per-lote detail already lives
   actor-stamped in `movimientos_stock`. Resolution: accumulate
   `movimientos_generados`/`lotes_afectados`/`delta_total` across the existing
   loop, write ONE `RegistrarAsync` call after it, per counting **operation**.
   Slice 4, task 4.4, discriminating test 4.11.
6. **CONFLICT FOUND AND RESOLVED #2 — `usuario.baja` payload.** The proposal's
   payload table read `{estado:"eliminado"}` — not constructible
   (`EstadoUsuario` has no `Eliminado` member; `EliminarAsync` performs a soft
   delete via `deleted_at`). Design's own call-site table already used the
   correct shape (`{deleted_at, estado}`), flagged in its Open Questions as
   needing reconciliation. **The design's call site is authoritative.** Slice
   2, task 2.5.
7. `mutation-proof-tests`: 28 named targets placed exactly once (1: 8, 2: 4,
   3: 3, 4: 3, 5: 6, 6: 3, 7: 1 = 28); the checkout non-regression row is a
   binding verify criterion (task 3.12), not one of the 28.
8. `dto-contract-honesty` at slices 1, 5, 7 — every new/mirrored data contract.
9. `db-error-backstops` once, slice 1, task 1.28 — the `23503` fail-closed
   gate covered by the existing generic mapping, unmodified.
10. `react-async-state` + `web-descriptor-tests` at slice 7 only — the single
    web-touching slice this stage.
11. `work-unit-commits` at every slice.
12. Test dates fixed at `RelojFijo(2026-08-14T12:00:00Z)`, never wall-clock-
    relative — exact equalities, not range checks.
13. Checkout-budget protection is a **binding verify criterion**:
    `VentasCheckoutTests.cs` MUST be absent from the stage's diff entirely
    (task 3.12) — confirmed absent across all 7 slices AND the 3 post-slice
    chip PRs, `Assert.Equal(16, …)` intact throughout.
14. The doc-10 update (the `auditoria` table + "Estado (Etapa 14)" annotation)
    is a slice-1 task (1.11) — landed as a new standalone "## 10. Auditoría"
    section rather than literally inside §6 (doc-10's §6 was already closed
    around stock/lotes; a multi-domain capability didn't fit there).
15. `ServicioDeStock.InsertarMovimientoStockAsync`'s `Task` → `Task<int>`
    ripple registered so `sdd-verify` would not read it as scope drift.
    `TransferirAsync` ignores the value, byte-identical behavior, still writes
    zero `auditoria` rows.

**DB gate amendment 1** (`state.yaml`, approved 2026-08-16 under delegated
autonomous mandate, same pattern as stage 12's amendments): the migration
carries **2 additional FK-support indexes** (`ix_auditoria_id_actor`,
`ix_auditoria_punto_venta`) beyond the 3 gate-contracted ones — EF Core's
`ForeignKeyIndexConvention` auto-generates one per FK not covered by an
existing index's leading columns, and is not model-time-suppressible (a
synchronous `RemoveIndex` right after `HasForeignKey` does not stick — the
convention batch re-adds it, confirmed empirically). Rather than ship the
default PascalCase names (violating doc-10's snake_case convention), two
explicit names were used, matching the pattern every other FK in the schema
already carries. Total: 5 indexes on `auditoria` (3 contract + 2 FK-support),
not the 3 the gate's model-summary table names as a business-index count. No
ALTER, no data statement, no new table/column/constraint/enum — flagged for
`sdd-verify`/orchestrator ratification, not silently assumed.

## Judgment-Day Summary — ~35 Confirmed Findings Across 7 Slices + 2 Chip PRs

Every slice closed CLEAN before its PR merged, with mutation evidence (mutate →
named test fails → revert → green) recorded in `tasks.md` for every fix. The
CRITICAL of the stage-13 archive (the orphan-ghost-row defect) does **not**
apply here — that belonged to stage 13. This stage's own standout findings:

- **Slice 7's PRODUCTION bug (MAJOR, the headline finding of the whole stage)**:
  `construirQueryDeConsultaDeAuditoria` and `construirQueryDeExportacionDeAuditoria`
  diverged on how they guarded empty `desde`/`hasta` — the JSON query omitted
  them, the export query sent them regardless, and `fechaIsoConOffset` over an
  empty string produced `desde=...T00:00:00+NaN:NaN`, a malformed
  `DateTimeOffset` the server rejected with `400`. **Clearing a date field
  desynchronized the listing from the download** — the export URL always
  carried the stale/malformed offset. Fixed by unifying both builders into one
  shared `construirQueryDeAlcanceDeAuditoria` (`api/auditoria.ts`), the same
  criterion `construirQueryDeAlcanceDeCajas` already used; since
  `AuditoriaEndpoints.cs`'s `/export` route declares non-nullable
  `DateTimeOffset desde, hasta`, the download button is now disabled with a
  visible reason instead of emitting a URL the server would reject.
- **Slice 4's MAJOR — aggregate conteo write with zero test coverage.** The
  `EjecutarConteoAsync` (non-por-lote) path writing an `auditoria` row for a
  real difference had NO test at all until judgment-day found it; closed with
  key-by-key payload coverage.
- **PR #129's secret-adjacent denylist hardening carried forward from slice
  1**: the recursive denylist over nested dictionaries (closed at slice 1
  round 1) is the same defense class the stage kept probing — the ADO
  parameter-helper CRITICAL below is its raw-SQL-path sibling.
- **Slice 2's per-tenant username scoping, MAJOR**: became unfalsifiable after
  an `EF InMemory` round-trip test was removed (InMemory doesn't support
  `BeginTransactionAsync`) — the per-tenant uniqueness check in
  `ExigirDisponibilidadAsync` could be mutated to a global check and still pass
  all 203 existing tests. Closed with a real cross-tenant, cross-Postgres round
  trip, plus a platform-actor variant the round-2 Judge A required to fully
  falsify the guard.
- **Slice 2, round 2 — a REVERTED suggestion that would have introduced a
  double-INSERT-on-retry.** Round 1's applied suggestion (build the `usuario`
  entity inside the `ExecutionStrategy` retry lambda) was flagged by Judge A as
  risking a duplicate row on a transient retry (the `ChangeTracker` still holds
  the failed attempt's entity; a second `Add` inside the lambda persists both
  on the retried commit). Resolved by reverting to construction OUTSIDE the
  lambda — a retry re-`Add`s the SAME instance, idempotent by construction.
- **PR #129's CRITICAL — the fix was not complete.** Judge A found that
  `POST /api/articulos/{id}/precios/programados` (`ServicioDePrecios.
  CerrarFilaAsync`, raw ADO) still returned `500` with a client offset, because
  the EF-level `NormalizacionAUtc` convention doesn't reach raw-ADO parameter
  writes. The fix's own doc-comment falsely claimed no raw path was affected.
  Closed by normalizing inside the parameter helper of every raw-ADO
  service — which turned out to be **duplicated across 16 files** (14 touched;
  2 are `int`-only and out of scope). Re-round: zero findings, all 19 call
  sites verified one by one. **Judge B's framing correction, recorded for
  honesty**: the `500` was **not reachable from Ways' own UI** —
  `Articulos.tsx` always sends `toISOString()` (offset `Z`) — but IS reachable
  from any other client of the public API, whose DTO accepts an arbitrary
  `DateTimeOffset`. The defect is real; its surface is the API, not the
  first-party UI.

## Backlog Registered By This Stage

- **13 of the 14 raw-ADO parameter-helper normalizations from PR #129 are
  defense in depth, not live defects** — a full mutation pass over the sibling
  helpers found that only `Precios`' helper had a client-reachable offset limit
  today; the other 13 protect paths with no current client-supplied
  non-UTC `DateTimeOffset`, registered rather than presented as fixing an
  active bug.
- **In 5 of the 16 helper files the normalized branch is unreachable** given
  today's call sites (no caller ever passes a non-UTC offset through them) —
  registered, not treated as a defect.
- **An open chip to unify the 16 duplicated copies of the raw-ADO parameter
  helper** (`AgregarParametro`/`AgregarParametroNulo`) into one shared
  implementation — the root cause PR #129's judgment-day named but explicitly
  left open rather than folding a refactor into a bug-fix PR.
- **Suite-wide limitation, carried forward, not new to this stage**: no test in
  the whole web suite imports `Layout`/`App`, so the Admin-only nav-gate and
  route-gate are both structurally infalsifiable (widening either to Supervisor
  passes the entire suite). The real backstop is the server's `403`, proven by
  `PoliticasTests` in slices 5/6. Same registered gap as stage 13's slice 6 and
  every prior reportes-de-gestión screen — no new pattern to replicate without
  opening a test surface outside this round's scope.
- **`compararPayloads.ts`'s `sonIguales` is sensitive to JSON key order**
  (`JSON.stringify` comparison) — documented as a known, low-risk limitation
  (both payloads originate from the same serializer) and pinned with a test
  that asserts current behavior rather than silently left undocumented.

## Verify Verdict (Final-State Authority)

Per `verify-report.md` (2026-08-17, HEAD `8c262ba`, covering all 7 slice PRs plus
the 3 post-slice chip PRs #129-#131): **PASS WITH WARNINGS**.

- **0 CRITICAL.**
- **W1 (doc-10 annotation drift, the stage-13 drift class recurring)** —
  **remediated pre-archive by the orchestrator**. The "Estado (Etapa 14)"
  annotation was written once at slice 1 ("tabla + writer implementados, sin
  call sites todavía…") and never revisited across slices 2-7; it now describes
  the completed feature end to end (12 call sites, the structural exclusion of
  `stock.transferencia`, one-row-per-conteo-operation, the Admin-only query and
  its export, the screen, and the data-proven fail-closed rule).
- **W2 (informational, already fixed on `main`, no action required for this
  change)** — PR #129 (a post-stage-14 chip) closed a UTC-offset gap in the raw-
  ADO paths that four of this stage's own files write through; stage 14's own
  testing strategy pins `RelojFijo` at `…T12:00:00Z` (always UTC), so the gap
  was structurally invisible to its own suite. Already closed on `main` before
  archive; the same chip PR added rule 10 to `mutation-proof-tests` (date-edge
  tests must send the CLIENT's real offset, never `Z`).
- All 10 requirements / 31 scenarios mapped to passing tests; the migration gate
  (exactly one migration, matching the contract + amendment 1) PASS; checkout
  protection PASS (`VentasCheckoutTests.cs` absent from the diff of the whole
  stage AND the 3 chip PRs, `Assert.Equal(16, …)` intact); fail-closed proven BY
  DATA (real `23503`/`fk_auditoria_actor`, real rollback, the same exception
  mapped to `400 referencia_invalida` by the real, unmodified
  `ManejadorDeErrores`) — no `IEscritorDeAuditoria` test seam anywhere.

This report is the terminal record of the change per the Final-State Authority
hierarchy: it supersedes any "pending" framing for W1 (fixed pre-archive, cited
above) and records W2 as informational and already closed on `main`, not as an
open item carried into archive.

## Rollback Note (carried from proposal, unaffected by archival)

Per slice: additive code over one new, otherwise-unreferenced table — reverting
any slice after 1 leaves `ServicioDeAuditoria` intact and simply unused for that
slice's call sites. Slice 1 alone: `DROP TABLE auditoria`, no dependent object
(no enum value added, no existing column altered, no existing row rewritten).
Whole stage: revert the code — there is no irreversible artifact of any kind.

## SDD Cycle Complete

The change has been fully planned (explore → propose → spec → design → tasks),
implemented (7 slices, PRs #123-#128 and #132, plus 3 adjacent chip PRs #129-#131
merged after the slices), verified (PASS WITH WARNINGS, W1 remediated, W2
informational), and archived (this report). Ready for the next change.
