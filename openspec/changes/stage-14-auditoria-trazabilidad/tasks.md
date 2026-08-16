# Tasks: Stage 14 — Auditoría y trazabilidad de operaciones sensibles

## Orchestrator Decisions Recorded This Phase

1. **7 slices, 7 PRs, stacked-to-main** — design.md's ratified breakdown
   (re-confirmed, not re-scoped, from the proposal's own tentative plan).
   Merge order: `1` blocks everything → `{2, 3, 4}` disjoint and parallel
   once `1` merges (they touch `ServicioDePrecios`/`ServicioDeUsuarios`,
   `ServicioDeVentas`/`ServicioDeCompras`, and
   `ServicioDeStock`/`ServicioDeReliquidacion` respectively — no shared
   file) → `5` (needs `1` for the table, reuses nothing from `2`/`3`/`4`) →
   `6` (needs `5`'s `ConstruirQuery`/`ServicioDeConsultaDeAuditoria`) → `7`
   last (needs `5` for the query contract and `6` for the download route).
   Format reference: the archived
   `2026-08-16-stage-13-stock-inteligente/tasks.md` structure — per-slice
   Start/Finish/Rollback, hierarchical task numbering, `[P]` for
   parallelizable test tasks, a Verify line, and a closing Review Workload
   Forecast.
2. **DB gate is `UNA-MIGRACION-APROBADA`** (`state.yaml`) — slice 1 carries
   **exactly one** new migration, named `AuditoriaEtapa14`, matching the
   proposal's gate §A verbatim (10 columns, 1 PK, 3 FKs, 2 CHECKs, 3
   indexes, standard RLS). Slices 2-7 each carry a gate-guard task requiring
   `dotnet ef migrations has-pending-model-changes` clean **and** zero new
   files under `src/Ways.Infrastructure/Persistencia/Migraciones/` in
   `git diff --stat`. Any slice that finds itself needing schema change
   STOPs and reopens the gate.
3. **No `size:exception` is anticipated on any slice.** Four pre-authorized
   cut points are named, inherited verbatim from design.md's Slicing
   section, and recorded again in their own slice below: `1a`/`1b` (split
   the migration+entity+RLS from the catalog+writer if slice 1 overflows —
   the migration must not ship in a droppable half), `5a`/`5b` (split
   policy+date/accion/actor filters from entidad/PV+pagination if slice 5
   overflows), and dropping `PanelDeCambio`/`compararPayloads` if slice 7
   overflows (the payload still reaches the operator through the export).
   Coverage of the twelve actions and the fail-closed rule are **never**
   degraded — a coverage slice splits, it is never trimmed.
4. **`judgment-day` runs once per slice**, on that slice's diff, before its
   PR — per `protocolo-pr-solo-dev`. Seven independent rounds.
5. **CONFLICT FOUND AND RESOLVED #1 — `stock.conteo` row cardinality.**
   `spec.md` and `design.md` ran **in parallel** during this planning pass,
   and design.md's own Open Questions section (last entry) already flags
   the disagreement without resolving it. `design.md`'s call-site table
   (row 11, `Stock/ServicioDeStock.cs:743` and `:810`) reads "Una fila **por
   movimiento de ledger escrito**" — i.e. one `auditoria` row per lote in a
   conteo por lote with N differing lotes, N rows. `specs/auditoria-de-
   operaciones/spec.md:97-98` is unambiguous and unconditional: "Each
   operation MUST write **exactly one** row" — no per-action carve-out is
   granted, and the requirement's own scenario list treats the
   close/reopen price dance the same way (one operation, one row).
   **The spec's letter is authoritative** — it is not stale prose that
   predates a decision, it is the ratified requirement, and it matches the
   proposal's own founding argument (Approach section, and the risk table's
   "Duplicated actor stamp diverging from the ledger" row): the audit row
   is a seal-plus-pointer, never the arithmetic, so the detail per lote
   already lives actor-stamped in `movimientos_stock` — the audit row does
   not need to repeat it lote by lote.
   **Resolution**: `stock.conteo`'s call sites (`EjecutarConteoAsync` and
   `EjecutarConteoPorLoteAsync`) accumulate `movimientos_generados` (the
   `id_movimiento_stock` list), `lotes_afectados` (count), and `delta_total`
   across the existing loop over lotes/agregado, and write **one**
   `RegistrarAsync` call **after** the loop, per counting **operation** —
   not per ledger movement. The payload widens from the proposal's
   `{cantidad, id_movimiento_stock, observaciones}` (single-lote shape) to
   the aggregate shape above; `id_movimiento_stock` (singular) is dropped
   from `stock.conteo`'s `valor_nuevo` in favor of `movimientos_generados`
   (plural) precisely because a single scalar cannot honestly represent N
   writes. The existing zero-difference early return (`:721-727`) is
   unchanged: it still produces zero ledger rows and, now unambiguously,
   zero audit rows for the whole operation. See slice 4, task 4.4 and its
   discriminating test 4.11 — flagged here so `sdd-apply` does not silently
   follow design.md's stale per-lote text.
6. **CONFLICT FOUND AND RESOLVED #2 — `usuario.baja` payload.** The
   proposal's payload table (decision 2, line 214) reads
   `{estado:"eliminado"}` for `usuario.baja`'s `valor_nuevo`. That value is
   **not constructible**: `EstadoUsuario` is `Activo | Inactivo | Bloqueado`
   (no `Eliminado` member), and `ServicioDeUsuarios.EliminarAsync` performs
   a soft delete by writing `deleted_at`, never by changing `estado`.
   Design.md's own call-site table (row 4) already diverges from the
   proposal here and uses the correct shape —
   `ant: {deleted_at: null, estado}` · `nuevo: {deleted_at: <momento>,
   estado}` — flagged in design.md's Open Questions as needing
   reconciliation with the spec, which ran in parallel. **The design's call
   site is authoritative**; the proposal's payload table entry for
   `usuario.baja` is stale text, the same treatment `sinSugerencia` got in
   stage 13: the design/spec mandate, and `sdd-apply` must not follow the
   proposal's literal `{estado:"eliminado"}`. See slice 2, task 2.5.
7. **`mutation-proof-tests` compliance**: the **28** named mutation targets
   in design.md's table are each placed in exactly one slice below (1:
   8 targets, 2: 4, 3: 3, 4: 3, 5: 6, 6: 3, 7: 1 — 8+4+3+3+6+3+1 = 28).
   Every one requires apply-time evidence (mutation applied → named failing
   test → reverted → green) recorded in its slice's PR body, per design's
   binding verify criterion 4. Design's 29th table row (the checkout
   no-regression row, marked "—", no slice number) is **not** one of the
   28 — it is a binding verify criterion, placed as task 3.12.
8. **`dto-contract-honesty` applies at slices 1, 5 and 7** — the three
   slices that introduce or mirror a data contract: slice 1's
   `RegistroDeAuditoria`/`PayloadDeAuditoria`/`AccionAuditada` (Domain
   contracts, even though not literally named `Contratos.cs`), slice 5's
   `Application/Auditoria/Contratos.cs` (`FiltrosDeAuditoria`/
   `FilaDeAuditoria`/`PaginaDeAuditoria`), and slice 7's `tipos.ts` mirror.
9. **`db-error-backstops` applies once, in slice 1** — the SQLSTATE `23503`
   fail-closed test gate §B's documented exemption requires (`id_actor` is
   always server-derived, never client input; covered anyway by the
   existing generic `fk_`/`23503` → `400 referencia_invalida` mapping,
   `ManejadorDeErrores.cs:224`, **unmodified**). Task 1.28.
10. **`react-async-state` + `web-descriptor-tests` apply to slice 7 only**
    — the single web-touching slice in this stage (unlike stage 13's
    three). Every new/modified pure helper (`compararPayloads`) ships a
    colocated `*.test.ts(x)`.
11. **`work-unit-commits` applies to every slice** — each slice's
    implementation tasks land as logical, reviewable commits, not one
    monolithic diff per branch.
12. **Test dates are fixed, never wall-clock-relative.** Every date-bearing
    test pins the clock at `RelojFijo(2026-08-14T12:00:00Z)` (mediodía UTC,
    per design's testing-strategy table), so `creado_el` assertions are
    exact equalities, never range checks, and stay stable in UTC and
    `America/Argentina/Buenos_Aires`.
13. **Checkout-budget protection is a binding verify criterion, not a
    task any other slice could violate**: `tests/Ways.IntegrationTests/
    VentasCheckoutTests.cs` MUST be **absent from the stage's diff
    entirely** (not merely unedited in its `Assert.Equal(16, …)` line) —
    the checkout emission path is out of scope by decision (proposal
    decision 4), not by technique, so nothing in any slice has a reason to
    touch that file. Placed as task 3.12, the only slice that opens
    `ServicioDeVentas.cs`.
14. **The doc-10 update (the `auditoria` table + "Estado (Etapa 14)"
    annotation) is a slice-1 task** (1.11), not a closing sweep — the same
    discipline stage 12/13 used for their own doc-10 updates from inside a
    slice.
15. **`ServicioDeStock.InsertarMovimientoStockAsync`'s signature ripple**
    (`Task` → `Task<int>`, `RETURNING id_movimiento_stock`) is registered
    here so `sdd-verify` does not read it as a scope violation: it is the
    one change outside the twelve named call sites, needed to feed
    `stock.ajuste`/`stock.decomiso`/`stock.conteo`'s payloads.
    `TransferirAsync` ignores the returned value and stays byte-identical
    in behavior — and still writes zero `auditoria` rows (proposal decision
    5). Task 4.1.

---

## Suggested Work Units

| Unit | Goal | Branch | Depends on | ~Lines |
|---|---|---|---|---|
| 1 | Migration `AuditoriaEtapa14` + `Auditoria` entity/config/RLS + `AccionAuditada`/`RegistroDeAuditoria`/`PayloadDeAuditoria`/`SerializadorDeAuditoria` + `ServicioDeAuditoria` writer (both modes, no call site yet) + doc-10 | `feat/stage14-slice1-tabla-auditoria` | none | ~430 |
| 2 | `precio.cambio` + the five `usuario.*` call sites (EF transactions) | `feat/stage14-slice2-precios-usuarios` | 1 | ~340 |
| 3 | `venta.anulacion` + `compra.anulacion` (ADO transactions) + checkout non-regression | `feat/stage14-slice3-anulaciones` | 1 | ~300 |
| 4 | `stock.ajuste`/`decomiso`/`conteo` (one row per operation, decision #1 above) + `cc.reliquidacion` | `feat/stage14-slice4-stock-cc` | 1 | ~320 |
| 5 | `LecturaDeAuditoria` policy + `ConstruirQuery`/`ConsultarAsync` + `GET /api/auditoria` | `feat/stage14-slice5-consulta` | 1 | ~380 |
| 6 | `GET /api/auditoria/export` + `ExportacionDeAuditoria` mapper | `feat/stage14-slice6-export` | 5 | ~230 |
| 7 | `Auditoria.tsx` (filters, pager, `PanelDeCambio`, download, nav) | `feat/stage14-slice7-web` | 5, 6 | ~360 |

**Parallelism.** `1` blocks everything. After it merges: `2`
(`Precios`/`Usuarios`), `3` (`Ventas`/`Compras`) and `4`
(`Stock`/`CuentaCorriente`) touch fully disjoint service files and are
genuinely foldable in parallel. `5` needs only `1` (the table and the
writer's read-side symmetry, not any call site — its integration tests
seed `auditoria` rows directly). `6` needs `5`'s `ConstruirQuery`/
`ServicioDeConsultaDeAuditoria`. `7` needs `5` (the query contract its
client mirrors) and `6` (the download route). Total ≈ **2 360 lines**.

---

## Slice 1: Tabla + Writer (PR 1)

**Start**: `main`. **Finish**: `auditoria` exists with standard RLS; the
Domain contracts (`AccionAuditada`, `RegistroDeAuditoria`,
`PayloadDeAuditoria`, `SerializadorDeAuditoria`) are pure and DB-free;
`ServicioDeAuditoria` exposes both enlistment modes (`Registrar`/
`RegistrarAsync`) and is called by **nobody yet**; doc-10 carries the table.
**Rollback**: revert the branch — `DROP TABLE auditoria`, no dependent
object (proposal Rollback Plan): no enum value added, no existing column
altered, no existing row rewritten.

**Budget note (design.md)**: pre-authorized split if this slice overflows —
`1a` (migration, entity, `AuditoriaConfiguration`, `DbSet`, tenant filter,
RLS + their tests, doc-10) and `1b` (catalog, invariants, payload
factories, serializer, writer, their tests). The migration must not ship in
a slice that might be dropped, so this split is pre-authorized, not
optional.

- [x] 1.1 Create the migration `AuditoriaEtapa14`: `auditoria` table exactly
  per proposal §A (the gate contract) — `id_auditoria bigint GENERATED BY
  DEFAULT AS IDENTITY`, `id_tenant integer NOT NULL`, `id_punto_venta
  integer NULL`, `id_actor integer NOT NULL`, `accion text NOT NULL`,
  `entidad text NOT NULL`, `id_entidad integer NOT NULL`, `valor_anterior
  jsonb NULL`, `valor_nuevo jsonb NOT NULL`, `creado_el timestamptz NOT
  NULL` (no `DEFAULT`); `pk_auditoria`; `fk_auditoria_tenant` (`ON DELETE
  RESTRICT`); `fk_auditoria_punto_venta` composite `(id_punto_venta,
  id_tenant)` MATCH SIMPLE (`ON DELETE RESTRICT`); `fk_auditoria_actor`
  simple (`ON DELETE RESTRICT`); `ck_auditoria_accion_no_vacia`;
  `ck_auditoria_entidad_no_vacia`; `ix_auditoria_tenant_creado`,
  `ix_auditoria_entidad`, `ix_auditoria_actor`;
  `migrationBuilder.HabilitarRlsDeTenant("auditoria")`. **Nothing else** —
  no existing table/column/index/enum touched. *(proposal §A, gate
  contract — reopening it is out of bounds)*
  **DEVIATION (registered, not silent):** `dotnet ef migrations add`
  auto-generates one FK-support index per FK whose columns are not the
  exact leading prefix of an already-declared index — neither
  `ix_auditoria_actor` (leads with `id_tenant`) nor any of the 3 gate
  indexes leads with `id_actor` or `(id_punto_venta, id_tenant)`. EF's
  `ForeignKeyIndexConvention` is not model-time-suppressible without
  fighting the convention pipeline (confirmed empirically: a synchronous
  `RemoveIndex` right after `HasForeignKey(...)` does not stick — the
  convention batch re-adds it). Rather than ship the ugly PascalCase
  default names (`IX_auditoria_id_actor`, `IX_auditoria_id_punto_venta_id_tenant`,
  violating doc-10's snake_case convention), two additional indexes were
  named explicitly: `ix_auditoria_id_actor` (`id_actor`) and
  `ix_auditoria_punto_venta` (`id_punto_venta, id_tenant`) — same pattern
  every other FK in this schema already uses (`ix_movimientos_stock_empleado`,
  `ix_comprobantes_venta_empleado`, etc.), never separately counted in any
  prior stage's gate. Total indexes on `auditoria`: 5 (3 named by the gate
  + 2 FK-support), not the "3 indexes" the gate's model-summary table
  states as a business-index count. No ALTER on any existing object, no
  data statement, no extra table/column/constraint/enum — the migration
  otherwise matches §A verbatim. Flagged here for `sdd-verify`/orchestrator
  ratification rather than assumed authorized.
- [x] 1.2 Create `src/Ways.Domain/Auditoria/Auditoria.cs`: immutable entity,
  no `EntidadBase` inheritance, no mutators — the `movimientos_stock`
  criterion. *(design File Changes)*
- [x] 1.3 Create `src/Ways.Domain/Auditoria/AccionAuditada.cs`: `sealed
  record (string Accion, string Entidad)`, 12 `static readonly` instances +
  `Todas`. `dto-contract-honesty`: doc-comment that the record fixes the
  **pair**, so no call site can mismatch an `accion` with the wrong
  `entidad`. *(design decision 4)*
- [x] 1.4 Create `src/Ways.Domain/Auditoria/RegistroDeAuditoria.cs`:
  constructor invariants — `valorNuevo` non-empty; key-subset rule
  (`valorAnterior` keys ⊆ `valorNuevo` keys, not the reverse); denylist over
  keys (`password`/`contrasena`/`hash`/`token`/`secret`, case-insensitive);
  snake_case key shape (`^[a-z][a-z0-9_]*$`); violation ⇒
  `InvalidOperationException`, never `ErrorDominio` (a call-site defect,
  not a client error). `dto-contract-honesty`: doc-comment each rule as the
  contract every downstream payload factory must satisfy. *(design decision
  3)*
- [x] 1.5 Create `src/Ways.Domain/Auditoria/PayloadDeAuditoria.cs`: 12
  static factories (one per `AccionAuditada`), **none accepting an
  entity** — the structural defense against a full-row dump.
  `dto-contract-honesty`: doc-comment each factory's field list against the
  proposal's per-action payload table (decision 2). *(design decision 5)*
- [x] 1.6 Create `src/Ways.Application/Auditoria/SerializadorDeAuditoria.cs`:
  one `JsonSerializerOptions`, `DictionaryKeyPolicy =
  JsonNamingPolicy.SnakeCaseLower` (**not** `PropertyNamingPolicy` — a
  no-op over a dictionary payload) + `JsonStringEnumConverter(
  JsonNamingPolicy.SnakeCaseLower)`; **not** registered globally, owned by
  this class alone. *(design decision 6)*
- [x] 1.7 Create
  `src/Ways.Infrastructure/Persistencia/Configuraciones/AuditoriaConfiguration.cs`:
  EF mapping; `ValorAnterior`/`ValorNuevo` as `string?`/`string` with
  `HasColumnType("jsonb")`; the three indexes mirrored.
- [x] 1.8 Modify `src/Ways.Infrastructure/Persistencia/WaysDbContext.cs`:
  `DbSet<Auditoria> Auditoria` + a dedicated
  `AplicarFiltroDeTenantEnAuditoria`, cloned from
  `AplicarFiltroDeTenantEnMovimientoStock` — `Auditoria` does **not**
  inherit `EntidadTenant`, `IdTenant` is written explicitly by the caller
  (never `EstamparTenant()`, which would silently overwrite the subject's
  tenant with the session's). *(design decision 7)*
- [x] 1.9 Modify `src/Ways.Application/Abstracciones/IWaysDbContext.cs`:
  `DbSet<Auditoria> Auditoria`.
- [x] 1.10 Create `src/Ways.Application/Auditoria/ServicioDeAuditoria.cs`:
  `Registrar(RegistroDeAuditoria)` — EF mode, sync, no I/O
  (`db.Auditoria.Add`, flushed by the caller's own `SaveChangesAsync`);
  `RegistrarAsync(DbConnection conexion, DbTransaction? transaccion,
  RegistroDeAuditoria registro, CancellationToken ct)` — ADO mode, **one**
  `INSERT` with no `RETURNING`, explicit `::jsonb` casts,
  `ExecuteNonQueryAsync`; `transaccion is null ⇒
  InvalidOperationException` (a row is never written outside a
  transaction). Both modes stamp `id_actor = contexto.UsuarioId` and
  `creado_el = reloj.Ahora` internally — **never** parameters. Neither mode
  calls `BeginTransaction`/`SaveChanges`/`Commit`. *(design decisions 1, 2)*
- [x] 1.11 Modify `docs/10-modelo-de-datos.md`: add the `auditoria` table
  with its "Estado (Etapa 14)" annotation, in §6-adjacent position, per the
  proposal's In Scope item and the stage-12/13 discipline of doing this
  from inside a slice. *(proposal — In Scope)*
  **Note**: landed as a new "## 10. Auditoría" section (adjacent to §9,
  before the reference diagram) rather than literally inside §6 —
  doc-10's §6 already closed its own scope around `movimientos_stock`/
  `lotes`/`stock_lotes`, and the "Etapas sugeridas" table is explicitly
  closed at Etapa 8 ("última fila de esta tabla"), so a standalone section
  matches the doc's actual structure better than shoehorning a
  multi-domain capability into the stock section.
- [x] 1.12 [P] Domain unit suite for `RegistroDeAuditoria` (`PoliticaDeRoles`
  pattern, no DB): subset OK; extra key in `valorAnterior` ⇒ throw;
  `valorAnterior` null legal; `valorNuevo` empty ⇒ throw; a `hash_password`
  key ⇒ throw; a PascalCase key ⇒ throw.
- [x] 1.13 [P] Domain unit: `AccionAuditada.Todas` has 12 entries, no
  duplicates, every `Accion` matches `<dominio>.<operacion>`, every
  `Entidad` non-empty.
- [x] 1.14 [P] Domain unit, generic over the 12 `PayloadDeAuditoria`
  factories: no factory's output ever violates the subset rule, the
  denylist, or snake_case — one test loop over `AccionAuditada.Todas`
  paired with each factory's canned inputs.
- [x] 1.15 [P] Domain unit: `SerializadorDeAuditoria` — keys serialize
  snake_case; an enum value serializes as its base label (e.g.
  `EstadoComprobante.Emitido` → `"emitido"`, not `"Emitido"`);
  `DateTimeOffset` serializes ISO-8601; an explicit `null` value is
  distinguishable from an absent key. Lives in
  `Ways.Application.Tests/Auditoria/SerializadorDeAuditoriaTests.cs` (the
  class itself is Application, not Domain — no DB either way).
- [x] 1.16 [P] **Mutation target**: the key-subset check in
  `RegistroDeAuditoria`'s constructor — delete it — the extra-key Domain
  fact (1.12) must fail. *(design mutation-targets table, slice 1 row 1;
  mutation-proof-tests)* **Evidence**: mutated (check deleted) → `dotnet
  test tests/Ways.Domain.Tests --filter FullyQualifiedName~RegistroDeAuditoriaTests`
  → `UnaClaveExtraEnValorAnteriorLanza` FAILED (no exception thrown) →
  reverted → 14/14 green.
- [x] 1.17 [P] **Mutation target**: the denylist check — delete it — the
  `hash_password` Domain fact (1.12) **and** the `usuario.*` denylist
  integration test (slice 2, task 2.19) must both fail. *(slice 1 row 2)*
  **Evidence (slice-1 half)**: mutated (denylist loop deleted) → same
  filter → all 5 `UnaClaveDeLaDenylistLanza` theory cases FAILED → reverted
  → 31/31 green. The slice-2 half (task 2.19) is cross-slice, per this
  task's own text — deferred, not evaluated here.
- [x] 1.18 [P] **Mutation target**: `DictionaryKeyPolicy =
  SnakeCaseLower` → swap for `PropertyNamingPolicy` (a no-op over a
  dictionary) — the snake_case serializer test (1.15) must fail. *(slice 1
  row 3)* **Evidence**: first attempt with an already-snake_case input key
  did NOT fail under mutation (mutation-proof-tests rule 3 confound — the
  input never needed transforming, so both policies produced the same
  output). Test strengthened to use a PascalCase input key
  (`SerializadorDeAuditoriaTests.LasClavesSerializanEnSnakeCase`,
  committed separately). Re-mutated → FAILED (`"IdListaPrecio"` left
  untransformed, `"id_lista_precio"` absent) → reverted → 5/5 green.
- [x] 1.19 [P] **Mutation target**: `JsonStringEnumConverter(
  SnakeCaseLower)` — remove it — `estado` serializes `"Emitido"` instead of
  `"emitido"`, the enum-label serializer test (1.15) must fail. *(slice 1
  row 4)* **Evidence**: mutated (converter removed) → `estado` serialized
  as `0` (numeric) instead of `"emitido"` → `UnEnumSerializaComoSuEtiquetaDeBase`
  FAILED → reverted → 5/5 green.
- [x] 1.20 [P] **Mutation target**: `transaccion is null ⇒ throw` in
  `RegistrarAsync` — delete the guard — the no-transaction test (1.21) must
  fail. *(slice 1 row 5)* **Evidence**: mutated (guard deleted) →
  `RegistrarAsyncSinTransaccionLanza` FAILED (`NullReferenceException`
  instead of the intended `InvalidOperationException` — the guard's job is
  precisely to turn that crash into a named business exception before
  touching `conexion`) → reverted → 5/5 green.
- [x] 1.21 [P] Integration: calling `RegistrarAsync` with `transaccion =
  null` throws `InvalidOperationException` — proves 1.20's guard and is the
  ADO-world half of fail-closed. Implemented as
  `Ways.Application.Tests/Auditoria/ServicioDeAuditoriaGuardTests.cs` — no
  Postgres needed, the guard throws before touching `conexion`.
- [x] 1.22 [P] **Mutation target**: `HabilitarRlsDeTenant("auditoria")` in
  the migration — delete the line — the cross-tenant read test (1.23) and
  the `42501` INSERT test (1.24) must both fail. *(slice 1 row 6)*
  **Evidence**: mutated (RLS line deleted from the migration, fresh
  testcontainer) → both `RlsBloqueaLaLecturaCrossTenantSobreWaysApp`
  (cross-tenant row visible, expected 0 got 1) and
  `UnInsertConIdTenantAjenoSeRechaza` (no exception thrown) FAILED →
  reverted → 5/5 green.
- [x] 1.23 [P] Integration — RLS over `ways_app` (`mutation-proof-tests`
  rule 5, NOSUPERUSER NOBYPASSRLS): a raw SQL `SELECT` with
  `app.tenant_id = 1` over rows seeded for tenants 1 and 2 returns **only**
  tenant 1's rows, by row count. *(spec `auditoria-de-operaciones`:
  scenario "RLS blocks a cross-tenant read over the ways_app connection")*
- [x] 1.24 [P] Integration: an `INSERT` into `auditoria` supplying a
  foreign `id_tenant` under `app.tenant_id = 1` is refused with SQLSTATE
  `42501`. *(spec `auditoria-de-operaciones`: scenario "An INSERT with a
  foreign id_tenant is refused")*
- [x] 1.25 [P] **Mutation target**: `creado_el = reloj.Ahora` →
  `DateTimeOffset.UtcNow` — the exact-equality `RelojFijo` test (1.26) must
  fail. *(slice 1 row 7)* **Evidence**: mutated ADO mode alone →
  `ElModoAdoEstampaCreadoElExactamenteIgualAlRelojFijo` FAILED (wall-clock
  value, not `2026-08-14T12:00:00Z`) while the EF-mode test stayed green →
  reverted → mutated EF mode alone → `ElModoEfEstampaCreadoElExactamenteIgualAlRelojFijo`
  FAILED the same way while ADO-mode stayed green → reverted → 5/5 green.
  Each mode's assignment tested independently since `Registrar`/
  `RegistrarAsync` each stamp `creado_el` in their own separate line.
- [x] 1.26 [P] Integration: the writer stamps `creado_el` **exactly equal**
  to `RelojFijo(2026-08-14T12:00:00Z)` (mediodía UTC — Orchestrator Decision
  12). Two tests (ADO + EF mode), both in `AuditoriaEscrituraTests.cs`.
- [x] 1.27 [P] **Mutation target**: `id_actor = contexto.UsuarioId` → a
  literal or a caller-supplied parameter — no integration test in slice 1
  can observe this (no call site exists yet); its evidence is collected
  once slice 2's coverage tests exist (task 2.8+), where the expected actor
  would fail to appear. Recorded here as the target and cross-slice
  dependency, per design's own framing ("cobertura de cualquier acción").
  *(slice 1 row 8)* **Not evaluated in this slice, per its own text** —
  slice 2 must run this mutation once tasks 2.8+ land.
- [x] 1.28 [P] `db-error-backstops`: the SQLSTATE `23503` fail-closed test
  gate §B's documented exemption requires — a session whose
  `contexto.UsuarioId` points at a non-existent `usuario` makes
  `Registrar`/`RegistrarAsync` raise `23503` on `fk_auditoria_actor`
  **inside** the caller's transaction; asserted by SQLSTATE, and the
  existing generic `fk_`/`23503` → `400 referencia_invalida` prefix mapping
  (`ManejadorDeErrores.cs:224`, unmodified) is confirmed rather than
  assumed. *(design decision 10; gate §B)*
  `FkAuditoriaActorRechazaUnActorInexistenteYSeMapeaA400ReferenciaInvalida`
  asserts the real `PostgresException.SqlState == "23503"` /
  `ConstraintName == "fk_auditoria_actor"`, that the transaction rollback
  leaves zero rows, and that the SAME exception through `ManejadorDeErrores`
  maps to `400`/`referencia_invalida`.
- [x] 1.29 Gate guard: `dotnet ef migrations has-pending-model-changes`
  reports no pending changes; `git diff --stat main --
  src/Ways.Infrastructure/Persistencia/Migraciones/` shows **exactly one**
  new file, named for `AuditoriaEtapa14`. **Both confirmed**: "No changes
  have been made to the model since the last migration."; `git status
  --short` on the migrations dir shows exactly one new pair
  (`20260816044634_AuditoriaEtapa14.cs` + `.Designer.cs`) plus the modified
  snapshot.
- [x] 1.30 Run `judgment-day` on the slice diff; fix confirmed issues;
  re-judge until clean.
  - Ronda 1 (juez B): 0 severos; 3 WARNING (findings 1-3). Fixed y con
    evidencia de mutación (commit `7b30f73`): finding 1 (denylist/snake_case
    ahora recursiva sobre diccionarios anidados), finding 2 (test que
    discrimina tenant de sesión vs tenant sujeto, design decisión 7), finding
    3 (doc-honesty de `AccionAuditada` + test que congela el catálogo de 12
    pares). Finding 4 (`comando.Transaction`) registrado como mutante
    equivalente bajo el driver Npgsql — sin fix (no hay assertion barata que
    lo discrimine sin acoplarse al driver). Finding 5 (CHECKs de la
    migración) es survivor CONTRACTUADO por el gate §B — no aplica fix.
  - Re-ronda B: fixes de ronda 1 verificados. Residual R2-B-1 (WARNING)
    cerrado: `ValidarValorAnidado` ganó un case `System.Collections.IDictionary`
    entre `IReadOnlyDictionary<string, object?>` e `IEnumerable`, cubriendo
    diccionarios anidados con otro `TValue` (invarianza de tipos) y
    `Hashtable` no genéricos, que antes caían al case `IEnumerable` sin
    validar sus claves. Clave no-string en un diccionario no genérico se
    rechaza (no se valida por `ToString()`). 3 tests nuevos, 2 de ellos los
    probes exactos del juez; evidencia de mutación (borrar el case → los 2
    tests fallan → revert → verde).
- [x] 1.31 Branch `feat/stage14-slice1-tabla-auditoria` off `main`; PR; *(CLEAN 2026-08-16: juez B ronda 1 — 0 severos, 3 WARNINGs fixeados (denylist recursiva, tenant sujeto-vs-sesion discriminado via modo plataforma, doc del catalogo honesto) + 2 disposiciones sin fix registradas (mutante equivalente Npgsql; CHECKs contractuados por gate SB); re-ronda B aprobada con R2-B-1 residual cerrado por micro-fix (case IDictionary — Dictionary<string,string>/Hashtable anidados ahora validados, claves no-string rechazadas); juez A fresh read-only: CERO hallazgos — verifico la migracion columna por columna contra el gate + enmienda 1, las 12 factories sin secretos alcanzables, y el orden GRANT-vs-migracion del fixture. JUDGMENT: APPROVED.)*
  merge stacked-to-main.

**Test plan**: Domain suite (1.12-1.15), 8 mutation targets (1.16-1.20,
1.22, 1.25, 1.27), RLS + `42501` (1.23-1.24), transaction guard (1.21),
fixed clock (1.26), SQLSTATE backstop (1.28).

**Verify**: `dotnet test --filter FullyQualifiedName~Auditoria`

---

## Slice 2: Precios + Usuarios (PR 2)

**Start**: slice 1 merged. **Finish**: `precio.cambio` and the five
`usuario.*` actions each write exactly one `auditoria` row in the same
transaction as the business write, fail-closed. **Rollback**: revert the
branch — the call sites disappear, `auditoria` and its writer stay intact
and simply unused for these paths.

- [x] 2.1 Modify `src/Ways.Application/Precios/ServicioDePrecios.cs:186`
  (just before `db.Precios.Add`, inside the transaction opened at `:94`):
  `auditoria.Registrar(...)` with `PayloadDeAuditoria.CambioDePrecio`,
  `valorAnterior` = `filaAbierta`'s `{id_lista_precio, monto,
  vigente_desde}` or `NULL`, `valorNuevo` = the same keys with the new
  values. **One** call per operation — after the close/reopen dance, not
  once per closed row. *(design call site 1; spec `precios`: "A price
  change is attributable to its actor")*
  **DEVIATION (registered):** `ServicioDeAuditoria` landed as an OPTIONAL
  constructor parameter (`ServicioDeAuditoria? auditoria = null`, guarded
  by a fail-loud `Auditoria` property) instead of required — a required
  4th parameter broke compilation of 9 pre-existing test files (10
  instantiation lines — `VentasCheckoutTests.cs` has two), including
  `tests/Ways.IntegrationTests/VentasCheckoutTests.cs`, the one file
  Orchestrator Decision 13 forbids touching in ANY slice. Verified none of
  those call sites ever reach `AbrirNuevoPrecioAsync` (all read-only via
  `ServicioDeOfertas.PreciosVigentesEnLoteAsync` or price resolution), so
  the guard is never exercised there; every real/DI caller still gets the
  real instance. See the class doc-comment for the full writeup. **Judgment-day
  slice 2 (round 1, judge B, WARNING):** the count was originally
  misreported as "12" — recounted precisely against the actual test tree
  and corrected here and in the class doc-comment. Guard coverage added:
  `ServicioDePreciosSuperficieTests.ElGuardDeAuditoriaAusenteFallaFuerteEnVezDeSaltearseEnSilencio`
  (reflection over the private `Auditoria` property — the real write path
  can't run under InMemory, same documented limitation as
  `ServicioDeUsuarios.CrearAsync`).
- [x] 2.2 Modify `ServicioDePrecios.cs`'s `BuscarFilaAbiertaAsync` (`:584`):
  add `monto` to its `SELECT` projection — one more column on a statement
  that already runs under the advisory lock, zero new round trips.
  **DEVIATION (registered):** the actual DB column is `precio`, not
  `monto` (`PrecioConfiguration.cs:56` — `monto` is only the JSON payload
  key, per `PayloadDeAuditoria.CambioDePrecio`). Using the literal string
  `monto` in the raw SQL broke all 20 HTTP-level `PreciosEndpointsTests`
  with a 500 (`column "monto" does not exist`) until caught by the filtered
  suite and fixed to `SELECT id_precio, vigente_desde, precio`.
- [x] 2.3 Modify `src/Ways.Application/Usuarios/ServicioDeUsuarios.cs`'s
  `CrearAsync` (`:113-114`): wrap in `CreateExecutionStrategy` +
  `BeginTransactionAsync`; `Add` → `SaveChangesAsync` → `Registrar(
  usuario.alta, ...)` with the now-generated id → `SaveChangesAsync` →
  `CommitAsync`. **The only call site that changes its caller's
  transaction structure** — the id does not exist before the first flush.
  *(design decision 11; call site 2)*
  **DEVIATION (registered):** `Database.BeginTransactionAsync` is not
  supported by the EF Core InMemory provider (same documented caveat as
  `ServicioDeOfertas.CrearAsync`/`ActualizarAsync`/`EliminarAsync`) — the
  one InMemory round-trip test in `ServicioDeUsuariosTests.cs`
  (`ElMismoNombreDeUsuarioEnDosTenantsDistintosConvive`) was removed and
  its business-rule coverage ("mismo nombre en dos tenants distintos
  convive") ported into the new Postgres-backed
  `PreciosYUsuariosAuditoriaTests` — see that file's class doc-comment note.
  **Judgment-day slice 2 (round 1, judge B, MAJOR — closed):** the
  port was originally recorded above as "implicit via `CrearAsync` calls
  succeeding against two different tenants across the slice's other
  tests" — it wasn't: no existing test in that file ever called
  `CrearAsync` with the SAME `NombreUsuario` against two different
  tenants, so the per-tenant scoping in
  `ServicioDeUsuarios.ExigirDisponibilidadAsync` was unfalsifiable
  (mutating it to a global uniqueness check survived all 203 tests).
  Now covered explicitly by
  `PreciosYUsuariosAuditoriaTests.ElMismoNombreDeUsuarioEnDosTenantsDistintosConviveYEnElMismoTenantSeRechaza`
  (same name, two tenants via real `CrearAsync` round-trips: both
  succeed; same name repeated in the same tenant: rejected with
  `usuario_duplicado`/409).
- [x] 2.4 Modify `ServicioDeUsuarios.cs`'s `ActualizarAsync` (`:143`,
  **before** the entity's fields are mutated, while the old values are
  still in memory): `Registrar(usuario.actualizacion, ant={usuario, mail,
  id_rol, estado} pre-mutation, nuevo=same keys post-mutation)`, encolado
  before the `:157` `SaveChangesAsync`. *(call site 3)*
- [x] 2.5 Modify `ServicioDeUsuarios.cs`'s `EliminarAsync` (`:200`):
  `Registrar(usuario.baja, ant={deleted_at: null, estado}, nuevo={
  deleted_at: <momento>, estado})` before the `:202` `SaveChangesAsync` —
  **this exact shape, per Orchestrator Decision #2 above, not the
  proposal's stale `{estado:"eliminado"}`**. *(call site 4)*
  Minor consolidation: `usuario.DeletedAt`/`UpdatedAt` now share ONE
  `var momento = reloj.Ahora;` (instead of two separate `reloj.Ahora`
  reads) so the audit payload's `deleted_at` is byte-identical to the
  persisted column under any clock, not just `RelojFijo`.
  **Judgment-day slice 2 (round 1, judge B, WARNING — closed):** no test
  inspected the payload's KEYS, so reverting the factory to the stale
  `{estado:"eliminado"}` shape survived. Now covered key-by-key by
  `PreciosYUsuariosAuditoriaTests.UsuarioBajaEscribePayloadConDeletedAtYEstadoClavePorClave`
  (asserts exactly `{deleted_at, estado}` on both sides, `anterior.deleted_at
  IS NULL`, `nuevo.deleted_at == RelojFijo` exact).
- [x] 2.6 Modify `ServicioDeUsuarios.cs`'s desbloqueo path (`:188`):
  `Registrar(usuario.desbloqueo, ant={estado:"bloqueado"} real pre-
  Desbloquear, nuevo={estado:"activo"} post)` before the `:189`
  `SaveChangesAsync`. `Desbloquear` still runs even if the account is
  already active, unchanged. *(call site 5)*
- [x] 2.7 Modify `ServicioDeUsuarios.cs`'s password-change path (`:177`):
  `Registrar(usuario.password, ant=NULL, nuevo={por_el_propio_usuario:
  usuario.Id == contexto.UsuarioId})` before the `:178` `SaveChangesAsync`
  — **never** the hash. *(call site 6)*
- [x] 2.8 [P] Integration: `precio.cambio` coverage — exactly one row,
  `{id_lista_precio, monto, vigente_desde}` on both payloads, actor
  identified. Also the first slot where task 1.27's mutation evidence can
  be collected (a mutated literal `id_actor` makes the expected actor not
  appear). *(spec `precios`: "A price change is attributable to its
  actor")* `PreciosYUsuariosAuditoriaTests.PrecioCambioEscribeUnaFilaConPayloadCompletoYActorIdentificado`.
  **Evidence (task 1.27, deferred from slice 1)**: mutated
  `ServicioDeAuditoria.Registrar`'s `IdActor = contexto.UsuarioId` → a
  literal `1` → `dotnet build --no-incremental` → this test FAILED
  (`Expected: 2, Actual: 1` — the real admin id never appeared) → reverted
  → green.
- [x] 2.9 [P] Integration: a price change that replaces a pending row
  (closes the pending row **and** re-closes the predecessor) writes
  **exactly one** `auditoria` row for the operation — not two. *(spec
  `auditoria-de-operaciones`: "A price change that closes a predecessor
  writes exactly one row")*
  `PreciosYUsuariosAuditoriaTests.PrecioCambioQueCierraPredecesorEscribeUnaSolaFila`.
- [x] 2.10 [P] **Mutation target**: `db.Auditoria.Add` moved **after** the
  price transaction's `SaveChangesAsync`/commit — the precios fail-closed
  test (2.11) must fail. *(slice 2 row 1)* **Evidence**: mutated (moved
  `Auditoria.Registrar(...)` call in `AbrirNuevoPrecioAsync` to after
  `SaveChangesAsync`/`CommitAsync`) → `dotnet build --no-incremental` →
  `FallaDeAuditoriaBloqueaElCambioDePrecio` FAILED (no exception thrown —
  the price change committed without ever attempting the broken audit
  insert) → reverted → green.
- [x] 2.11 [P] Integration — fail-closed on precios: forcing the audit
  write to fail (1.28's non-existent-usuario technique) leaves the
  previously vigente row's `vigente_hasta` unchanged and no new `precios`
  row. *(spec `precios`: "An audit failure blocks the price change rather
  than losing attribution"; spec `auditoria-de-operaciones`: "A forced
  audit-insert failure blocks a price change")*
  `PreciosYUsuariosAuditoriaTests.FallaDeAuditoriaBloqueaElCambioDePrecio`
  — asserts `DbUpdateException`/`PostgresException` `23503`/
  `fk_auditoria_actor`, zero `precios` rows, zero `auditoria` rows.
- [x] 2.12 [P] **Mutation target**: `usuario.alta`'s explicit transaction —
  revert to two loose `SaveChangesAsync` calls — the `usuario.alta`
  fail-closed test (2.13) must fail. *(slice 2 row 2)* **Evidence**:
  mutated (removed `CreateExecutionStrategy`/`BeginTransactionAsync`,
  two loose `SaveChangesAsync` calls) → `dotnet build --no-incremental` →
  `FallaDeAuditoriaBloqueaElAltaDeUsuario` FAILED (`Expected: 0, Actual: 1`
  — the usuario row survived the broken second flush) → reverted → green.
- [x] 2.13 [P] Integration: forcing the audit write to fail during
  `usuario.alta` leaves no `usuarios` row created.
  `PreciosYUsuariosAuditoriaTests.FallaDeAuditoriaBloqueaElAltaDeUsuario`.
- [x] 2.14 [P] **Mutation target**: the payload capture in
  `ActualizarAsync` moved **after** the field assignments — the coverage
  test's distinct-values assertion (2.15) must fail (anterior would equal
  nuevo). *(slice 2 row 3)* **Evidence**: mutated (moved the four
  `xAnterior` captures to after the four field assignments) → `dotnet
  build --no-incremental` →
  `UsuarioActualizacionEscribeValoresDistintosPrePostMutacion` FAILED
  (`Expected: "vendedor-original", Actual: "vendedor-nuevo"`) → reverted →
  green.
- [x] 2.15 [P] Integration: `usuario.actualizacion` coverage — both
  payloads carry `{usuario, mail, id_rol, estado}` with genuinely distinct
  pre/post values across all four fields (`mutation-proof-tests` rule 6),
  resolving 2.14's evidence.
  `PreciosYUsuariosAuditoriaTests.UsuarioActualizacionEscribeValoresDistintosPrePostMutacion`.
  **Judgment-day slice 2 (round 1, judge B, WARNING — closed):** the test
  claimed "four fields ALL distinct" while `estado` went Activo→Activo
  (never actually changed) and had no `NotEqual` assertion for it either.
  Fixture now moves `estado` Activo→Bloqueado (a real, supported
  transition) and a `NotEqual` on `estado` was added alongside the other
  three — the comment is true now.
- [x] 2.16 [P] **Mutation target**: `monto` in `BuscarFilaAbiertaAsync`'s
  `SELECT` — remove it / hardcode `0` — 2.8's `valorAnterior.monto`
  assertion must fail. *(slice 2 row 4)*
  **DEVIATION (registered):** 2.8's own test (`PrecioCambioEscribeUnaFilaConPayloadCompletoYActorIdentificado`)
  cannot discriminate this mutation — its ONE price change is the
  articulo's FIRST ever, so `valorAnterior` is `NULL` (no `.monto` to
  assert). A dedicated second test,
  `SegundoCambioDePrecioLlevaElMontoAnteriorReal` (a second price change
  whose `valorAnterior.monto` must equal the first price's real value),
  is the actual discriminator. **Evidence**: mutated (`precio` removed
  from the `SELECT`, `FilaVigente.Monto` hardcoded to `0m`) → `dotnet
  build --no-incremental` → `SegundoCambioDePrecioLlevaElMontoAnteriorReal`
  FAILED (`Expected: 100, Actual: 0`) while
  `PrecioCambioEscribeUnaFilaConPayloadCompletoYActorIdentificado` stayed
  green as predicted (confirming 2.8 alone is not a valid discriminator
  for this target) → reverted → both green.
- [x] 2.17 [P] Integration: `usuario.desbloqueo` coverage — one row, real
  pre-`Desbloquear` `{estado:"bloqueado"}` vs post `{estado:"activo"}`.
  `PreciosYUsuariosAuditoriaTests.UsuarioDesbloqueoEscribeEstadoRealPreYPostDesbloqueo`.
- [x] 2.18 [P] Integration: `usuario.password` coverage — `valor_anterior
  IS NULL`; `valor_nuevo = {por_el_propio_usuario: true}` on a self-change
  and `false` on an Admin-forced reset. *(spec `auditoria-de-operaciones`:
  "valor_anterior is NULL for a pure-fact action")*
  `PreciosYUsuariosAuditoriaTests.UsuarioPasswordEscribeHechoSinHashConValorAnteriorNulo`
  (Theory, both cases).
- [x] 2.19 [P] Integration — denylist real: `usuario.actualizacion` and
  `usuario.password` over an account with a known `hash_password` — the
  **raw** jsonb text read directly from the database contains neither the
  hash nor the substring `password` as a key. *(spec `auditoria-de-
  operaciones`: "No usuarios payload ever contains hash_password")*
  `PreciosYUsuariosAuditoriaTests.NingunPayloadDeUsuariosContieneHashPasswordNiSuSubcadena`.
- [x] 2.20 [P] Integration — límite registrado: editing a platform account
  (`usuarios.id_tenant IS NULL`) writes **zero** `auditoria` rows and the
  operation still returns `200`. *(design "Sujeto sin tenant"; proposal Out
  of Scope)*
  `PreciosYUsuariosAuditoriaTests.EdicionDeCuentaDePlataformaNoEscribeFilaDeAuditoria`
  (direct-service call, no exception thrown ≡ the HTTP 200 the design
  criterion describes).
  **Judgment-day slice 2 (round 1, judge B, WARNING — closed):** the
  platform-subject skip (`if (usuario.IdTenant is int idTenantSujeto)`)
  was only exercised for `ActualizarAsync` — removing it from
  `CambiarPasswordAsync` survived undetected. Now also covered by
  `PreciosYUsuariosAuditoriaTests.PasswordYDesbloqueoDeCuentaDePlataformaNoEscribenFilaDeAuditoria`
  (password + desbloqueo in the same fixture; `EliminarAsync` is
  structurally unreachable for a platform subject — always root, and
  `PoliticaDeRoles.ValidarPuedeIntervenirSobre` rejects both self-baja and
  any root-targeted baja before the audit guard is ever reached).
- [x] 2.21 [P] Integration — reloj: all five `usuario.*` actions and
  `precio.cambio` stamp `creado_el` exactly `RelojFijo(2026-08-
  14T12:00:00Z)`, closing 1.27's cross-slice mutation evidence.
  `PreciosYUsuariosAuditoriaTests.TodasLasSeisAccionesEstampanElRelojFijo`
  — all 6 rows (precio.cambio + 5 usuario.*) asserted equal to the fixed
  clock.
- [x] 2.22 Gate guard: `has-pending-model-changes` clean; zero new files in
  `Migraciones/`. **Confirmed**: `dotnet ef migrations
  has-pending-model-changes --project src/Ways.Infrastructure
  --startup-project src/Ways.Infrastructure` → "No changes have been made
  to the model since the last migration."; `git diff --stat main --
  src/Ways.Infrastructure/Persistencia/Migraciones/` → empty.
- [x] 2.23 Run `judgment-day`; fix confirmed issues; re-judge until clean.
  *(orchestrator)* **Round 1, judge B**: 1 MAJOR (2.3's cross-tenant
  `usuario` uniqueness coverage was claimed "implicit" but didn't exist —
  now explicit, see 2.3's note) + 4 WARNINGs closed (2.1's misreported
  call-site count, 2.5's `usuario.baja` payload key-by-key coverage,
  2.18/2.20's platform-subject skip on `CambiarPasswordAsync`, 2.15's
  Activo→Activo non-change). Authorized suggestion applied: `CrearAsync`'s
  `new Usuario {...}` moved inside the `ExecutionStrategy` retry lambda,
  matching `ServicioDePrecios.AbrirNuevoPrecioAsync`/
  `ServicioDeAprovisionamiento`'s pattern of building retry-scoped entities
  inside the lambda — all existing tests stayed green with no semantic
  adjustment. **Round 2, judge A**: WARNING — round 1's suggestion
  REVERTED. Building `usuario` inside the `ExecutionStrategy` lambda risks a
  double INSERT on a transient retry: the `ChangeTracker` still holds the
  entity from the failed attempt, the retry builds and `Add`s a second fresh
  instance, and `SaveChangesAsync` persists both. Moved the construction
  back outside the lambda (pre-9f015b2 shape) so a retry re-`Add`s the SAME
  instance — idempotent, single row inserted, explicit id valid because the
  identity column is `GENERATED BY DEFAULT`. The same
  `ExecutionStrategy`+tracker interplay exists pre-existing in
  `ServicioDePrecios` — out of scope for this slice, known residual.
  SUGGESTION also closed: class doc-comment now clarifies the
  platform-subject skip guard is structurally unreachable in `CrearAsync`
  (`PoliticaDeRoles` rejects Root from the app; `tenant_requerido` covers
  the rest), same treatment `EliminarAsync` already documents in
  `PreciosYUsuariosAuditoriaTests`. Verified:
  `dotnet build --no-incremental` clean; `dotnet test --filter
  "FullyQualifiedName~Precios|FullyQualifiedName~Usuarios|FullyQualifiedName~Auditoria"`
  → 207/207, all pre-existing tests unchanged (fail-closed alta and the rest
  stayed green).
- [x] 2.24 Branch `feat/stage14-slice2-precios-usuarios` off `main` *(CLEAN 2026-08-16: juez B ronda 1 — 1 MAJOR (scoping por-tenant del username infalsificable tras el retiro del test InMemory; cerrado con el test cross-tenant de actor PLATAFORMA — el filtro EF+RLS enmascara el predicado para actores tenant-scoped) + 4 WARNINGs cerrados + sugerencia aplicada; re-ronda B aprobada con 5 re-mutantes muertos; juez A ronda 2: 0 severos, 1 WARNING inferencial (doble-INSERT en retry por la construccion dentro del lambda — RESUELTO REVIRTIENDO la sugerencia al estado que B reviso en ronda 1, con el residuo del interplay ExecutionStrategy+tracker registrado como pre-existente en ServicioDePrecios) + 1 SUGGESTION de doc cerrada. JUDGMENT: APPROVED.)*
  (parent: slice 1); PR; merge stacked-to-main. *(orchestrator)*

**Test plan**: coverage ×6 (2.8, 2.9, 2.15, 2.17, 2.18) — actually 7 Facts/
Theories once `SegundoCambioDePrecioLlevaElMontoAnteriorReal` (2.16's real
discriminator) and the alta-coverage assertions inside 2.21's test are
counted — 4 mutation targets (2.10, 2.12, 2.14, 2.16), fail-closed ×2
(2.11, 2.13), denylist (2.19), platform-account limit (2.20), fixed clock
(2.21). All in `tests/Ways.IntegrationTests/PreciosYUsuariosAuditoriaTests.cs`.

**Verify**: the literal filter string below is DOCUMENTARY — no test class
in this codebase is literally named `ServicioDePrecios*`/`ServicioDeUsuarios*`
(they're `PreciosEndpointsTests`, `UsuariosYLoginTests`,
`PreciosYUsuariosAuditoriaTests`, etc.), so it matches zero tests verbatim.
Functional equivalent run and GREEN: `dotnet test
--filter "FullyQualifiedName~Precios|FullyQualifiedName~Usuarios|FullyQualifiedName~Auditoria"`
→ 75/75. Original text preserved for traceability:
`dotnet test --filter FullyQualifiedName~ServicioDePrecios|FullyQualifiedName~ServicioDeUsuarios`

---

## Slice 3: Anulaciones (PR 3)

**Start**: slice 1 merged (parallel with 2 and 4). **Finish**:
`venta.anulacion` and `compra.anulacion` each write exactly one `auditoria`
row in the same ADO transaction as the `estado` transition, including the
100%-servicio-sin-CC comprobante. **Rollback**: revert the branch — the
call sites disappear, both `MarcarAnulado*Async` methods keep their
`RETURNING`-derived PV (harmless if unused).

- [x] 3.1 Modify `src/Ways.Application/Ventas/ServicioDeVentas.cs`:
  `MarcarAnuladoAsync` → `RETURNING id_punto_venta` / return `int?`
  (instead of `bool` via `RETURNING id_comprobante_venta`) — the same
  `UPDATE ... WHERE estado = 'emitido'` stays the sole race-safe authority,
  now also answering "in which PV", zero extra round trips. *(design
  decision 8; call site 7)*
- [x] 3.2 Modify `ServicioDeVentas.cs`'s `EjecutarAnulacionAsync` (`:541`,
  after the `!seAnulo` guard, before paso 2):
  `auditoria.RegistrarAsync(conexion, transaccionCruda, ...)` with
  `accion=venta.anulacion`, `ant={estado: EstadoComprobante.Emitido}` (the
  **same constant** that binds the `UPDATE`'s `WHERE`), `nuevo={estado:
  EstadoComprobante.Anulado}`.
  **DEVIATION (registered, not silent)**: `auditoria` is **not** a new
  constructor dependency of `ServicioDeVentas`/`ServicioDeCompras` — design's
  own call-site table never lists a constructor change for call sites 7/8,
  and design binding verify criterion 2 restricts this etapa's diff of
  `src/Ways.Application/Ventas/` to the lines of
  `EjecutarAnulacionAsync`/`MarcarAnuladoAsync`. `VentasCheckoutTests.cs` and
  4 other integration test files construct `ServicioDeVentas`/`ServicioDeCompras`
  with `new(...)` and the CURRENT positional arg count (confirmed by
  `grep`: `PlanDeVentaFefoTests`, `VentasAtomicidadYConcurrenciaTests`,
  `VentaEscrituraLoteTests`, `VentasTurnoWiringTests`,
  `VentasCheckoutTests`, and `ComprasAnulacionYConcurrenciaTests.CrearServicio`)
  — a constructor parameter would have broken all of them, including the
  one file that must stay byte-identical. Instead,
  `new ServicioDeAuditoria(db, reloj, contexto)` is instantiated LOCAL to
  each `EjecutarAnulacionAsync`, from the same `db`/`reloj`/`contexto`
  already captured by each service's own primary constructor — zero DI
  surface change, zero touched constructor, zero touched test file. For
  `ServicioDeVentas.cs` specifically, fully-qualified names
  (`Ways.Application.Auditoria.ServicioDeAuditoria`,
  `Ways.Domain.Auditoria.*`) are used inline instead of adding `using`
  directives, to keep the diff confined to the two named methods per
  binding verify criterion 2's literal text.
- [x] 3.3 Modify `src/Ways.Application/Compras/ServicioDeCompras.cs`'s
  anulación path (`:522`, after the guard, before paso 2):
  `RegistrarAsync` with `accion=compra.anulacion`, `ant={estado:
  "confirmada"}` (guaranteed by the `UPDATE`'s `WHERE`, `:677`),
  `nuevo={estado:"anulada"}`; `id_punto_venta` from the `RETURNING`
  `MarcarAnuladaAsync` **already** returns — no change to that method.
  Compras is not named by binding verify criterion 2, so ordinary `using`
  directives (`Ways.Application.Auditoria`, `Ways.Domain.Auditoria`) were
  added — same local-instantiation pattern as 3.2 otherwise.
- [x] 3.4 [P] **Mutation target**: `RETURNING id_punto_venta` on
  `MarcarAnuladoAsync` — revert to `id_comprobante_venta` and read the PV
  from the pre-read instead — a test whose pre-read PV disagrees with the
  row's actual PV must catch the audit row's `id_punto_venta` diverging.
  *(slice 3 row 1)* **Evidence**: a same-thread, no-race test cannot
  discriminate here — the PV is immutable post-emission, so the pre-read
  and the `RETURNING` always agree without a real interleaving (same
  confound flagged in slice 1 task 1.18). Test
  `AuditoriaAnulacionVentaTests.LaAuditoriaDeAnulacionLlevaElPuntoDeVentaQueElUpdateAtomicoRealmenteVioNoElDelPreRead`
  forces a genuine race via a `DbCommandInterceptor` pausing
  `EjecutarAnulacionAsync` right after the pre-read SELECT executes; from a
  separate owner connection (outside the app transaction) `id_punto_venta`
  is reassigned to a second, freshly-seeded PV; the atomic `UPDATE ...
  RETURNING` then sees and returns the reassigned PV. Mutated (audit call
  reads `comprobantePreLectura!.IdPuntoVenta` instead of the `RETURNING`
  value) → `dotnet build --no-incremental` → named test →
  `Assert.Equal() Failure: Values differ / Expected: 4 / Actual: 3` (audit
  row carried the stale pre-read PV) → reverted → `git diff` clean →
  rebuilt → green.
- [x] 3.5 [P] **Mutation target**: `RegistrarAsync` moved **after**
  `CommitAsync` in the anulación transaction — the flagship fail-closed
  test (3.9) must fail. *(slice 3 row 2)* **Evidence**: mutated (the whole
  "1.5. Auditoría" block cut from before paso 2 and pasted after
  `transaccion.CommitAsync(ct)`, immediately before `return await
  ObtenerAsync(...)`) → `dotnet build --no-incremental` → named test
  `AuditoriaAnulacionVentaTests.UnaFallaAlEscribirLaAuditoriaBloqueaLaAnulacionDelComprobante100PorCientoServicio`
  → `Assert.Equal() Failure: Values differ / Expected: Emitido / Actual:
  Anulado` (the 100%-servicio comprobante committed `Anulado` even with
  `INSERT` on `auditoria` revoked, because the commit had already happened
  before the now-doomed audit insert ran) → reverted via `git checkout --`
  → `git diff` clean → rebuilt → green.
- [x] 3.6 [P] **Mutation target**: `EstadoComprobante.Emitido` as
  `valor_anterior` replaced by a hardcoded `"anulado"` literal — the
  `venta.anulacion` payload coverage test (3.8) must fail. *(slice 3 row 3)*
  **Evidence**: mutated (`PayloadDeAuditoria.AnulacionDeVenta`'s first
  argument changed from `EstadoComprobante.Emitido` to
  `EstadoComprobante.Anulado`, the enum member that serializes to the
  `"anulado"` literal per `SerializadorDeAuditoria`'s
  `JsonStringEnumConverter(SnakeCaseLower)`) → `dotnet build
  --no-incremental` → named test
  `AuditoriaAnulacionVentaTests.VentaAnulacionCoberturaSobreUnComprobanteConConsumoDeCuentaCorriente`
  → `Assert.Equal() Failure: Strings differ / Expected: "emitido" / Actual:
  "anulado"` → reverted → `git diff` clean → rebuilt → green.
- [x] 3.7 [P] Integration — **the flagship scenario**: a TX comprobante
  composed entirely of service lines (`id_articulo NULL` on every item)
  with no cuenta corriente pago, anulado — one `auditoria` row naming the
  actor, zero `movimientos_stock`, zero `movimientos_cuenta_corriente`
  reversal rows. *(spec `comprobantes-venta`: "A 100%-servicio comprobante
  without cuenta corriente is attributable on anulación"; spec `auditoria-
  de-operaciones`: "A 100%-servicio anulación without cuenta corriente is
  attributable")* Implemented as
  `AuditoriaAnulacionVentaTests.AnulacionDeUnComprobante100PorCientoServicioSinCcEsAtribuible`.
  **Note**: `POST /api/ventas` cannot construct a free-concept
  (`id_articulo NULL`) line — `LineaDeVenta.IdArticulo` is non-nullable
  `int` (`Ventas/Contratos.cs`) and the checkout path "no construye ese
  camino todavía" per `ItemComprobanteVenta.IdArticulo`'s own doc-comment
  — so the comprobante + its free-concept item are seeded directly by EF
  (`SembrarComprobanteDeServicioAsync`), same precedent as
  `CajaCierreEndpointsTests.SembrarPagoAsync`, then anulado through the
  real `POST /api/ventas/{id}/anulacion` endpoint.
- [x] 3.8 [P] Integration: `venta.anulacion` coverage over a mixed
  comprobante (product + service lines, with CC consumo) — one row,
  `{estado: Emitido}` → `{estado: Anulado}`, `id_punto_venta` matches the
  comprobante's own PV. Implemented as
  `AuditoriaAnulacionVentaTests.VentaAnulacionCoberturaSobreUnComprobanteConConsumoDeCuentaCorriente`.
  **DEVIATION (registered, not silent)**: since the checkout endpoint
  structurally cannot emit a free-concept line (see 3.7's note), "mixed"
  composition is covered by the flagship test's directly-seeded
  100%-servicio comprobante instead; this task's own coverage test uses an
  ordinary product line paid by cuenta corriente (real checkout path,
  `EmitirConCcAsync`) to exercise the payload/PV assertions over a
  comprobante that DOES produce ledger reversal rows — the generality axis
  this task actually needed (a non-degenerate case, as opposed to 3.7's
  degenerate one), rather than a literal product+service item mix.
- [x] 3.9 [P] Integration — fail-closed on `venta.anulacion`: forcing the
  audit write to fail leaves `estado = emitido` and no inverse
  `movimientos_stock`/`movimientos_cuenta_corriente` row. *(spec
  `comprobantes-venta`: "An audit failure blocks the anulación"; spec
  `auditoria-de-operaciones`: "A forced audit-insert failure blocks a venta
  anulación")* Implemented as
  `AuditoriaAnulacionVentaTests.UnaFallaAlEscribirLaAuditoriaBloqueaLaAnulacionDelComprobante100PorCientoServicio`
  over the SAME 100%-servicio-sin-CC comprobante as 3.7 — design's own
  "test insignia (b)": the audit `INSERT` is the ONLY statement in that
  transaction touching `usuarios` (via `fk_auditoria_actor`), so `REVOKE
  INSERT ON auditoria` isolates the failure without ambiguity (same
  `REVOKE`/`RESTORE` technique as `AnulacionTests`/
  `ComprasAnulacionYConcurrenciaTests`). Also doubles as 3.5's fail-closed
  evidence, per that task's own text.
  **Judgment Day fix (slice 3 juez B ronda 1, finding 1, WARNING):** the
  100%-servicio-sin-CC comprobante this task's test runs on never produces
  reversas under any implementation, so the spec's THEN ("no inverse
  movimientos_stock/movimientos_cuenta_corriente row exists") was vacuously
  true there — it asserted nothing about the mechanism it claims to guard.
  Complemented (not replaced) with
  `UnaFallaAlEscribirLaAuditoriaBloqueaLaAnulacionDeUnComprobanteConTresLineasDeProductoYConsumoDeCc`,
  the spec's literal GIVEN ("3 líneas de producto y un consumo de cuenta
  corriente"), with distinct per-line magnitudes so the CEROs it asserts
  are real. Evidence: committed → mutated (the "1.5. Auditoría" block moved
  after `transaccion.CommitAsync(ct)`) → `dotnet build --no-incremental` →
  new test FAILED (reversas existían / estado quedó `Anulado`) → reverted →
  `git diff` clean → rebuilt → green.
- [x] 3.10 [P] Integration: `compra.anulacion` coverage — a confirmada
  compra of 50 units, none sold, anulada — one row, actor identified, same
  transaction as the `-50` `movimientos_stock` row. *(spec `comprobantes-
  compra`: "A compra anulación is attributable to its actor")* Implemented
  as `AuditoriaAnulacionCompraTests.CompraAnulacionCoberturaSobreUnaCompraConfirmadaSinVender`.
- [x] 3.11 [P] Integration — fail-closed on `compra.anulacion`: `estado`
  remains `confirmada`, no `movimientos_stock` contramovimiento. *(spec
  `comprobantes-compra`: "An audit failure blocks the anulación, same as
  the negative-stock refusal")* Implemented as
  `AuditoriaAnulacionCompraTests.UnaFallaAlEscribirLaAuditoriaBloqueaLaCompraAnulacion`
  (`REVOKE INSERT ON auditoria`, same technique as 3.9).
- [x] 3.12 **Binding verify criterion, not a mutation target**:
  `tests/Ways.IntegrationTests/VentasCheckoutTests.cs` is **absent from the
  stage's diff entirely** (`git diff --name-only` against the stage's base
  never lists this file), and its `Assert.Equal(16, …)` query-count guard
  runs unedited. This slice is the only one that opens
  `ServicioDeVentas.cs`, so it is asserted here. *(spec `auditoria-de-
  operaciones`: "Checkout emission writes no audit row and the query-count
  guard stays at 16"; design binding verify criterion 2; Orchestrator
  Decision 13 above)* **Confirmed**:
  `git diff --name-only main | grep -i VentasCheckout` → no match, on the
  final committed state (`ba9b8d5`). `EmitirAsync`/checkout is never
  touched by this slice — only `EjecutarAnulacionAsync`/`MarcarAnuladoAsync`
  in `ServicioDeVentas.cs`, per 3.2's DEVIATION note above.
- [x] 3.13 Gate guard: `has-pending-model-changes` clean; zero new files in
  `Migraciones/`. **Confirmed**: `dotnet ef migrations
  has-pending-model-changes --project src/Ways.Infrastructure
  --startup-project src/Ways.Infrastructure` → "No changes have been made
  to the model since the last migration."; `git diff --stat main --
  src/Ways.Infrastructure/Persistencia/Migraciones/` → empty.
- [x] 3.14 Run `judgment-day`; fix confirmed issues; re-judge until clean.
  - Ronda 1 (juez B): 0 severos; 2 WARNING (findings 1-2). Ambos fixed y con
    evidencia de mutación: finding 1 (test fail-closed nuevo sobre la
    composición literal del spec — 3 líneas de producto + consumo de CC —
    complementando, no reemplazando, el flagship 100%-servicio, task 3.9);
    finding 2 (`Assert.NotEqual(0, fila.IdActor)` → `Assert.Equal(ctx.
    IdEmpleadoAdmin, fila.IdActor)` en ambos tests de cobertura, venta y
    compra). Re-judge pendiente.
- [x] 3.15 Branch `feat/stage14-slice3-anulaciones` off `main` (parent: *(CLEAN 2026-08-16: juez B ronda 1 — 0 severos, 2 WARNINGs test-only cerrados (fail-closed con la composicion literal del spec 3 lineas+CC asertando cero reversas; IdActor por IGUALDAD con el admin — el mutante actor-constante ahora muere en ambos tests); re-ronda B aprobada (patch-id verificado, REVOKE con restore en finally, retry limpio 200) con 1 INFO de cifras de comentario corregido por el orquestador; juez A fresh: CERO hallazgos — verifico el precedente MarcarCerradoAsync, la carrera cross-connection del interceptor como patron establecido del repo, y que MarcarAnulad(o|a)Async no tiene callers externos. JUDGMENT: APPROVED.)*
  slice 1); PR; merge stacked-to-main.

**Test plan**: 3 mutation targets (3.4-3.6), flagship (3.7), coverage
(3.8, 3.10), fail-closed ×2 (3.9, 3.11), checkout non-regression (3.12).

**Verify**: `dotnet test --filter FullyQualifiedName~ServicioDeVentas|FullyQualifiedName~ServicioDeCompras`

---

## Slice 4: Stock + Cuenta Corriente (PR 4)

**Start**: slice 1 merged (parallel with 2 and 3). **Finish**:
`stock.ajuste`/`stock.decomiso`/`stock.conteo` and `cc.reliquidacion` each
write `auditoria` rows; `stock.conteo` writes **one row per counting
operation**, per Orchestrator Decision #1 above — never one per lote.
**Rollback**: revert the branch — the call sites disappear;
`InsertarMovimientoStockAsync`'s `Task<int>` signature can stay (harmless,
`TransferirAsync` ignores the value) or revert with it.

- [x] 4.1 Modify `src/Ways.Application/Stock/ServicioDeStock.cs`:
  `InsertarMovimientoStockAsync` → `RETURNING id_movimiento_stock` /
  `ExecuteScalarAsync`, returning `int`. `TransferirAsync` ignores the
  returned value and stays byte-identical in behavior — still writes zero
  `auditoria` rows (proposal decision 5). *(Orchestrator Decision 15 above;
  design Open Questions)*
  **DEVIATION (registered, not silent):** the `RETURNING` clause reads
  `id_movimiento`, not `id_movimiento_stock` — `movimientos_stock`'s actual
  primary-key column is `id_movimiento` (confirmed against the etapa-5
  migration and `MovimientoStock.Id`), not the name tasks.md/design.md use
  for the SQL identifier. The payload dictionary KEY stays literally
  `id_movimiento_stock` in every `PayloadDeAuditoria` factory (that name is
  the audit contract's own field name, independent of the DB column) — only
  the `RETURNING` SQL identifier differs from the task's prose. Verified
  empirically: the literal task text fails with `column "id_movimiento_stock"
  does not exist` against real Postgres.
- [x] 4.2 Modify `ServicioDeStock.cs`'s ajuste path (after the
  aggregate **and** lote upserts, before commit): `RegistrarAsync(
  stock.ajuste, ant={cantidad: nueva − delta}, nuevo={cantidad: nueva,
  id_movimiento_stock, observaciones})`.
- [x] 4.3 Modify `ServicioDeStock.cs`'s decomiso path (**after**
  both negative-stock refusals): `RegistrarAsync(stock.decomiso,
  ant={cantidad}, nuevo={cantidad, id_movimiento_stock, observaciones,
  id_lote})` — `id_lote` null = no lote-efectivo.
- [x] 4.4 Modify `ServicioDeStock.cs`'s conteo paths
  (`EjecutarConteoAsync` and `EjecutarConteoPorLoteAsync`): **per
  Orchestrator Decision #1 above** — accumulate `movimientos_generados`
  (the `id_movimiento_stock` list), `lotes_afectados` (count), and
  `delta_total` across the existing loop over lotes/agregado; write
  **exactly one** `RegistrarAsync(stock.conteo, ant={cantidad: cantidad al
  inicio}, nuevo={cantidad: cantidad final, movimientos_generados,
  lotes_afectados, delta_total})` **after** the loop, per counting
  operation. The existing zero-difference early return is
  untouched and now unambiguously produces zero ledger **and** zero audit
  rows for the whole operation. The single-lote/aggregate path
  (`EjecutarConteoAsync`) uses the same `PayloadDeAuditoria.Conteo` factory
  with `lotesAfectados: 0` (the aggregate path never touches a lote) and a
  one-element `movimientos_generados`.
- [x] 4.5 Modify
  `src/Ways.Application/CuentaCorriente/ServicioDeReliquidacion.cs`
  (after the marker + rowcount check, before commit):
  `RegistrarAsync(cc.reliquidacion, ant={saldo} from the SELECT …
  FOR UPDATE, nuevo={saldo: nuevoSaldo, id_movimiento, consumos_actualizados:
  |ids|, diferencia: delta})`. `BloquearClienteAsync`'s previously-discarded
  `Saldo` tuple element is now captured as `saldoInicial`.
- [x] 4.6 [P] **Mutation target**: `cantidad − delta` as before-image — use
  `nueva` on both sides — the `stock.ajuste` coverage test (4.9) must fail.
  *(slice 4 row 1)* **Evidence**: mutated (`nuevaCantidad, nuevaCantidad`
  on both sides) → `dotnet build --no-incremental` → `dotnet test
  tests/Ways.IntegrationTests --no-build --filter FullyQualifiedName~Ajuste`
  → `StockAuditoriaTests.UnAjusteDeStockEscribeUnaFilaDeAuditoriaConAnteriorDistintoDeNuevo`
  FAILED (`Assert.Equal` on `anterior.cantidad`: expected 50, actual 70 —
  the mutated before-image collapsed to the after-image) → reverted →
  44/44 green.
- [x] 4.7 [P] **Mutation target**: the delta-cero early return
  (`ServicioDeStock.cs`, `EjecutarConteoAsync`) — remove it — the
  zero-difference conteo test (4.10) must fail. *(slice 4 row 2)*
  **Evidence**: mutated (early-return block deleted) → `dotnet build
  --no-incremental` → `dotnet test tests/Ways.IntegrationTests --no-build
  --filter FullyQualifiedName~Conteo` →
  `StockAuditoriaTests.UnConteoAgregadoSinDiferenciaNoEscribeFilaDeAuditoria`
  FAILED (`400 movimiento_de_stock_sin_cantidad` — the mutated path reached
  `InsertarMovimientoStockAsync` with `cantidad = 0`, tripping
  `ck_movimientos_stock_cantidad_no_cero`), together with two pre-existing
  zero-difference tests
  (`TransferenciasYConteoDeInventarioTests.UnConteoQueCoincideConElCacheNoEscribeNadaYDevuelve200`,
  `ConteoPorLoteTests.UnConteoAgregadoDeContadaIgualALaActualSigueSinEscribirNada`)
  → reverted → 51/51 green.
- [x] 4.8 [P] **Mutation target**: the `saldo` before-image taken from the
  `FOR UPDATE` — re-read it **after** the `UPDATE` instead — the
  `cc.reliquidacion` coverage test (4.13) must fail. *(slice 4 row 3)*
  **Evidence**: mutated (`ReliquidacionDeCc(nuevoSaldo, nuevoSaldo, …)`,
  simulating a post-`UPDATE` re-read) → `dotnet build --no-incremental` →
  `dotnet test tests/Ways.IntegrationTests --no-build --filter
  FullyQualifiedName~Reliquidacion` →
  `ReliquidacionAuditoriaTests.UnaReliquidacionConDiferenciaEscribeUnaFilaDeAuditoriaConSaldoAnteriorDistintoDeNuevo`
  FAILED (`Assert.Equal` on `anterior.saldo`: expected 100, actual 150 —
  the mutated before-image collapsed to the after-image) → reverted →
  25/25 green.
- [x] 4.9 [P] Integration: `stock.ajuste` coverage — one row, `{cantidad:
  anterior}` ≠ `{cantidad: nuevo}` with delta ≠ 0, `id_movimiento_stock`
  and `observaciones` present, resolving 4.6's evidence. Implemented as
  `StockAuditoriaTests.UnAjusteDeStockEscribeUnaFilaDeAuditoriaConAnteriorDistintoDeNuevo`.
- [x] 4.10 [P] Integration: `stock.conteo`, zero-difference — zero
  `movimientos_stock` rows **and** zero `auditoria` rows, resolving 4.7's
  evidence. *(spec `auditoria-de-operaciones`: "A zero-difference conteo
  writes no audit row")* Implemented as
  `StockAuditoriaTests.UnConteoAgregadoSinDiferenciaNoEscribeFilaDeAuditoria`.
- [x] 4.11 [P] Integration — **the reconciled scenario** (Orchestrator
  Decision #1): a conteo por lote over one articulo with 3 lotes, 2
  differing, writes **exactly one** `auditoria` row for the whole
  operation — not two — with `valor_nuevo.movimientos_generados` naming
  both `id_movimiento_stock` values and `lotes_afectados = 2`. This is the
  discriminating test that replaces design.md's stale per-lote call-site
  text. *(spec `auditoria-de-operaciones`: "Each operation MUST write
  exactly one row")* Implemented as
  `StockAuditoriaTests.UnConteoPorLoteConDosDeTresLotesDiferentesEscribeUnaSolaFilaDeAuditoria`
  (L1 10→15 and L3 5→3 differ, L2 20→20 matches; asserts exactly one
  `auditoria` row, `lotes_afectados = 2`, and `movimientos_generados`
  naming both real ledger row ids).
- [x] 4.12 [P] Integration: `stock.decomiso` coverage — one row, `id_lote`
  present on a lote-efectivo decomiso and `NULL` on an aggregate-only one;
  both `409 stock_insuficiente_para_decomiso` refusal paths leave zero
  rows. Implemented as four `StockAuditoriaTests` facts:
  `UnDecomisoDeStockEscribeUnaFilaConIdLotePresenteCuandoEsLoteEfectivo`,
  `UnDecomisoDeStockEscribeUnaFilaConIdLoteNuloCuandoNoEsLoteEfectivo`,
  `UnDecomisoRechazadoPorStockInsuficienteEnElLoteNoEscribeFilaDeAuditoria`,
  `UnDecomisoRechazadoPorStockInsuficienteEnElAgregadoNoEscribeFilaDeAuditoria`.
- [x] 4.13 [P] Integration: `cc.reliquidacion` coverage — one row, `saldo`
  anterior ≠ nuevo with a known `diferencia`, `consumos_actualizados`
  matches the seeded consumos; the two no-op paths (sin elegibles, delta
  cero) commit without any ledger or audit row, resolving 4.8's evidence.
  Implemented as three `ReliquidacionAuditoriaTests` facts:
  `UnaReliquidacionConDiferenciaEscribeUnaFilaDeAuditoriaConSaldoAnteriorDistintoDeNuevo`,
  `UnaReliquidacionSinConsumosElegiblesNoEscribeFilaDeAuditoria`,
  `UnaReliquidacionConDeltaCeroNoEscribeFilaDeAuditoria` (a consumo whose
  price never changed — eligible, but a zero-delta re-pricing, distinct
  from the "sin elegibles" no-op).
- [x] 4.14 [P] Integration — límite registrado: `TransferirAsync` writes
  **zero** `auditoria` rows for either leg, and both `movimientos_stock`
  legs still carry their own `id_empleado`. *(spec `auditoria-de-
  operaciones`: "stock.transferencia is excluded by scope, not by defect")*
  Implemented as
  `StockAuditoriaTests.UnaTransferenciaNoEscribeFilasDeAuditoriaParaNingunaDeLasDosPatas`.
- [x] 4.15 Gate guard: `has-pending-model-changes` clean; zero new files in
  `Migraciones/`. **Confirmed**: `dotnet ef migrations
  has-pending-model-changes --project src/Ways.Infrastructure
  --startup-project src/Ways.Infrastructure` → "No changes have been made
  to the model since the last migration."; `git diff --stat main --
  src/Ways.Infrastructure/Persistencia/Migraciones/` and `git status
  --short` on that directory both empty.
- [x] 4.16 Run `judgment-day`; fix confirmed issues; re-judge until clean.
  - Ronda 1 (juez B): 2 MAJOR cerrados con evidencia de mutación. Finding 1:
    el conteo agregado con diferencia (`EjecutarConteoAsync`, delta ≠ 0) no
    tenía ningún test — cubierto por
    `StockAuditoriaTests.UnConteoAgregadoConDiferenciaEscribeUnaFilaDeAuditoriaConPayloadCompleto`
    (payload clave por clave + actor por igualdad). Finding 2: el test de
    ajuste no discriminaba `id_entidad` (coincidencia accidental con
    `id_movimiento_stock` por alineación de secuencias en el entorno) —
    desincronizado quemando filas descartables antes de sembrar la entidad
    real (`QuemarArticulosDescartablesAsync`/`QuemarClientesDescartablesAsync`),
    aplicado también a los hermanos `UnConteoPorLoteConDosDeTresLotesDiferentesEscribeUnaSolaFilaDeAuditoria`
    y `ReliquidacionAuditoriaTests.UnaReliquidacionConDiferenciaEscribeUnaFilaDeAuditoriaConSaldoAnteriorDistintoDeNuevo`.
    La sugerencia equivalente para `stock.decomiso` quedó registrada como
    survivor equivalente por rollback (mutar su call site no cambia el
    resultado observable) — sin fix.
  - Ronda 1 (juez A): 0 severos; 1 WARNING cerrado test-only. Los dos tests
    de cobertura de `stock.decomiso` (`StockAuditoriaTests.cs:260-330`)
    assertaban solo `cantidad`/`id_lote` y nunca `id_movimiento_stock` ni
    `observaciones`, aunque `PayloadDeAuditoria.DecomisoDeStock` escribe las
    4 claves — agregados los asserts de `observaciones` (igualdad con la
    observación seedeada) e `id_movimiento_stock` (round-trip contra la fila
    real de `movimientos_stock` del decomiso), mismo patrón del test de
    ajuste del mismo archivo. Evidencia de mutación: call site de decomiso
    en `ServicioDeStock.cs` mutado a `observaciones=""`/`idMovimientoStock=0`
    → los 2 tests fallaron → revert → 23/23 verdes.
- [x] 4.17 Branch `feat/stage14-slice4-stock-cc` off `main` (parent: *(CLEAN 2026-08-16: juez B ronda 1 — 2 MAJORs cerrados (cobertura del conteo agregado clave-por-clave; id_entidad desincronizado de secuencias coincidentes con quemadores de filas) + survivor equivalente del decomiso registrado; re-ronda B aprobada con 4 re-mutantes muertos incl. el survivor de ronda 1 y el probe de reliquidacion; juez A fresh: 0 severos, 1 WARNING (payload de decomiso 2-de-4 claves) cerrado test-only en cf2a474 con evidencia. JUDGMENT: APPROVED.)*
  slice 1); PR; merge stacked-to-main.

**Test plan**: 3 mutation targets (4.6-4.8), coverage ×3 (4.9, 4.12, 4.13),
zero-difference (4.10), the reconciled one-row-per-operation scenario
(4.11), transferencia limit (4.14).

**Verify**: `dotnet test --filter FullyQualifiedName~ServicioDeStock|FullyQualifiedName~ServicioDeReliquidacion`

---

## Slice 5: Consulta (PR 5)

**Start**: slice 1 merged. **Finish**: `LecturaDeAuditoria` (Admin-only)
gates `GET /api/auditoria`, filtered by the 7 filters and paginated.
**Rollback**: revert the branch — a pure read surface, no write path
depends on it.

**Budget note (design.md)**: pre-authorized split if this slice overflows
— `5a` (policy + route with date/`accion`/actor filters + 403s) and `5b`
(`entidad`/`idEntidad`/PV filters + pagination).

- [x] 5.1 Modify `src/Ways.Api/Seguridad/Politicas.cs`: `const string
  LecturaDeAuditoria = "lectura_auditoria"`; `.AddPolicy(LecturaDeAuditoria,
  p => p.RequireAuthenticatedUser().RequireClaim(ClaimsWays.RolId,
  ((int)RolConocido.Admin).ToString()))` — the exact shape of
  `LecturaDeRentabilidad`, **not** stacked over `LecturaDeReportes`.
  *(design decision 6)* Covered at the policy-composition level too:
  `PoliticasTests.LecturaDeAuditoriaAdmiteSoloAdmin` (Theory, 4 roles, no
  Docker — same shape as `LecturaDeRentabilidadAdmiteSoloAdmin`).
- [x] 5.2 Create `src/Ways.Application/Auditoria/Contratos.cs`:
  `FiltrosDeAuditoria(Desde, Hasta, Accion, IdActor, Entidad, IdEntidad,
  IdPuntoVenta)`, `FilaDeAuditoria(IdAuditoria, CreadoEl, Accion, Entidad,
  IdEntidad, IdActor, Actor, IdPuntoVenta, ValorAnterior, ValorNuevo)`,
  `PaginaDeAuditoria(Items, Total, Pagina, Tamanio)`. `dto-contract-
  honesty`: doc-comment each of the 7 filters — read exclusively inside
  `ConstruirQuery`, no field accepted and silently discarded; doc-comment
  `Actor == null` as "not visible to this session", never "no actor"
  (`IdActor` always travels).
- [x] 5.3 Create
  `src/Ways.Application/Auditoria/ServicioDeConsultaDeAuditoria.cs`:
  `ConstruirQuery(filtros)` — `LEFT JOIN` to `db.Usuarios.IgnoreQueryFilters(
  ["BajaLogica"])` via `DefaultIfEmpty()`, `orderby CreadoEl descending,
  IdAuditoria descending`, one `if (filtro is { } x)` clause per filter;
  `ConsultarAsync(f, pagina, tamanio, ct)` = `CountAsync` +
  `Skip((pagina-1)*tamanio).Take(tamanio)` with `pagina = Math.Max(pagina,
  1)`, `tamanio = Math.Clamp(tamanio, 1, 200)`; `idEntidad` without
  `entidad` → `400 entidad_requerida`.
  **DEVIATION (registered):** the entity's real PK property is `Id`
  (`Auditoria.cs`, `AuditoriaConfiguration.cs` — slice 1), not the
  `IdAuditoria` design/task prose used before the entity landed — the
  tiebreaker is `orderby ... a.Id descending`, same underlying column
  (`id_auditoria`). `idEntidad`-without-`entidad` validation lives at the
  TOP of `ConstruirQuery` (not a separate step in `ConsultarAsync`) so
  slice 6's `ConsultarParaExportacionAsync` — not declared yet — inherits
  it automatically by reusing the same private method, per design decision
  13 ("one `ConstruirQuery`, two consumers").
- [x] 5.4 Create `src/Ways.Api/Endpoints/AuditoriaEndpoints.cs`: `GET
  /api/auditoria?desde&hasta&accion&idActor&entidad&idEntidad&idPuntoVenta&pagina&tamanio`
  under `.RequireAuthorization(Politicas.LecturaDeAuditoria)`.
  **Note (plumbing, not a scope deviation):** wiring this route required
  two mechanical additions the task/design prose didn't spell out
  line-by-line but every prior `MapearX()` slice needed the same way:
  `app.MapearAuditoria();` in `Program.cs` and
  `services.AddScoped<ServicioDeConsultaDeAuditoria>();` in
  `Ways.Application/DependencyInjection.cs` — without either, the route
  404s / DI throws at first request.
- [x] 5.5 [P] **Mutation target**: `ThenByDescending(a.IdAuditoria)` on the
  ordering — delete it — the tied-`creado_el` pagination test (5.9) must
  fail. *(slice 5 row 1)* **Evidence**: mutated (`orderby a.CreadoEl
  descending, a.Id descending` → `orderby a.CreadoEl descending`) →
  `dotnet build --no-incremental` → `--filter
  FullyQualifiedName~PaginacionConCreadoElEmpatadoNoRepiteNiSalteaYRespetaElOrdenDescendentePorId`
  → FAILED (`Expected: [5,4,3,2,1], Actual: [2,1,3,4,5]` — the concatenated
  3-page sequence stopped matching the expected strict descending-id order)
  → reverted → green.
- [x] 5.6 [P] **Mutation target**: `candidatos.DefaultIfEmpty()` (the LEFT
  JOIN) → an inner join — the root-actor/soft-deleted-actor visibility test
  (5.10) must fail. *(slice 5 row 2)* **Evidence**: mutated (`join u in
  ... into actores from u in actores.DefaultIfEmpty()` → plain `join u in
  ... on a.IdActor equals u.Id`, no `into`/`DefaultIfEmpty`) → build →
  `--filter
  FullyQualifiedName~UnActorSoftDeletedSigueMostrandoElNombreYUnActorRootApareceConActorNuloEIdActorPresente`
  → FAILED (`InvalidOperationException: Sequence contains no matching
  element` — the root-actor row vanished from the result set entirely,
  `.Single()` found nothing) → reverted → green.
- [x] 5.7 [P] **Mutation target**: `IgnoreQueryFilters(["BajaLogica"])` —
  remove it — the soft-deleted-actor-name half of 5.10 must fail. *(slice 5
  row 3)* **Evidence**: mutated (`db.Usuarios.IgnoreQueryFilters(["BajaLogica"])`
  → `db.Usuarios`) → build → same filter as 5.6 → FAILED
  (`Expected: "vendedor-de-baja", Actual: null` — the soft-deleted actor's
  row survived the LEFT JOIN but its name disappeared, as predicted) →
  reverted → green.
- [x] 5.8 [P] **Mutation target**: each `if (filtro is { } x)` clause in
  `ConstruirQuery` — delete one at a time — the matching filter's dedicated
  test (5.11-5.16, asymmetric seeds per filter) must fail, with no other
  clause producing the same subset. *(slice 5 row 4; mutation-proof-tests
  rules 4/6)* Implemented as one combined `where (... || ...) && (... ||
  ...) && ...` (design's own snippet shape), so each mutation replaces one
  AND-term with `true`. **Evidence, one clause at a time, `dotnet build
  --no-incremental` between each**: `Desde` → true →
  `FiltroDeFechaDevuelveElSubconjuntoEsperado` FAILED (`Expected: 3, Actual:
  5`) → reverted; `Hasta` → true → same test FAILED (`Expected: 3, Actual:
  6`) → reverted; `Accion` → true →
  `FiltroDeAccionDevuelveElSubconjuntoEsperado` FAILED (`Expected: 2,
  Actual: 8`) → reverted; `IdActor` → true →
  `FiltroDeActorDevuelveElSubconjuntoEsperado` FAILED (`Expected: 3, Actual:
  8`) → reverted; `Entidad` → true →
  `FiltroDeEntidadMasIdEntidadDevuelveSoloLaHistoriaDeEseAgregado` FAILED
  (`Expected: 3, Actual: 4`) → reverted; `IdEntidad` → true → same test
  FAILED (`Expected: 3, Actual: 5`) → reverted; `IdPuntoVenta` → true →
  `FiltroDePuntoDeVentaDevuelveElSubconjuntoEsperado` FAILED (`Expected: 3,
  Actual: 8`) → reverted → 16/16 green.
  **DEVIATION (registered, mutation-proof-tests rule 3):** the `Entidad`/
  `IdEntidad` pair was originally UNDISCRIMINATING — the fixture's
  `idEntidad` values never collided across different `entidad`s, so
  `idEntidad=41` alone already identified the same 3-row subset regardless
  of the `entidad` clause (an overdetermined confound, same defect class
  the skill documents). Fixed by making R2 (`comprobante_venta`) carry
  `idEntidad=41` too — colliding with the `articulo`/41 rows — which is
  what actually makes the `Entidad` mutation observable (see the 4-vs-3
  evidence above).
- [x] 5.9 [P] Integration: pagination with `creado_el` tied across every
  row (RelojFijo) — page 2 neither repeats nor skips a row, resolving 5.5's
  evidence.
  `AuditoriaConsultaTests.PaginacionConCreadoElEmpatadoNoRepiteNiSalteaYRespetaElOrdenDescendentePorId`
  — asserts the concatenated 3-page sequence (tamanio=2) equals the full
  expected strictly-descending-by-id order (design Testing Strategy:
  "order as a sequence, not a set"), not merely "no dup/skip" as a set.
- [x] 5.10 [P] Integration — actor visibility: a row whose actor is
  soft-deleted still shows the actor's name; a row whose actor is a
  root/platform user, read by a tenant Admin, appears with `actor: null`
  and `idActor` present — resolving 5.6 and 5.7's evidence. *(design
  decision 14)*
  `AuditoriaConsultaTests.UnActorSoftDeletedSigueMostrandoElNombreYUnActorRootApareceConActorNuloEIdActorPresente`.
- [x] 5.11 [P] Integration: `desde`/`hasta` returns its expected subset
  (asymmetric seeds — every date, actor, entidad and PV distinct).
  `AuditoriaConsultaTests.FiltroDeFechaDevuelveElSubconjuntoEsperado` — 8-row
  fixture (`SembrarEscenarioDeFiltrosAsync`), every row's date/accion/actor/
  entidad+id/PV mutually distinct (mutation-proof-tests rule 6).
- [x] 5.12 [P] Integration: `accion` returns its expected subset; an
  unknown `accion` returns `200` with zero rows. *(design decision 15)*
  `FiltroDeAccionDevuelveElSubconjuntoEsperado` +
  `UnaAccionDesconocidaDevuelve200ConCeroFilas`.
- [x] 5.13 [P] Integration: `idActor` returns its expected subset.
  `FiltroDeActorDevuelveElSubconjuntoEsperado`.
- [x] 5.14 [P] Integration: `entidad` + `idEntidad` returns exactly that
  aggregate's rows — 3 rows for articulo 41, 2 for articulo 42. *(spec:
  "Filtering by entidad + id_entidad returns only that aggregate's
  history")*
  `FiltroDeEntidadMasIdEntidadDevuelveSoloLaHistoriaDeEseAgregado` — asserts
  both aggregates (41 and 42), not just the spec's literal one.
- [x] 5.15 [P] Integration: `idPuntoVenta` returns its expected subset;
  unset ("todos") includes `id_punto_venta IS NULL` rows. *(spec:
  "Tenant-wide rows appear under 'todos' punto de venta")*
  `FiltroDePuntoDeVentaDevuelveElSubconjuntoEsperado` (both PVs) +
  `SinFiltroDePuntoDeVentaTodosIncluyeLasTenantWideYAmbosPuntosDeVenta`
  (also resolves 5.18's "Admin reads across every punto de venta" scenario
  in the same assertion: both PVs' rows AND the PV-null rows present in one
  response).
- [x] 5.16 [P] Integration: `idEntidad` without `entidad` → `400
  entidad_requerida`. *(design decision 16)*
  `IdEntidadSinEntidadRechazaCon400EntidadRequerida` — asserts the status
  code AND the `codigo` field of the ProblemDetails body.
- [x] 5.17 [P] **Mutation target**:
  `.RequireAuthorization(Politicas.LecturaDeAuditoria)` on `GET
  /api/auditoria` — delete the line — the Supervisor-403 test (5.18) must
  fail. *(slice 5 row 5)* **Evidence**: mutated (`.RequireAuthorization(...)`
  line deleted from the `MapGroup`) → build → `--filter
  FullyQualifiedName~UnSupervisorEsRechazado` → FAILED (`Expected:
  Forbidden, Actual: OK` — the group fell back to `Program.cs`'s
  authenticated-only fallback policy, letting any logged-in role through) →
  reverted → green.
- [x] 5.18 [P] Integration — authorization: Supervisor → `403`; Vendedor →
  `403`; Root → `403`; Admin → `200`, sees rows from **every** punto de
  venta of the tenant. *(spec: "Admin reads across every punto de venta of
  the tenant", "A Supervisor is rejected", "A Vendedor is rejected")*
  `UnSupervisorEsRechazado`, `UnVendedorEsRechazado`, `UnRootEsRechazado`,
  `UnAdminEsAceptado` (+ the cross-PV assertion inside 5.15's
  `SinFiltroDePuntoDeVentaTodosIncluyeLasTenantWideYAmbosPuntosDeVenta`).
  Supervisor/Vendedor clients are created ONLY inside these two tests (via
  `CrearYLoguearAsync`, a real `POST /api/usuarios`) — deliberately kept
  OUT of the shared `PrepararAsync` scaffolding, because a real alta writes
  its own `usuario.alta` audit row (slice 2) that would otherwise pollute
  every exact-count assertion in 5.11-5.16/5.9 (caught this exact
  contamination on first run: `Expected: 8, Actual: 10` — fixed by moving
  role-client creation out of the shared setup).
- [x] 5.19 [P] **Mutation target**: the tenant/RLS filter of the query —
  read with another tenant's GUC — the tenant-isolation test (5.20) must
  fail. *(slice 5 row 6)*
  **DEVIATION (registered, mutation-proof-tests rule 3 — the exact
  `LotesRlsTests` precedent):** over `ways_app`, RLS alone already isolates
  regardless of the EF tenant filter — mutating/removing
  `AplicarFiltroDeTenantEnAuditoria` would NOT make an `ways_app`-based
  isolation test fail (confound, not a real proof). Routed BELOW the
  confound, same as `LotesRlsTests.CrearContextoDeOwner`: a dedicated test
  runs `ServicioDeConsultaDeAuditoria.ConsultarAsync` over a
  `WaysDbContext` built on the OWNER connection (`ways_owner`, bypasses
  RLS) — the ONLY mechanism left able to isolate is the EF query filter
  itself. **Evidence**: mutated (commented out
  `AplicarFiltroDeTenantEnAuditoria(modelBuilder);` in
  `WaysDbContext.OnModelCreating`) → `dotnet build --no-incremental` →
  `--filter
  FullyQualifiedName~ElFiltroDeTenantDeLaConsultaAislaAunSobreUnaConexionQueBypaseaRls`
  → FAILED (`Assert.DoesNotContain() Failure: Item found in set` — tenant
  A's row leaked into tenant B's owner-connection session) → reverted →
  green.
- [x] 5.20 [P] Integration — tenant isolation over `ways_app`
  (`mutation-proof-tests` rule 5): row-count isolation, plus an Admin of
  tenant B never seeing tenant A's rows through the endpoint.
  `UnAdminDeOtroTenantNuncaVeFilasDelTenantAjenoATravesDelEndpoint` (HTTP,
  full stack, `ways_app`) +
  `ElFiltroDeTenantDeLaConsultaAislaAunSobreUnaConexionQueBypaseaRls` (the
  genuinely discriminating half, 5.19's evidence — see its DEVIATION note).
- [x] 5.21 Gate guard: `has-pending-model-changes` clean; zero new files in
  `Migraciones/`. **Confirmed**: `dotnet ef migrations
  has-pending-model-changes --project src/Ways.Infrastructure
  --startup-project src/Ways.Infrastructure` → "No changes have been made
  to the model since the last migration."; `git diff --stat main --
  src/Ways.Infrastructure/Persistencia/Migraciones/` → empty.
- [ ] 5.22 Run `judgment-day`; fix confirmed issues; re-judge until clean.
  *(orchestrator — out of `sdd-apply`'s scope per the launch prompt)*
- [ ] 5.23 Branch `feat/stage14-slice5-consulta` off `main` (parent:
  slice 1); PR; merge stacked-to-main. *(branch already exists — this
  worktree runs on it, created off `main` at `5fe20f1` per the launch
  prompt; PR creation/merge is orchestrator work)*

**Test plan**: 6 mutation targets (5.5-5.8, 5.17, 5.19), per-filter tests
×6 (5.11-5.16), tied-pagination (5.9), actor visibility (5.10),
authorization ×4 roles (5.18), tenant isolation (5.20).

**Verify**: `dotnet test --filter FullyQualifiedName~Auditoria` → **60/60
green** (16 new `AuditoriaConsultaTests` + 4 new `PoliticasTests` theory
cases + 40 pre-existing Auditoria-filtered tests from slices 1-4,
unaffected).

---

## Slice 6: Export (PR 6)

**Start**: slice 5 merged. **Finish**: `GET /api/auditoria/export`
mirrors the JSON endpoint's figures cell by cell, refuses rather than
truncates at cap. **Rollback**: revert the branch — the JSON endpoint is
untouched.

- [ ] 6.1 Create `src/Ways.Application/Exportacion/ExportacionDeAuditoria.cs`:
  `De(IReadOnlyList<FilaDeAuditoria> filas, ctx, zona)` mapping to
  `TablaExportable`, 8 columns in order: `Fecha · Acción · Entidad · Id
  entidad · Actor · Punto de venta · Valor anterior · Valor nuevo`.
  `Actor` null ⇒ `"#<idActor>"`; `Punto de venta` null ⇒ blank cell; both
  payload cells via `Celda.Texto(JsonSerializer.Serialize(elemento))`.
- [ ] 6.2 Modify `ServicioDeConsultaDeAuditoria.cs`: add
  `ConsultarParaExportacionAsync(f, tope, ct)` = `Contar → GuardaDeTope.
  Exigir → Take(tope+1) → Exigir` (the stage-11 listing shape, decision 13),
  mapping from the **same** `FilaDeAuditoria` the JSON endpoint returns.
- [ ] 6.3 Modify `src/Ways.Api/Endpoints/AuditoriaEndpoints.cs`: `GET
  /api/auditoria/export?...&formato=xlsx`, `desde`/`hasta` mandatory
  (export house rule), `NombreDeArchivo.Construir("auditoria", alcance,
  desde, hasta)`. No separate `.RequireAuthorization` — inherited by
  co-location under the same route group as `LecturaDeAuditoria`.
- [ ] 6.4 Modify `src/Ways.Web/src/api/{tipos,auditoria}.ts`: add
  `rutasDeExportacion.auditoria(filtros)` (route builder only — the screen
  is slice 7).
- [ ] 6.5 [P] **Mutation target**: `GuardaDeTope.Exigir` (the second call,
  after `Take(tope+1)`) — delete it — the over-cap export test (6.7) must
  fail (truncates instead of rejecting). *(slice 6 row 1)*
- [ ] 6.6 [P] **Mutation target**: `ExportacionDeAuditoria`'s header row —
  swap the `Valor anterior`/`Valor nuevo` titles — the full-header-row
  assertion (6.8) must fail. *(slice 6 row 2; `mutation-proof-tests` rule
  8)*
- [ ] 6.7 [P] Integration: with `TopeDeFilas` lowered below the seeded row
  count, the export is rejected `400 exportacion_demasiado_grande` and no
  file is generated, resolving 6.5's evidence. *(spec: "The export refuses
  rather than truncates at cap")*
- [ ] 6.8 [P] Integration — export parity, cell by cell
  (`mutation-proof-tests` rule 8): the same query string on JSON and XLSX
  produce equal figures for every row **and** the complete 8-header-row
  text asserted in order; a tenant-wide row's PV cell is blank; both jsonb
  payload cells equal `JsonSerializer.Serialize` of the JSON endpoint's own
  `JsonElement`. *(spec: "Export figures equal the endpoint's for
  identical filters")*
- [ ] 6.9 [P] **Mutation target**: `.RequireAuthorization` on `/export` —
  delete the line — the Supervisor-403-on-export test (6.10) must fail.
  *(slice 6 row 3)*
- [ ] 6.10 [P] Integration — authorization: Supervisor → `403` on
  `/export`, with no separate policy declared on the route. *(spec: "A
  Supervisor is rejected on the export too, inherited from the source
  route")*
- [ ] 6.11 Gate guard: `has-pending-model-changes` clean; zero new files in
  `Migraciones/`.
- [ ] 6.12 Run `judgment-day`; fix confirmed issues; re-judge until clean.
- [ ] 6.13 Branch `feat/stage14-slice6-export` off `main` (parent:
  slice 5); PR; merge stacked-to-main.

**Test plan**: 3 mutation targets (6.5, 6.6, 6.9), cap refusal (6.7),
cell-by-cell parity + header row (6.8), Supervisor 403 (6.10).

**Verify**: `dotnet test --filter FullyQualifiedName~ExportacionDeAuditoria|FullyQualifiedName~Auditoria`

---

## Slice 7: Web (PR 7)

**Start**: slices 5+6 merged. **Finish**: `Auditoria.tsx` — filters, pager,
before/after detail panel, download, Admin-only nav entry. **Rollback**:
revert the branch — a web-only change over an unchanged API.

**Budget note (design.md)**: pre-authorized cut if this slice overflows —
ship the screen with filters, listing and download; **drop
`PanelDeCambio`/`compararPayloads`**. The payload still reaches the
operator through the export. A documented reduction, never a silent one.

- [ ] 7.1 Modify `src/Ways.Web/src/api/tipos.ts`: mirror
  `FiltrosDeAuditoria`, `FilaDeAuditoria`, `PaginaDeAuditoria`, and the
  12-action catalog labels. `dto-contract-honesty`: doc-comment each
  mirrored field's source (the `Contratos.cs` records from slice 5).
- [ ] 7.2 Modify/create `src/Ways.Web/src/api/auditoria.ts`:
  `clienteDeAuditoria.consultar`, wired to `rutasDeExportacion.auditoria`
  from 6.4.
- [ ] 7.3 Create `src/Ways.Web/src/paginas/Auditoria.tsx`:
  `HistoricoDeCajas.tsx`-shape filters+pager (`FiltrosDeAuditoria` object,
  `filtrosDeAuditoriaVacios()`, `generacionRef` per `react-async-state`
  rule 2, `cambiarFiltro` resets to page 1, `cambiarPagina(±1)` disabled at
  edges) + `Vencimientos.tsx`'s `BotonDeDescarga`; columns `Fecha · Acción
  · Entidad · #Id · Actor · PV`; `Actor` null ⇒ `#<idActor>`; PV null ⇒
  `—` with `title="Evento de todo el tenant"`; the "Todos" PV option is an
  **absent** filter, never `0`.
- [ ] 7.4 Create the pure helper `compararPayloads(anterior, nuevo)`
  (colocated module): a key only in `nuevo` renders `"—→ valor"`; a changed
  key is marked as changed; an equal key is not; both-`null` handled.
- [ ] 7.5 Create `PanelDeCambio`: an expandable row rendering
  `valor_anterior`/`valor_nuevo` key by key via `compararPayloads`, with
  its own `data-testid` per side.
- [ ] 7.6 Modify `src/Ways.Web/src/App.tsx` and
  `componentes/Layout.tsx`: add the `/auditoria` route and one nav line,
  visible only to Admin.
- [ ] 7.7 [P] **Mutation target**: `compararPayloads`'s "key only in
  `nuevo`" branch — treat it as "no change" — the colocated helper test
  (7.8) must fail. *(slice 7 row 1, the sole one; `web-descriptor-tests`)*
- [ ] 7.8 [P] Descriptor tests for `compararPayloads`: key only in `nuevo`,
  changed key, unchanged key, both-null. *(`web-descriptor-tests`)*
- [ ] 7.9 [P] Component test: changing any filter resets the page to 1.
- [ ] 7.10 [P] Component test: a stale response resolved **inside `act`**
  after a filter change is discarded, asserted synchronously after the
  flush (`react-async-state` rule 7).
- [ ] 7.11 [P] Component test: pager `disabled` at both edges (page 1 and
  the last page).
- [ ] 7.12 [P] Component test: `actor: null` renders `#<idActor>`;
  `id_punto_venta: null` renders `—` with the tenant-wide title.
- [ ] 7.13 [P] Component test: the download button calls
  `rutasDeExportacion.auditoria(filtros)` with the current filter state.
- [ ] 7.14 Gate guard: `has-pending-model-changes` clean; zero new files in
  `Migraciones/` (web-only slice — confirms no accidental API/EF drift).
- [ ] 7.15 Run `judgment-day`; fix confirmed issues; re-judge until clean.
- [ ] 7.16 Branch `feat/stage14-slice7-web` off `main` (parent: slices 5+6);
  PR; merge stacked-to-main. **If the slice overflows at apply time, drop
  tasks 7.5/7.7/7.8 (`PanelDeCambio`/`compararPayloads`) per the
  pre-approved degradation above and record the reduction in the PR body —
  never a silent cut.**

**Test plan**: mutation target (7.7), descriptor tests (7.8), stale-inside-
`act` (7.10), pager edges (7.11), null renders (7.12), download wiring
(7.13).

**Verify**: `npm run test -- Auditoria`

---

## Global Cross-Slice Tasks

- **`dto-contract-honesty` compliance**: enforced at slices 1 (1.3, 1.4,
  1.5), 5 (5.2) and 7 (7.1) — every new/mirrored field has a documented
  destination.
- **`mutation-proof-tests` compliance**: all 28 named mutation targets are
  placed exactly once (§ Orchestrator Decision 7), each with apply-time
  evidence required in its slice's PR body; the checkout non-regression
  (design's unnumbered "—" row) is a binding verify criterion, task 3.12,
  not counted among the 28.
- **`db-error-backstops`**: applies once, task 1.28 — the `fk_auditoria_
  actor` SQLSTATE `23503` fail-closed test, covered by the existing generic
  `fk_`/`23503` mapping with no new mapping added.
- **`react-async-state`/`web-descriptor-tests` compliance**: slice 7 is the
  only web-touching slice; every new pure helper (`compararPayloads`) ships
  a colocated descriptor test in that same slice.
- **Checkout-budget protection (proposal decision 4, design intro)**: no
  task in any slice touches the checkout emission path;
  `VentasCheckoutTests.cs` is absent from the stage's diff (task 3.12) —
  binding, confirmed by `sdd-verify`.
- **`movimientos_cuenta_corriente.detalle` untouched** (proposal decision
  7): no task in slice 4 changes its type, content or serialization —
  confirmed by `sdd-verify`.
- **`ManejadorDeErrores.cs` untouched** (gate §B): no task in any slice
  modifies it — the generic `fk_`/`23503` mapping already covers this
  stage.
- **`ServicioDeStock.InsertarMovimientoStockAsync` signature ripple**
  (Orchestrator Decision 15): recorded so `sdd-verify` does not read
  task 4.1 as a scope violation.

---

## Dependency Summary

```
Slice 1 (tabla-auditoria)
  ├─ Slice 2 (precios-usuarios)
  ├─ Slice 3 (anulaciones)
  ├─ Slice 4 (stock-cc)
  └─ Slice 5 (consulta) ── Slice 6 (export)
                                      │
                                      ▼
                            Slice 7 (web)
                            needs: 5 (query contract), 6 (download route)
```

Merge order: `1` blocks everything → `{2, 3, 4}` in any interleaving
(disjoint service files: `Precios`/`Usuarios`, `Ventas`/`Compras`,
`Stock`/`CuentaCorriente`) → `5 → 6` → `7` last. `2`/`3`/`4` never conflict
with `5`/`6` (`AuditoriaEndpoints.cs` is created in 5 and only extended in
6 — no shared line with 2/3/4's files).

---

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | ~2 360 total (7 slices: 430/340/300/320/380/230/360) |
| 400-line budget risk | Medium — slices 1 and 5 sit closest to the cap; pre-identified cuts named in each slice's Budget note (`1a`/`1b`, `5a`/`5b`) |
| Chained PRs recommended | Yes |
| Suggested split | 7 PRs, stacked-to-main, per the Suggested Work Units table above |
| `size:exception` anticipated | No — the migration is one small table and its pre-authorized `1a`/`1b` split keeps it under budget without an exception |
| Delivery strategy | `auto-chain` (already resolved, `state.yaml`) |
| Chain strategy | `stacked-to-main` |
| Decision needed before apply | No — already resolved |

Per-slice budget risk: 1 **Medium (~430)** · 2 Low (~340) · 3 Low (~300) ·
4 Low (~320) · 5 **Medium (~380)** · 6 Low (~230) · 7 Low (~360, with its
own pre-approved overflow mitigation — drop `PanelDeCambio`, not split the
PR). As in prior stages, overflow is expected to come from **test depth** —
the twelve coverage tests spread across slices 2-4, the six-filter
integration suite in slice 5, and the cell-by-cell export parity test in
slice 6 — not from scope creep. Coverage of the twelve actions and the
fail-closed rule are never degraded (Orchestrator Decision 3): a coverage
slice splits, it is never trimmed.

Decision needed before apply: No
Chained PRs recommended: Yes
Chain strategy: stacked-to-main
400-line budget risk: Medium
