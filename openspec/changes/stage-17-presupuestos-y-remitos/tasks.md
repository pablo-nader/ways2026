# Tasks: Stage 17 — Presupuestos y remitos

## Orchestrator Decisions Recorded This Phase

> `spec.md` and `design.md` ran in PARALLEL (`state.yaml`). Where they diverge, `state.yaml`'s
> OD8 (spec tensions) and OD9 (design tensions) are authority — **both ratify `design` in every
> case cited**, plus two conflicts this phase found and reconciles below (precedent: stage-16
> "conflict found and resolved" numbering).

1. **8 slices, stacked-to-main, adopted verbatim from `design.md`'s ratified Slicing table**
   (`design.md:582-597`), itself the proposal's tentative plan (`proposal.md:1185-1198`)
   re-scoped by two adjustments design already made: (a) `ManejadorDeErrores` branches move
   into the **schema** slices, split 7/5 (slice 1 / slice 4); (b) doc-10's *"Estado (Etapa 17)"*
   headers are **opened** in slices 1 and 4 and **closed** in slice 8 — the programme's new
   closing rule (stage-16's W1 verify remediation, codified forward). Merge order
   `1 → 2 → 3 → 4 → 5 → 6 → 7 → 8`. Slice 1 blocks 2, 3, 7; slice 4 blocks 5, 6, 8; 3 depends on
   2 for the entity surface; 6 depends on 5 for the remito lifecycle. Tracks 1-3 and 4-6 are
   independent and may interleave if the chain allows it.
2. **DB gate — `db_gate: DOS-MIGRACIONES-APROBADAS`** (`state.yaml`): slice 1 carries **exactly
   one** new migration, `PresupuestosEtapa17` (1 enum/4 values, 2 tables/10 FKs/2 CHECKs/13
   named indexes + 1 implicit AK index, 1 additive ALTER on `comprobantes_venta` + FK23 + 1
   partial-unique index, 1 data statement, RLS last); slice 4 carries the second and last,
   `RemitosEtapa17` (the isolated `ALTER TYPE ADD VALUE 'remito'`, 1 enum/4 values, 2 tables/12
   FKs/5 CHECKs/15 named indexes + 1 implicit AK index, 1 additive ALTER on `movimientos_stock` +
   FK24 + 1 index, 1 guarded data statement + its `Down`, RLS last). Slices 2-3 and 5-8 each
   carry a gate-guard task requiring `dotnet ef migrations has-pending-model-changes` clean and
   zero new files under `Migraciones/`. **Binding count**: total new indexes = **30**
   (14 cumulative at slice 1 = 6+7 on the presupuesto tables + 1 on `comprobantes_venta`; +16 at
   slice 4 = 7+8 on the remito tables + 1 on `movimientos_stock`) — verified **by definition**
   against `pg_indexes`, never by name, per `state.yaml`'s `db_gate_approval`.
3. **Pre-authorized cut points**, inherited verbatim from `design.md:611-626`, in priority
   order: **(a)** slice 1 → `1a` (type + both presupuesto tables + entities + configs + seed
   change + data statement 1 + RLS/CHECK tests) / `1b` (the `comprobantes_venta` ALTER + index
   29 + the 7 backstops + doc 10) — **one migration per document** is the invariant that must
   not degrade; **(b)** slice 4 → `4a` (`ALTER TYPE` + type + both remito tables) / `4b` (the
   `movimientos_stock` ALTER + the `TXR` data statement + the 5 backstops + doc 10); **(c)**
   slice 6 → `6a` (the consolidation itself) / `6b` (the widened `RETURNING`, the un-link, its
   races); **(d)** slices 7/8 → drop the POS banner (plain conversion link survives) and
   `FacturarRemitos.tsx`'s bulk selection (one remito at a time) — a documented reduction, never
   silent. **Never degraded**: the two `PRE` nets and their independent mutation tests, the
   frozen-price fidelity assertion, the fourth write site's lock order and its rendezvous test,
   and the zero-items/zero-stock assertions of the consolidation.
4. **OD8/T1 — anular una venta convertida NO revierte `convertido`, terminal.** Ratified
   verbatim (`presupuestos/spec.md:200-214`, `design.md` decision 4 and tension T1). Task 5.8
   confirms the remito side has no equivalent coupling by omission; task 3.10/3.11 implement the
   presupuesto side.
5. **OD8/T2 — the doble-anulación-de-remito scenario is MISSING from `remitos/spec.md` and is
   ADDED via tasks, as parity with `comprobantes-venta`'s own double-anulación precedent.**
   `remitos/spec.md`'s "Anulación..." requirement states `borrador`/`emitido` are annullable and
   `facturado` is refused (409), but never states what happens to an **already-`anulado`**
   remito. Tasks 5.9/5.11 close the gap: `WHERE estado = 'emitido'` in the anulación `UPDATE`
   (already load-bearing per `design.md` mutation target 46) additionally refuses a second
   annulment with `409 remito_ya_anulado`, and a dedicated test proves no second inverse
   movement is ever written.
6. **OD8/T3 — the TXR-anulación composition (empty item loop + non-empty pagos/CC reversal)
   is NOT "plausible by construction" and REQUIRES its own discriminant scenario/test**, per
   `state.yaml`'s explicit citation of stage-16 slice 3's lesson ("las composiciones se
   prueban"). Task 6.21 is that test: a TXR annulment where the ORIGINAL consolidation used
   cuenta corriente must prove **both** halves of the composition together — zero
   `movimientos_stock` rows **and** the CC balance actually reversed by the exact original
   amount, in the same transaction. Neither half alone is sufficient evidence.
7. **OD9 — all twelve design tensions (T1-T12, `design.md:663-724`) ratified in favor of
   `design`.** The ones with the most task-surface: **T3** — one `id_lista_precio` per quote is
   a **service invariant**, asserted (`InvalidOperationException` if `items_presupuesto`
   disagree), not a schema fact per line — task 3.7/3.9. **T5** — the `vencido` listing filter
   REQUIRES `idPuntoVenta` (400); `Vencido` resolved per **distinct** PV of the page — task 2.9.
   **T6** — a conversion race loser burns a `TX` **sale** series number, accepted with registry
   (not fiscal) — task 3.17 asserts it explicitly. **T7** — `ComprobanteEmitido` gains
   `IdPresupuestoOrigen` (round-trip, `dto-contract-honesty` rule 2) — task 3.13/3.22. **T8** —
   the remito's anulación carries NO negative-balance guard (a remito decrements, its reversal
   adds — the compra guard would be dead code) — task 5.8, no task adds one. **T9** — the
   consolidation RE-IMPLEMENTS the checkout's credit-limit backstop inside its own transaction
   — task 6.7/6.17. **T10** — the lock-order clause *"a new-row INSERT is not a position"* is
   written into the spec-side of this file (see the task notes on 3.11 and 6.4/6.11). **T12** —
   `emitir` refuses a remito with zero items (400) — task 5.7.
8. **`mutation-proof-tests` compliance**: the **60** named mutation targets in `design.md:518-579`
   are each placed in exactly one slice per design's own "Slice" column: 1 → targets 1-11,
   2 → 12-22, 3 → 23-37, 4 → 38-39, 5 → 40-47, 6 → 48-58, 7-8 → 59-60 (the two compound rows,
   split by sub-clause across the two web slices, none dropped, none duplicated). Every target
   requires apply-time evidence (mutation applied → named failing test → reverted → green)
   recorded in its slice's PR body.
9. **`db-error-backstops` applies to the two schema slices only, per `design.md` decision 18.**
   Slice 1 ships **7** exact-name branches (3 `23505` + 4 `23514`, listed in tasks 1.23-1.27 +
   generic-mapping verifications 1.28-1.29). Slice 4 ships **5** (2 `23505` — the 5th `_numero`
   trap occurrence — + 3 `23514`, tasks 4.24-4.28). Client-reachable FK backstops (cliente,
   punto de venta, presupuesto origen) are verified against the EXISTING generic `23503`
   mapping, not new code.
10. **`react-async-state` + `web-descriptor-tests` apply to slices 7-8 only** — the two
    web-touching slices.
11. **`dto-contract-honesty` applies at slice 3** (`SolicitudDeVenta`/`ComprobanteEmitido` gain
    `IdPresupuestoOrigen`, `lineas` refused when the id is present — task 3.4/3.13/3.20) **and
    at slices 2 and 5** (`ContratosDePresupuesto.cs`/`ContratosDeRemito.cs` — `orden` never
    travels, no money in the create request, `SolicitudDeFacturacionDeRemitos` carries no
    `idCliente` because a conflict would have nowhere to go).
12. **`work-unit-commits` applies to every slice.**
13. **Testing convention — fixed clock and asymmetric seeds, carried verbatim from stage 16.**
    Every date-bearing test pins `RelojFijo(2026-08-19T12:00:00Z)` (mediodía UTC); at least one
    listing/boundary test per slice that touches a date additionally sends the real client
    offset `-03:00` (never `Z`) and asserts both the rows and the displayed value
    (`mutation-proof-tests` rule 10 — the only shape that can see a raw-ADO UTC-normalization
    regression, PR #129's lesson). Every fixture uses **deliberately desynchronized ids**
    (`id_tenant`, `id_punto_venta`, `id_cliente`, `id_presupuesto`/`id_remito`, `id_articulo`
    never coincidentally equal or sequentially aligned).
14. **Archive-phase carryover, registered so it is not read as an omission from this phase's
    scope.** `docs/11-programa-post-paridad.md`'s Etapa 17 status block is explicitly
    "orchestrator, outside the phase" (`proposal.md:1272`) — no task here touches it. Task 8.6
    (doc-10's "Estado (Etapa 17)" closing annotation) IS in scope — it is a slice-8 deliverable,
    not an archive-phase one, per decision 1's closing rule.
15. **Process rule (stage-12/14/15/16 discipline): every deviation `sdd-apply` takes from this
    plan is registered IN `tasks.md`** — as a task-level note or a new numbered decision
    appended to this section — never left to verify-phase archaeology.

### Conflicts found and reconciled this phase

- **CONFLICT #1 (OD8/T2 above)** — remito double-annulment scenario missing from spec, added.
- **CONFLICT #2 (OD8/T3 above)** — TXR-anulación composition discriminant test missing, added.
- **CONFLICT #3 — NEW, found this phase, not named by OD8 or OD9.** `presupuestos/spec.md`'s
  "Conversion Freezes The Presupuesto's Own Snapshot..." requirement never states that the
  conversion's `idPuntoVenta` must agree with the presupuesto's own punto de venta.
  `design.md`'s guarded `UPDATE` (`:154-160`) carries `AND id_punto_venta = $3` as one of its
  four conjuncts and names the domain code `400 punto_venta_no_coincide`
  (`EscriturasDePresupuesto.ExigirCausaDelRechazoAsync`, `design.md:126`), and mutation target
  33 is built entirely around it. **Resolved in favor of `design`**, same pattern as stage-16's
  CONFLICT #2 (`state.yaml`'s spec OD7/T3 precedent: *"si el spec deja códigos de dominio sin
  nombre, el design los nombra; si no, tasks los reconcilia"*) — task 3.18 implements and tests
  it as a cross-PV conversion refusal.
- **CONFLICT #4 — NEW, same class.** `remitos/spec.md`'s consolidation requirement states a
  row-count mismatch "MUST return 409" but never names the code; `design.md:169` names
  `remito_no_facturable`. Resolved in favor of `design` (task 6.8). Similarly
  `presupuesto_inconsistente` (totals-fidelity 409, `design.md:68`, task 3.9),
  `remito_sin_items`/`articulo_no_es_producto` (`design.md` decision 14, task 5.7), and
  `cantidad_de_linea_invalida` (backstop tables, tasks 1.27/4.28) are all domain codes the
  proposal/spec left unnamed and `design` names — adopted verbatim, no further reconciliation
  needed per the established convention.
- **No further conflicts found.** Every other design decision either restates the proposal's
  gate contract verbatim (checked line-by-line against `proposal.md` §A-K) or is one of the
  twelve tensions OD9 already ratifies.

---

## Suggested Work Units

| Unit | Goal | Likely PR | Focused test command | Runtime harness | Rollback boundary |
|---|---|---|---|---|---|
| 1 | `PresupuestosEtapa17` migration + entities + `ReglaDePresupuestos` + configs + `MapEnum` + seed change + 7 `ManejadorDeErrores` branches + doc 10 | PR 1 | `dotnet test --filter FullyQualifiedName~PresupuestosSchema\|FullyQualifiedName~ReglaDePresupuestos` | Testcontainers Postgres 17, `ways_app` NOSUPERUSER NOBYPASSRLS | `DROP INDEX`→`DROP CONSTRAINT`→`DROP COLUMN`→both tables→type, no dependent object |
| 2 | `ServicioDePresupuestos` draft CRUD + `enviar` (`'PRES'`) + `anular` + derived `vencido` in PV zona | PR 2 | `dotnet test --filter FullyQualifiedName~ServicioDePresupuestos` | Real Postgres, forced rendezvous (two `enviar` tasks) | Endpoints/service disappear, schema untouched |
| 3 | Resolver guard (net 2) + decide-phase snapshot branch + `EscriturasDePresupuesto` + guarded call at 1.5 + `ComprobanteEmitido.IdPresupuestoOrigen` | PR 3 | `dotnet test --filter FullyQualifiedName~ServicioDeVentasConversion\|FullyQualifiedName~EscriturasDePresupuesto` | Real Postgres, forced rendezvous (convertir×convertir) | Guarded call + branch disappear, checkout reverts to byte-identical pre-stage |
| 4 | `RemitosEtapa17` migration (isolated `ALTER TYPE`) + entities + `MotivoStock.Remito` + configs + 5 `ManejadorDeErrores` branches + doc 10 | PR 4 | `dotnet test --filter FullyQualifiedName~RemitosSchema` | Testcontainers Postgres 17 | `DROP CONSTRAINT`→`DROP COLUMN`→both tables→type; `motivo_stock` value NOT reverted (accepted) |
| 5 | `ServicioDeRemitos` draft + `emitir` (fourth write site) + `anular` (incl. double-anulación guard, OD8/T2) | PR 5 | `dotnet test --filter FullyQualifiedName~ServicioDeRemitos` | Real Postgres, forced rendezvous (remitir×remitir, remitir×checkout) | Endpoints/service disappear, schema untouched |
| 6 | `ServicioDeFacturacionDeRemitos` + `EscriturasDeRemito` + widened `RETURNING` + guarded un-link in `AnularAsync` | PR 6 | `dotnet test --filter FullyQualifiedName~ServicioDeFacturacionDeRemitos` | Real Postgres, forced rendezvous (facturar×facturar, facturar×anular) | Guarded call + service disappear, checkout anulación reverts to pre-stage |
| 7 | Web: `Presupuestos.tsx`/`Presupuesto.tsx` + client + routes + `Pos.tsx` conversion branch | PR 7 | `npm run test -- Presupuesto` (Vitest) | Vitest + RTL | Screens/branch disappear, API still serves the shape |
| 8 | Web: `Remitos.tsx`/`Remito.tsx`/`FacturarRemitos.tsx` + client + routes + doc-10 closing annotation | PR 8 | `npm run test -- Remito` (Vitest) | Vitest + RTL | Screens disappear, API still serves the shape |

Total ≈ **3 880-4 000 lines naive**. `Decision needed before apply: No` — `auto-chain` +
`stacked-to-main` already resolved in `state.yaml`. **10-12 PR outturn expected** (four
pre-authorized splits above).

---

## Slice 1: Schema presupuestos + ramas + cierre del PRE (nets 1/1b) (PR 1)

**Branch**: `feat/stage17-slice1-schema-presupuestos`. **Start**: `main`. **Finish**:
`estado_presupuesto` + both tables + the `comprobantes_venta` ALTER exist with standard RLS, 14
cumulative new indexes, 7 `ManejadorDeErrores` branches proven out-of-band, `PRE` deactivated on
both migrated and fresh databases; doc 10 carries both tables. No write path calls anything yet
(slice 2/3). **Net 2 (the resolver clause) does NOT land here** — it touches `ServicioDeVentas`
and ships in slice 3; this slice closes only nets 1 and 1b. **Rollback**: `DROP INDEX
ux_comprobantes_venta_presupuesto_origen` → `ALTER TABLE comprobantes_venta DROP CONSTRAINT
fk_comprobantes_venta_presupuesto_origen` → `DROP COLUMN id_presupuesto_origen` → `DROP TABLE
items_presupuesto` → `DROP TABLE presupuestos` → `DROP TYPE estado_presupuesto` (`proposal.md:
1085-1090`). **Budget note**: pre-authorized split `1a`/`1b` (decision 3 above) if this slice
overflows. **Done** = tests green + `judgment-day` clean round + PR merged.

- [ ] 1.1 Migration `PresupuestosEtapa17`: `CREATE TYPE estado_presupuesto AS ENUM
  ('borrador','enviado','convertido','anulado')`. *(proposal.md:625, design.md:91)*
- [ ] 1.2 Same migration: `CREATE TABLE presupuestos` — 17 columns exactly per §C (`numero
  bigint NULL`, `fecha_emision` **no DEFAULT**, `vencimiento date NULL`); `pk_presupuestos`.
  *(proposal.md:653-672)*
- [ ] 1.3 Same migration: 4 named FKs on `presupuestos` + `ak_presupuestos_id_presupuesto_id_tenant
  UNIQUE (id_presupuesto, id_tenant)`. *(proposal.md:678-686)*
- [ ] 1.4 Same migration: `ck_presupuestos_envio_completo` exactly per §C's table (three
  conjuncts, `anulado` admitted without number/date/vencimiento). *(proposal.md:686)*
- [ ] 1.5 Same migration: 6 named indexes — `ix_..._tenant`, `ix_..._punto_venta_fecha`,
  `ix_..._cliente`, `ix_..._empleado` (simple), `ux_presupuestos_numero` **UNIQUE PARTIAL**
  `WHERE numero IS NOT NULL` — plus the implicit AK index (7 total). Zero EF-autogenerated
  FK-support index beyond these. *(proposal.md:689-702)*
- [ ] 1.6 Same migration: `CREATE TABLE items_presupuesto` — 17 columns exactly per §D (no
  `id_area`, no `codigo_barra`, no `costo_unitario`, no `id_lote`); `pk_items_presupuesto`.
  *(proposal.md:714-733)*
- [ ] 1.7 Same migration: 6 named FKs on `items_presupuesto` + `ck_items_presupuesto_cantidad_positiva`.
  *(proposal.md:741-750)*
- [ ] 1.8 Same migration: 7 named indexes — 6 FK-support + `ux_items_presupuesto_orden`
  **UNIQUE** `(id_presupuesto, orden)`. *(proposal.md:753-763)*
- [ ] 1.9 Same migration: `ALTER TABLE comprobantes_venta ADD COLUMN id_presupuesto_origen
  integer NULL` + `fk_comprobantes_venta_presupuesto_origen` composite MATCH SIMPLE + explicit
  `CREATE UNIQUE INDEX ux_comprobantes_venta_presupuesto_origen ... WHERE id_presupuesto_origen
  IS NOT NULL` — the 1:1 database guarantee. *(proposal.md:887-903, gate §G)*
- [ ] 1.10 Same migration, data statement 1: `UPDATE tipos_comprobante SET activo = false WHERE
  codigo = 'PRE'` — idempotent, net 1 of decision 2. *(proposal.md:943-944)*
- [ ] 1.11 Same migration: `HabilitarRlsDeTenant` on both new tables, **LAST** — verify the
  generated `Up()` matches the exact ordering `CREATE TYPE → presupuestos+idx → items_presupuesto+idx
  → ALTER comprobantes_venta+FK+idx → data stmt 1 → RLS`. Hand-reorder if EF emits a different
  sequence (stage-16 precedent, register any reordering as a deviation here).
  *(proposal.md:1010-1012)*
- [ ] 1.12 Create `src/Ways.Domain/Ventas/EstadoPresupuesto.cs` — 4 values, member order = native
  type order. *(design.md:91)*
- [ ] 1.13 Create `src/Ways.Domain/Ventas/ReglaDePresupuestos.cs` — `EstaVencido`/`EsConvertible`
  pure functions, no database, `ReglaDeLotes` pattern. *(design.md:99-106, decision 11)*
- [ ] 1.14 Create `Presupuesto.cs` / `ItemPresupuesto.cs` — `EntidadTenant` ⇒ `EntidadBase`.
  *(design.md:440, gate §C-§D)*
- [ ] 1.15 Create `PresupuestoConfiguration.cs` / `ItemPresupuestoConfiguration.cs` — every
  support index declared by hand with doc-10 names. *(design.md:445)*
- [ ] 1.16 Modify `ComprobanteVentaConfiguration.cs` — `IdPresupuestoOrigen` + FK23 + the named,
  filtered `ux_comprobantes_venta_presupuesto_origen`. *(design.md:446, mutation target 6)*
- [ ] 1.17 Modify `WaysDbContext.cs` / `IWaysDbContext.cs` — two new `DbSet`s. *(design.md:448)*
- [ ] 1.18 Modify `WaysDbContextFactory.cs` **and** `DependencyInjection.cs` —
  `MapEnum<EstadoPresupuesto>` in **both** builders, never also `HasPostgresEnum`.
  *(design.md:449, mutation target 9)*
- [ ] 1.19 Modify `InicializadorDeBaseDeDatos.cs` — `TiposComprobanteBase` gains an explicit
  `Activo` field, `false` for `PRE` alone — **net 1b, mandatory**: without it every fresh
  install reopens the hole (seeder runs only against an empty DB, after migrations,
  `:432`). *(proposal.md:957-962, decision 2, mutation target 10)*
- [ ] 1.20 Modify `docs/10-modelo-de-datos.md` — `presupuestos` + `items_presupuesto` tables,
  `comprobantes_venta.id_presupuesto_origen`, `PRE` inactive note, "Estado (Etapa 17)" header
  **OPENED** (closes at slice 8, decision 1). *(proposal.md:83-84, design.md:465)*
- [ ] 1.21 Modify `src/Ways.Api/Seguridad/ManejadorDeErrores.cs` — exact-name
  `ux_presupuestos_numero` → `numero_de_presupuesto_duplicado`, 409, **ABOVE**
  `ClasificarUnicidad`'s generic `_numero` branch — **4th occurrence** of the ordering trap.
  *(proposal.md:968, design.md:381, mutation target 7)*
- [ ] 1.22 Same file: exact-name `ux_comprobantes_venta_presupuesto_origen` →
  `presupuesto_ya_convertido`, 409, above `ClasificarUnicidad`. *(proposal.md:970, design.md:383,
  mutation target 8)*
- [ ] 1.23 Same file: exact-name `ux_items_presupuesto_orden` → `orden_de_item_duplicado`, 409.
  *(proposal.md:971)*
- [ ] 1.24 Same file: exact-name `ck_presupuestos_envio_completo` →
  `presupuesto_envio_incompleto`, 409. *(design.md:386)*
- [ ] 1.25 Same file: exact-name `ck_items_presupuesto_cantidad_positiva` →
  `cantidad_de_linea_invalida`, 400. *(design.md:389)*
- [ ] 1.26 Verify (no new code): the existing generic `23503` → `400 referencia_invalida`
  mapping covers FK3 (`…_cliente`), FK7-10 (`…_articulo`/`…_lista_precio`/`…_oferta`/
  `…_alicuota_iva`) and FK23 (`…_presupuesto_origen`, backstop only — the state-guarded
  `UPDATE` of slice 3 is the primary authority). *(proposal.md:974-979)*
- [ ] 1.27 Document (no mapping): `ak_presupuestos_id_presupuesto_id_tenant` exemption —
  structurally unviolable, no `23505` branch. *(proposal.md:973)*
- [ ] 1.28 [P] RLS test: cross-tenant `SELECT` on `presupuestos`/`items_presupuesto` → 0 rows
  via `ways_app`; `INSERT` with foreign `id_tenant` → `42501`. *(mutation target 1)*
- [ ] 1.29 [P] Raw-insert `ck_presupuestos_envio_completo` — three directions (`numero` without
  `fecha_envio`, without `vencimiento`, `enviado` without `numero`) → `23514`. *(mutation target 2)*
- [ ] 1.30 [P] Raw-insert `ck_items_presupuesto_cantidad_positiva` → `23514`. *(mutation target 3)*
- [ ] 1.31 Test: two drafts (`numero IS NULL`) in one PV both insert cleanly — proves the
  partial `WHERE numero IS NOT NULL` filter on `ux_presupuestos_numero`. *(mutation target 4)*
- [ ] 1.32 Test: two ordinary sales (`id_presupuesto_origen IS NULL`) both insert cleanly —
  proves the partial filter on index 29. *(mutation target 5)*
- [ ] 1.33 `pg_indexes` audit: `ux_comprobantes_venta_presupuesto_origen` is the **only** index
  covering FK23, no EF-autogenerated `IX_…` sibling. *(mutation target 6)*
- [ ] 1.34 Raw duplicate-number insert on `ux_presupuestos_numero` → translated
  `numero_de_presupuesto_duplicado`, not `numero_duplicado` (**4th** `_numero` trap occurrence).
  *(mutation target 7)*
- [ ] 1.35 Raw duplicate insert on `ux_comprobantes_venta_presupuesto_origen` → translated
  `presupuesto_ya_convertido`. *(mutation target 8)*
- [ ] 1.36 [P] Domain unit — `ReglaDePresupuestos` full truth table: 4 estados × (`vencimiento`
  before/equal/after `hoy`) × NULL; `EstaVencido` false for every non-`enviado` estado; the
  boundary `vencimiento == hoy` ⇒ convertible. *(design.md:494)*
- [ ] 1.37 **GATE GUARD** — exactly one migration file, named `PresupuestosEtapa17`, exists in
  this slice's diff; `dotnet ef migrations has-pending-model-changes` clean; `pg_indexes` shows
  exactly **14** new indexes (6 `presupuestos` + 7 `items_presupuesto` + 1
  `ux_comprobantes_venta_presupuesto_origen`) verified by definition. *(state.yaml db_gate_approval,
  proposal.md:1130-1137)*
- [ ] 1.38 **GATE GUARD, net 1** — a **migrated** database's `PRE` is inactive, independent of
  net 1b (test still fails if only the seed change is removed). *(mutation target 11)*
- [ ] 1.39 **GATE GUARD, net 1b** — a **freshly seeded** database has `PRE` inactive.
  *(mutation target 10)*
- [ ] 1.40 **GATE GUARD, partial closure note** — net 2 (the resolver clause) is NOT part of
  this slice's evidence; a `POST /api/ventas` with `"PRE"` still passes today's resolver until
  slice 3 ships. This is a deliberate, registered gap between slices 1 and 3, closed by task
  3.2/3.3's *"venta fantasma 400 SIEMPRE"* test only once slice 3 merges.
- [ ] 1.41 [P] Non-regression: `VentasCheckoutTests`/`VentasAnulacionTests`/
  `VentasAtomicidadYConcurrenciaTests` suites green and **unedited** (schema-only slice).
  *(design.md:580)*
- [ ] 1.42 `judgment-day` round: two blind review agents, fix confirmed findings, re-judge to a
  clean round.
- [ ] 1.43 Open PR #1 `feat/stage17-slice1-schema-presupuestos`, merge to `main` after a clean
  `judgment-day` round.

---

## Slice 2: ABM + numeración de presupuestos (PR 2)

**Branch**: `feat/stage17-slice2-presupuestos-abm`. **Start**: PR 1 merged. **Finish**: full
draft/enviar/anular/list/detail lifecycle over the schema of slice 1, own series `'PRES'`.
**Rollback**: endpoints + service disappear, schema untouched.

- [ ] 2.1 Create `ContratosDePresupuesto.cs` — `SolicitudDePresupuesto`/`LineaDePresupuesto`/
  `SolicitudDeEnvio`/`ItemDePresupuesto`/`PresupuestoDetalle`/`PresupuestoParaVenta`. `orden`
  never travels; no money in the create request (`dto-contract-honesty` rule 1).
  *(design.md:180-202)*
- [ ] 2.2 Create `ServicioDePresupuestos.cs` — `CrearBorradorAsync`: resolves prices via
  `ServicioDeOfertas` at save time (mirrors the checkout's own price-at-decide-time rule),
  persists `estado = borrador`, `numero`/`fecha_envio`/`vencimiento` NULL.
  *(presupuestos/spec.md:36-39)*
- [ ] 2.3 Same file: `EditarAsync` — full item replace-set under `SELECT … FOR UPDATE … WHERE
  estado = 'borrador'`; `orden` server-assigned 1..N. *(design.md mutation targets 12-14,
  presupuestos/spec.md:52-66)*
- [ ] 2.4 Same file: `EnviarAsync` — `CreateExecutionStrategy` ⇒
  `AsignarComprometidoAsync(db, tenant, pv, "PRES")` in its **own** transaction, then
  `EstrategiaSinReintento` ⇒ `BEGIN UPDATE presupuestos SET numero, fecha_envio, vencimiento,
  estado = 'enviado' WHERE id AND tenant AND estado = 'borrador' AND id_punto_venta = $pv
  RETURNING numero`. 0 rows ⇒ reclassify under a read (409). *(design.md:278-283, mutation
  targets 15-17)*
- [ ] 2.5 Same file: `hoy` resolved via `ParametroConocido.ZonaHoraria` /
  `ResolverZonaAsync`; `vencimiento >= hoy(zona del PV)` required at `enviar`, else
  `400 vencimiento_invalido`. *(design.md:19, mutation targets 18-19)*
- [ ] 2.6 Same file: `presupuesto_sin_items` guard at `enviar` (400 on an empty draft).
  *(mutation target 21)*
- [ ] 2.7 Same file: `AnularAsync` — `borrador`/`enviado` → `anulado`; `convertido` → `409`
  (OD8/T1, decision 4 above). *(presupuestos/spec.md:200-214)*
- [ ] 2.8 Same file: list/detail read model — `ConstruirQuery` with `idPuntoVenta`/`idCliente`/
  `estado`/`vencido`/`desde`/`hasta` filters, `ThenByDescending(p => p.Id)` tiebreaker; derived
  `Vencido`/`Convertible` per row. *(design.md:220-227, mutation target 59, presupuesto half)*
- [ ] 2.9 Same file: `vencido` filter **requires** `idPuntoVenta` (400
  `punto_venta_requerido`); `Vencido` resolved per **distinct** `id_punto_venta` of the page
  (OD9/T5). *(design.md:80)*
- [ ] 2.10 Test: two concurrent `enviar` on **distinct** borrador presupuestos, same PV → two
  distinct numbers, no 409. *(presupuestos/spec.md:84-87, mutation target 15)*
- [ ] 2.11 Test: the assigner call runs **before**, not inside, the `enviar` transaction.
  *(mutation target 16)*
- [ ] 2.12 Test: `AND id_punto_venta = $pv` in the `enviar UPDATE` — a concurrent `PUT` moving
  the PV lands the number in the wrong series if removed. *(mutation target 17)*
- [ ] 2.13 Test: `-03:00` boundary — `RelojFijo(2026-09-30T02:00:00Z)`, PV
  `America/Argentina/Buenos_Aires` ⇒ local `29th`; mirror at `+05:30`. *(mutation target 19,
  `mutation-proof-tests` rule 10)*
- [ ] 2.14 Test: expiry-day boundary — `vencimiento == hoy` ⇒ still convertible (`v < hoy`, not
  `v <= hoy`). *(mutation target 20)*
- [ ] 2.15 Test: empty-quote `enviar` refused (400), draws no number. *(mutation target 21)*
- [ ] 2.16 Test: raw duplicate `ux_presupuestos_numero` through the real writer path →
  translated `numero_de_presupuesto_duplicado`. *(presupuestos/spec.md:95-99)*
- [ ] 2.17 Sibling-seed replace-set test (rule 12c): a second presupuesto of the same tenant,
  with its own items, is asserted intact by exact count and identity after a `PUT` on the
  first. *(mutation target 13)*
- [ ] 2.18 Test: `PUT` on an `enviado` presupuesto → 409 (`borrador`-only mutation).
  *(mutation target 12, presupuestos/spec.md:63-66)*
- [ ] 2.19 Test: request-supplied `orden` is ignored; `ux_items_presupuesto_orden` is reachable
  only out-of-band, race-test exemption documented. *(mutation target 14)*
- [ ] 2.20 [P] Read-model rules 12b/12c: pagination with tied `fecha_emision` (`RelojFijo`) ⇒
  page 2 repeats/skips nothing; each filter proven with asymmetric seeds; every positional
  field of `PresupuestoDetalle` read back with pairwise-distinct values. *(design.md:505,
  mutation target 59, presupuesto half)*
- [ ] 2.21 **GATE GUARD** — zero new files under `Migraciones/`; `has-pending-model-changes`
  clean (schema untouched this slice).
- [ ] 2.22 `judgment-day` round, fix confirmed findings, re-judge to a clean round.
- [ ] 2.23 Open PR #2 `feat/stage17-slice2-presupuestos-abm`, merge after a clean round.

---

## Slice 3: guard + conversión (PR 3)

**Branch**: `feat/stage17-slice3-guard-y-conversion`. **Start**: PR 2 merged. **Finish**: the
`PRE` phantom-sale hole fully closed (both nets, end to end); a quote converts into a sale at its
frozen price, terminal 1:1. **Binding**: this is the slice the DB gate's `ServicioDeVentas`
criterion is exercised against (state.yaml). **Rollback**: the guarded call + resolver clause +
snapshot branch disappear; the checkout reverts to byte-identical pre-stage behavior; schema
untouched.

- [ ] 3.1 Modify `ServicioDeVentas.cs:930` — append `|| !tipo.AfectaStock` to the existing
  boolean chain (**net 2**). No signature change, no new statement, no new error code.
  *(design.md decision 1, mutation target 23)*
- [ ] 3.2 Test: an **out-of-band active**, non-fiscal, venta-class type with
  `afecta_stock = false` → 400, **still RED with `PRE` deactivated** — proves net 2 independent
  of nets 1/1b. *(mutation target 23, comprobantes-venta/spec.md:21-26)*
- [ ] 3.3 Test: **"venta fantasma 400 SIEMPRE"** — `POST /api/ventas` with the seeded, now
  inactive `"PRE"` and real product lines → 400, zero comprobante/stock/CC written (both nets
  together, end to end — closes the gap registered at task 1.40). *(comprobantes-venta/spec.md:
  15-19, state.yaml CRITERIO DE VERIFY VINCULANTE)*
- [ ] 3.4 Modify `SolicitudDeVenta` — `int? IdPresupuestoOrigen`. *(design.md:215-218,
  dto-contract-honesty)*
- [ ] 3.5 Decide phase, `:59` — `lineas := idPresupuestoOrigen is null ? ExigirLineasValidas(...) :
  ExigirSinLineas(...)` — 400 `lineas_no_admitidas` when non-empty and the id is present.
  *(design.md:234, mutation target 36)*
- [ ] 3.6 Decide phase — the snapshot branch (`p1`-`p6`): read presupuesto + items
  `AsNoTracking`, exigir mismo tenant/PV, `hoy` in PV zona, `ReglaDePresupuestos.EsConvertible`
  pre-check (409, **not** the authority), cliente from the quote (conflicting `idCliente` → 400),
  `tipo.Signo <= 0` → 400. *(design.md:238-244)*
- [ ] 3.7 Create `MaterializarItemsDesdePresupuesto` — new **private static**, same file;
  `MaterializarItems` (`:1007-1065`) stays untouched; both call `CalculadorDeTotales.Calcular` as
  the single arithmetic authority. One `id_lista_precio` for the whole document is asserted
  against `items_presupuesto` (`InvalidOperationException` if they disagree — OD9/T3).
  *(design.md decision 3, mutation targets 24-28)*
- [ ] 3.8 Same materializer: `costo_unitario` frozen from **today's** `costo_nominal`, never
  quoting-time. *(design.md decision 4 of the proposal, mutation target 29)*
- [ ] 3.9 Totals-fidelity assertion: recomputed totals == the presupuesto's stored header, else
  `409 presupuesto_inconsistente`. *(design.md:68, mutation target 30 — CONFLICT #4)*
- [ ] 3.10 Create `EscriturasDePresupuesto.cs` — `MarcarConvertidoAsync` (one statement, four
  conjuncts: `estado='enviado'`, `vencimiento >= $hoy`, `id_punto_venta = $pv`, tenant/id) +
  `ExigirCausaDelRechazoAsync` (0-rows reclassification under `FOR UPDATE` into
  404/`409 presupuesto_no_convertible`/`409 presupuesto_vencido`/`409 presupuesto_ya_convertido`/
  `400 punto_venta_no_coincide`). *(design.md:117-129, 152-160)*
- [ ] 3.11 Guarded call at **POSITION 1.5** in `EjecutarTransaccionAsync` — after
  `ExigirTurnoAbiertoBajoLockAsync` (`:773`), before the comprobante `INSERT` (`:781`); the
  `INSERT` itself is not a lock-order position (T10). *(design.md decision 6, mutation targets
  34-35)*
- [ ] 3.12 Comprobante `INSERT` gains `id_presupuesto_origen`. *(design.md:258, mutation
  target 37)*
- [ ] 3.13 `ComprobanteEmitido` gains `int? IdPresupuestoOrigen` (round-trip, OD9/T7).
  *(design.md:215-218)*
- [ ] 3.14 Frozen-price fidelity test — **discriminating fixture**: quoted `precio_unitario=100`,
  `descuento=10` on list A; list moves to `130`, the oferta is deactivated, the alicuota moves
  `21 → 10.5`, the artículo is renamed. Every one of `precio_unitario`/`descuento`/`total`/
  `id_lista_precio`/`id_oferta`/`id_alicuota_iva`/`porcentaje_iva`/`descripcion` asserted.
  *(mutation targets 25-28, mutation-proof-tests rule 11)*
- [ ] 3.15 Cost fidelity test: `costo_unitario` equals **today's** `costo_nominal`, not the
  quoting-time one. *(mutation target 29)*
- [ ] 3.16 Test: an expired quote's conversion is refused (`409 presupuesto_vencido`) at the
  `-03:00` boundary where UTC and local disagree on the day. *(mutation target 32,
  mutation-proof-tests rule 10)*
- [ ] 3.17 Test: convertir × convertir race — one `201` + one `409
  presupuesto_ya_convertido`; the loser writes **nothing** (no comprobante, items, stock, CC)
  and burns a `TX` number (OD9/T6, asserted explicitly). *(mutation target 35,
  presupuestos/spec.md:173-176)*
- [ ] 3.18 Test: cross-punto-de-venta conversion refused, `400 punto_venta_no_coincide`.
  *(mutation target 33 — CONFLICT #3)*
- [ ] 3.19 Test: `lineas_no_admitidas` (non-empty `lineas` + `idPresupuestoOrigen`) and the
  conflicting-`idCliente` refusal. *(mutation target 36)*
- [ ] 3.20 Test: a raw `UPDATE` desyncing `presupuestos.total` from its items → `409
  presupuesto_inconsistente`, never a silently different sale. *(mutation target 30,
  mutation-proof-tests rule 12a)*
- [ ] 3.21 Test: a sale **without** `idPresupuestoOrigen` issues the exact pre-stage command
  count — the *"zero extra statements"* criterion, two networks (stage-16 precedent).
  *(mutation target 34)*
- [ ] 3.22 Test: `ComprobanteEmitido.IdPresupuestoOrigen` round-trip + the unique-index race
  (two concurrent conversions of **different** quotes both succeed with distinct sales).
  *(mutation target 37)*
- [ ] 3.23 **GATE GUARD, criterio del toque a `ServicioDeVentas` (vinculante, state.yaml)** —
  the diff of `ServicioDeVentas.cs` is bounded to exactly: one clause at `:930`, the decide-phase
  snapshot branch + `MaterializarItemsDesdePresupuesto`, one guarded call inside
  `EjecutarTransaccionAsync` at 1.5. The pinned statement order and both loops (stock `:866-885`,
  CC `:890-914`) are byte-identical — verified by diff review, not tests alone.
  *(design.md binding verify criterion 3)*
- [ ] 3.24 [P] Non-regression: `VentasCheckoutTests`/`VentasAnulacionTests`/
  `VentasAtomicidadYConcurrenciaTests` green and **not edited**.
- [ ] 3.25 `judgment-day` round, fix confirmed findings, re-judge to a clean round.
- [ ] 3.26 Open PR #3 `feat/stage17-slice3-guard-y-conversion`, merge after a clean round.

---

## Slice 4: Schema remitos + ALTER TYPE aislado + ramas (PR 4)

**Branch**: `feat/stage17-slice4-schema-remitos`. **Start**: PR 3 merged. **Finish**:
`estado_remito` + both tables + the `movimientos_stock` ALTER exist with standard RLS, **30
cumulative** new indexes, 5 `ManejadorDeErrores` branches proven out-of-band, `motivo_stock`
gains `'remito'`. **Rollback**: `ALTER TABLE movimientos_stock DROP CONSTRAINT
fk_movimientos_stock_remito` → `DROP COLUMN id_remito` → `DROP TABLE items_remito` → `DROP TABLE
remitos` → `DROP TYPE estado_remito` → deactivate `TXR` (never delete). **The `motivo_stock`
value `'remito'` is NOT reverted — irreversible, accepted, registered** (`proposal.md:1097-1099`).
**Budget note**: pre-authorized split `4a`/`4b` (decision 3 above) if this slice overflows.

- [ ] 4.1 Migration `RemitosEtapa17`, **first statement**: `ALTER TYPE motivo_stock ADD VALUE
  'remito'` — named by **nothing else** in this migration (Postgres forbids using a value in
  the transaction that adds it). *(proposal.md:636-643, decision 11, dependency for mutation
  target 39)*
- [ ] 4.2 Same migration: `CREATE TYPE estado_remito AS ENUM
  ('borrador','emitido','facturado','anulado')`. *(proposal.md:626)*
- [ ] 4.3 Same migration: `CREATE TABLE remitos` — 18 columns exactly per §E; `pk_remitos`.
  *(proposal.md:771-791)*
- [ ] 4.4 Same migration: 5 named FKs on `remitos` + `ak_remitos_id_remito_id_tenant`.
  *(proposal.md:796-803)*
- [ ] 4.5 Same migration: `ck_remitos_salida_completa` + `ck_remitos_facturacion` exactly per
  §E's table. *(proposal.md:804-805)*
- [ ] 4.6 Same migration: 7 named indexes — 5 FK-support/listing + `ux_remitos_numero`
  **UNIQUE PARTIAL** — plus the implicit AK index. *(proposal.md:808-819)*
- [ ] 4.7 Same migration: `CREATE TABLE items_remito` — 20 columns exactly per §F, including
  `fk_items_remito_lote` MATCH SIMPLE; `pk_items_remito`. *(proposal.md:826-848, 857-864)*
- [ ] 4.8 Same migration: `ck_items_remito_cantidad_positiva`, `ck_items_remito_costo_no_negativo`,
  `ck_items_remito_estimado_con_costo`. *(proposal.md:865-867)*
- [ ] 4.9 Same migration: 8 named indexes — 7 FK-support + `ux_items_remito_orden` **UNIQUE**.
  *(proposal.md:873-881)*
- [ ] 4.10 Same migration: `ALTER TABLE movimientos_stock ADD COLUMN id_remito integer NULL` +
  `fk_movimientos_stock_remito` composite MATCH SIMPLE + named `ix_movimientos_stock_remito`.
  *(proposal.md:916-928)*
- [ ] 4.11 Same migration, data statement 2: guarded `INSERT` of `TXR` for already-migrated
  databases (`EXISTS`/`NOT EXISTS` guard, `RC`/`C-*` precedent); `Down` statement 3 deactivates
  it (never deletes). *(proposal.md:946-954)*
- [ ] 4.12 Same migration: `HabilitarRlsDeTenant` on both new tables, **LAST**; verify ordering
  matches gate §K exactly. *(proposal.md:1014-1016)*
- [ ] 4.13 Create `src/Ways.Domain/Ventas/EstadoRemito.cs` — 4 values, native type order.
  *(design.md:92)*
- [ ] 4.14 Modify `MotivoStock.cs` — `Remito` declared **LAST**, ninth value, with its
  irreversibility comment. *(design.md:96-97, mutation target 39)*
- [ ] 4.15 Create `Remito.cs` / `ItemRemito.cs` — `EntidadTenant` ⇒ `EntidadBase`.
  *(design.md:440, gate §E-§F)*
- [ ] 4.16 Create `RemitoConfiguration.cs` / `ItemRemitoConfiguration.cs` — every support index
  declared by hand. *(design.md:445)*
- [ ] 4.17 Modify `MovimientoStockConfiguration.cs` — `IdRemito` + FK24 + named
  `ix_movimientos_stock_remito`. *(design.md:447)*
- [ ] 4.18 Modify `WaysDbContext.cs` / `IWaysDbContext.cs` — two new `DbSet`s.
- [ ] 4.19 Modify `WaysDbContextFactory.cs` **and** `DependencyInjection.cs` —
  `MapEnum<EstadoRemito>` in both. *(design.md:449, mutation target 38 family)*
- [ ] 4.20 Modify `InicializadorDeBaseDeDatos.cs` — `TXR` tuple (`clase venta`, `letra 'X'`,
  `signo +1`, `discrimina_iva false`, `es_fiscal false`, `afecta_stock false`, `activo true`).
  *(proposal.md:957-962)*
- [ ] 4.21 Modify `docs/10-modelo-de-datos.md` — `remitos` + `items_remito` tables,
  `movimientos_stock.id_remito`, `TXR` catalog note, "Estado (Etapa 17)" header for remitos
  **OPENED** (closes at slice 8). *(design.md:465)*
- [ ] 4.22 Modify `ManejadorDeErrores.cs` — exact-name `ux_remitos_numero` →
  `numero_de_remito_duplicado`, 409, **ABOVE** `ClasificarUnicidad` — **5th** `_numero`
  ordering-trap occurrence. *(design.md:382)*
- [ ] 4.23 Same file: exact-name `ux_items_remito_orden` → `orden_de_item_duplicado`, 409.
- [ ] 4.24 Same file: exact-name `ck_remitos_salida_completa` → `remito_salida_incompleta`, 409.
- [ ] 4.25 Same file: exact-name `ck_remitos_facturacion` → `remito_facturacion_incoherente`, 409.
- [ ] 4.26 Same file: exact-name `ck_items_remito_cantidad_positiva` →
  `cantidad_de_linea_invalida`, 400. (`ck_items_remito_costo_no_negativo`/
  `ck_items_remito_estimado_con_costo` are generic-mapped, exemption documented — server-derived,
  no client path.)
- [ ] 4.27 [P] RLS test on `remitos`/`items_remito`. *(mutation target 38)*
- [ ] 4.28 [P] Raw-insert CHECK3/CHECK4/CHECK5 tests → `23514` with translated codes.
  *(mutation target 38)*
- [ ] 4.29 `pg_indexes` audit — **cumulative 30** new indexes verified by definition (14 from
  slice 1 + 15 remito-side + 1 `movimientos_stock` support), including that the partial
  `ux_comprobantes_venta_presupuesto_origen` from slice 1 still resolves as the sole covering
  index for FK23. *(design.md binding verify criterion 1)*
- [ ] 4.30 Test: raw duplicate `ux_remitos_numero` → translated `numero_de_remito_duplicado`
  (5th trap). *(mutation target 38)*
- [ ] 4.31 Test: `MotivoStock.Remito` last — every existing `motivo` round-trip test still
  reads the correct value (no shift). *(mutation target 39)*
- [ ] 4.32 Test: the `TXR` guarded `INSERT` (already-migrated DB) and the seed's `TXR` tuple
  (fresh DB) both produce a usable, `afecta_stock = false` row.
- [ ] 4.33 **GATE GUARD** — exactly **two** migration files total across the whole stage
  (`PresupuestosEtapa17` + `RemitosEtapa17`), no third; `has-pending-model-changes` clean.
  *(state.yaml CRITERIO DE VERIFY VINCULANTE)*
- [ ] 4.34 **GATE GUARD** — re-run task 3.3's *"venta fantasma 400 SIEMPRE"* test unchanged and
  still green at this point in the stack — regression check across the schema boundary.
- [ ] 4.35 [P] Non-regression: full stock/lotes suites green and unedited (the
  `movimientos_stock` ALTER is additive-only, metadata-only).
- [ ] 4.36 `judgment-day` round, fix confirmed findings, re-judge to a clean round.
- [ ] 4.37 Open PR #4 `feat/stage17-slice4-schema-remitos`, merge after a clean round.

---

## Slice 5: emisión de remito — el cuarto write site (PR 5)

**Branch**: `feat/stage17-slice5-remito-write-site`. **Start**: PR 4 merged. **Finish**: a
remito emits, resolves FEFO, moves stock through an independently-implemented fourth write
site, and reverses cleanly on annulment (including the double-annulment guard, OD8/T2).
**Rollback**: endpoints/service disappear, schema untouched.

- [ ] 5.1 Create `ContratosDeRemito.cs` — `SolicitudDeRemito`/`LineaDeRemito`.
  *(design.md:204-207)*
- [ ] 5.2 Create `ServicioDeRemitos.cs` — `CrearBorradorAsync`/`EditarAsync` replace-set under
  `FOR UPDATE … WHERE estado = 'borrador'` (mirrors 2.2/2.3).
- [ ] 5.3 Same file: `EmitirAsync` — `AsignarComprometidoAsync(db, tenant, pv, "REM")` in its
  own transaction, then `EstrategiaSinReintento` ⇒ `BEGIN UPDATE remitos SET numero,
  fecha_salida, estado = 'emitido' WHERE ... AND estado = 'borrador' AND id_punto_venta = $pv
  RETURNING numero` (locks the remito). *(design.md:288-291, mutation target 44)*
- [ ] 5.4 FEFO resolution before the transaction opens, **UTC-naive `hoy`** (parity with the
  checkout, deliberately NOT the PV zone — OD9/T4). *(design.md decision 10, mutation target 47)*
- [ ] 5.5 `items_remito` update: freeze `id_lote`, `costo_unitario` (**today's**
  `costo_nominal`), `costo_es_estimado`. *(design.md:292)*
- [ ] 5.6 **The fourth stock write site** — ascending `(id_articulo, id_punto_venta, id_lote
  NULLS FIRST)`, aggregate `stock` upsert **before** `stock_lotes`, one `movimientos_stock`
  `INSERT` per line (`motivo = remito`, `id_remito` set) — implemented **independently**, no
  shared helper with `ServicioDeVentas`. *(design.md:52-56, decision 8, mutation targets 40-42)*
- [ ] 5.7 `remito_sin_items` guard (400 on an empty draft) + `EsProducto` refusal
  (`articulo_no_es_producto`, 400) — removes the checkout's `EsProducto` skip-branch entirely
  for write site 4. *(design.md decision 14, mutation target 43)*
- [ ] 5.8 `AnularAsync` — `borrador`/`emitido` → `anulado`; `facturado` → 409; reads the
  **original** `motivo = remito` movements and inserts their exact inverses (`motivo =
  anulacion`, same `id_remito`, same `id_lote` — no re-derivation), **no negative-balance
  guard** (a remito decrements, its reversal adds — OD9/T8, `ServicioDeVentas.cs:1130-1135`
  posture verbatim). *(design.md decision 9, mutation targets 45-46)*
- [ ] 5.9 **[OD8/T2, spec gap closed]** Same `UPDATE`: `WHERE estado = 'emitido'` additionally
  refuses annulling an **already-`anulado`** remito with `409 remito_ya_anulado` — parity with
  `comprobantes-venta`'s own double-anulación precedent, absent from `remitos/spec.md`'s own
  scenario list.
- [ ] 5.10 List/detail read model for remitos (mirrors 2.8).
- [ ] 5.11 **[OD8/T2]** Test: annulling an already-`anulado` remito → `409
  remito_ya_anulado`, and no second inverse `movimientos_stock` row is ever written.
  *(widens mutation target 46)*
- [ ] 5.12 Test: **remitir × checkout rendezvous** — same artículo/lot, both complete, no
  deadlock — write site 4's own concurrency test. *(mutation targets 40-41,
  stock/spec.md:119-127)*
- [ ] 5.13 Test: **remitir × remitir rendezvous** — same artículo/lot, both complete, no
  deadlock, serialized on `stock`/`stock_lotes`.
- [ ] 5.14 Test: **FEFO parity** — the same two-lot fixture through the checkout and through
  `emitir` picks the **same** lot; an explicit `idLote` is honoured in both.
  *(lotes-y-vencimientos/spec.md:50-61, mutation target 47)*
- [ ] 5.15 Test: **nine-motivo consistency** — `stock.cantidad == SUM(movimientos_stock.cantidad)`
  across a sequence including `remito` and its `anulacion`. *(stock/spec.md:166-171)*
- [ ] 5.16 Test: annulment reads **original** movements, not re-derived from `items_remito` —
  a partially-annulled/soft-deleted fixture diverges if re-derived. *(mutation target 45)*
- [ ] 5.17 Test: non-product line refused (400) + empty remito refused (400).
  *(remitos/spec.md:36-39)*
- [ ] 5.18 Test: double `emitir` (409) / wrong-series test — `WHERE estado = 'borrador' AND
  id_punto_venta = $pv`. *(mutation target 44)*
- [ ] 5.19 [P] Read-model rules 12b/12c for the remito detail/list (mirrors 2.20).
- [ ] 5.20 Seeds: `RelojFijo` mediodía UTC + desynced ids (decision 13 above).
- [ ] 5.21 **GATE GUARD** — zero new files under `Migraciones/`; `has-pending-model-changes`
  clean.
- [ ] 5.22 [P] Non-regression: existing checkout/stock suites green and unedited (write site 4
  is additive-only).
- [ ] 5.23 `judgment-day` round, fix confirmed findings, re-judge to a clean round.
- [ ] 5.24 Open PR #5 `feat/stage17-slice5-remito-write-site`, merge after a clean round.

---

## Slice 6: consolidación TXR (PR 6)

**Branch**: `feat/stage17-slice6-consolidacion`. **Start**: PR 5 merged. **Finish**: N remitos
consolidate into one itemless `TXR`; its annulment un-links them and reverses CC with zero
stock movements, including the OD8/T3 discriminant test. **Budget note**: pre-authorized split
`6a`/`6b` (decision 3 above). **Rollback**: guarded call + service disappear, checkout anulación
reverts to pre-stage.

- [ ] 6.1 Create `EscriturasDeRemito.cs` — `BloquearAscendenteAsync` (`FOR UPDATE ORDER BY
  id_remito`) + `LigarAsync` (guarded N-row `UPDATE`) + `DesligarAsync`. *(design.md:131-149,
  mutation targets 48-50)*
- [ ] 6.2 Create `ServicioDeFacturacionDeRemitos.cs` — pre-tx: load remitos + items, same
  tenant/cliente/PV, all `emitido` and unlinked; `totales := Σ headers` asserted against `Σ
  items`; `ValidadorDePagos`; turno-abierto check.
- [ ] 6.3 Same file: `EstrategiaSinReintento` ⇒ `BEGIN ExigirTurnoAbiertoBajoLockAsync` as
  statement 0 (decision 13 of the proposal — unlike the plain remito, which requires none).
  *(design.md:313, mutation target 54)*
- [ ] 6.4 `BloquearAscendenteAsync` **before** the comprobante `INSERT` and before `clientes` —
  the `INSERT` is not a lock-order position (T10). *(design.md decision 12, mutation target 49)*
- [ ] 6.5 Itemless `TXR` comprobante `INSERT` — `RC` precedent, **zero items by construction**.
  *(design.md decision 5, mutation target 52)*
- [ ] 6.6 Pagos + cuenta corriente via the existing `EscriturasDeCuentaCorriente` (unchanged).
  *(design.md:317)*
- [ ] 6.7 **Credit-limit backstop re-implemented inside the transaction** (parity with
  `ServicioDeVentas.cs:901-908` — OD9/T9). *(design.md decision 13, mutation target 53)*
- [ ] 6.8 `LigarAsync` — filas == N or `409 remito_no_facturable` (CONFLICT #4).
  *(design.md:318, mutation target 50)*
- [ ] 6.9 `AsignadorDeNumeroComprobante` reused with `'TXR'`. *(design.md:311)*
- [ ] 6.10 Widen `MarcarAnuladoAsync`'s `RETURNING` with a scalar subquery — `(SELECT t.codigo
  FROM tipos_comprobante t WHERE t.id_tipo_comprobante = comprobantes_venta.id_tipo_comprobante)
  AS codigo_tipo`. *(design.md decision 7, mutation target 55)*
- [ ] 6.11 Guarded call at **POSITION 1.6** in `EjecutarAnulacionAsync` — `if (codigoTipo ==
  "TXR")` ⇒ `EscriturasDeRemito.DesligarAsync`, never after the CC loop. *(design.md decision
  4/7, mutation targets 56, 58)*
- [ ] 6.12 `DesligarAsync` — clears `estado` **and** `id_comprobante_venta` **together**
  (`ck_remitos_facturacion`). *(design.md:146-148, mutation target 57)*
- [ ] 6.13 Test: consolidating two remitos emits **one** itemless `TXR`, total == Σ frozen
  lines, **zero** `movimientos_stock` rows. *(remitos/spec.md:135-139, mutation target 52)*
- [ ] 6.14 Test: **facturar × facturar** race over overlapping sets — exactly one 201 + one
  409, ascending lock order. *(mutation targets 48, 50; remitos/spec.md:141-145)*
- [ ] 6.15 Test: **facturar × anular-remito** race, both orders. *(mutation target 49)*
- [ ] 6.16 Test: mixed-customer / mixed-PV / already-invoiced set refused 409 before any write.
  *(mutation target 51)*
- [ ] 6.17 Test: credit-limit exceeded by a **concurrent** sale between pre-check and commit →
  400. *(mutation target 53)*
- [ ] 6.18 Test: closed-turno 409 for the consolidation; deliberate **absence** of that
  requirement for plain `emitir` (decision 13, both directions asserted). *(mutation target 54)*
- [ ] 6.19 Test: an **ordinary** anulación (non-`TXR`) issues the exact pre-stage command
  count. *(mutation targets 55-56)*
- [ ] 6.20 Test: annulling a `TXR` returns its remitos to `emitido`, clears
  `id_comprobante_venta`, reverses CC, **zero** stock movements — the double-decrement and
  phantom-restock traps proven unreachable. *(comprobantes-venta/spec.md:79-83, mutation
  target 52)*
- [ ] 6.21 **[OD8/T3, discriminant test]** TXR-anulación composition: a `TXR` whose original
  consolidation used cuenta corriente, annulled — the test asserts **both** halves together in
  one transaction: (a) zero `movimientos_stock` rows created, AND (b) the CC balance reversed by
  the **exact** original amount. Proves the composition is not "plausible by construction"
  (state.yaml OD8/T3, stage-16-slice-3 lesson).
- [ ] 6.22 Test: `ck_remitos_facturacion` — `DesligarAsync` clearing only one of the two
  columns → `23514`. *(mutation target 57)*
- [ ] 6.23 Test: **anular-TXR × facturar** race — whoever takes `comprobantes_venta`/`remitos`
  first wins, no cycle (T10). *(mutation target 58)*
- [ ] 6.24 [P] Non-regression: existing anulación suites green and not edited beyond the one
  guarded call.
- [ ] 6.25 `judgment-day` round, fix confirmed findings, re-judge to a clean round.
- [ ] 6.26 Open PR #6 `feat/stage17-slice6-consolidacion`, merge after a clean round.

---

## Slice 7: web presupuestos + POS banner (PR 7)

**Branch**: `feat/stage17-slice7-web-presupuestos`. **Start**: PR 3 merged (does not need
slices 4-6). **Finish**: quote list/detail/draft + the POS conversion entry point.
**Rollback**: screens/branch disappear, API still serves the shape.

- [ ] 7.1 Create `src/Ways.Web/src/api/presupuestos.ts` — client + pure mappers.
- [ ] 7.2 Create `Presupuestos.tsx` — list, filters (PV/cliente/estado/`vencido`/desde-hasta),
  `HistoricoDeCajas.tsx` pager pattern, `vencido` toggle disabled without a PV.
  *(design.md:399-403)*
- [ ] 7.3 Create `Presupuesto.tsx` — draft editor (`CompraEditor.tsx` line grid) + detail +
  expiry state + `enviar` (date input defaulted `hoy + 30` in PV zone) + `anular` +
  *"Convertir en venta"* (rendered only when `Convertible`). *(design.md:404-407)*
- [ ] 7.4 Modify `Pos.tsx` — read `idPresupuesto` from `useSearchParams`, fetch `/para-venta`,
  render the frozen-price banner, hydrate the cart **read-only**, **skip the price-resolution
  effect entirely**, disable scan/quantity/removal, post `{ idPuntoVenta,
  codigoTipoComprobante: 'TX', idPresupuestoOrigen, lineas: undefined, pagos }`,
  `key={idPresupuesto ?? 'libre'}`. *(design.md:408-415, react-async-state rule 8)*
- [ ] 7.5 Modify `App.tsx` — routes `/presupuestos`, `/presupuestos/nuevo`,
  `/presupuestos/:id`.
- [ ] 7.6 Descriptor tests for every new pure helper (expiry-badge formatter, filter builder)
  and every screen's descriptors. *(web-descriptor-tests)*
- [ ] 7.7 Test: no price-resolution request issued under `?idPresupuesto=`, no `lineas`
  posted, cart inputs disabled. *(design.md:508)*
- [ ] 7.8 Test: a non-convertible quote renders no "Convertir" action.
- [ ] 7.9 Test: double click on `enviar` issues exactly ONE POST (rule 9 re-entrancy + disable).
  *(design.md:424-425)*
- [ ] 7.10 Test: stale promise resolved **inside `act`** (rule 7). *(mutation-proof-tests
  rule 7)*
- [ ] 7.11 Test: `vencido` toggle disabled without `idPuntoVenta`; pager disabled at edges.
- [ ] 7.12 Rule 10: any recovery path added is grepped for and replicated in sibling screens in
  the same commit.
- [ ] 7.13 [P] Non-regression: existing `Pos.test.tsx` green (only the new branch added).
- [ ] 7.14 `judgment-day` round, fix confirmed findings, re-judge to a clean round.
- [ ] 7.15 Open PR #7 `feat/stage17-slice7-web-presupuestos`, merge after a clean round.

---

## Slice 8: web remitos + consolidación + refresco del Estado header de doc-10 (PR 8)

**Branch**: `feat/stage17-slice8-web-remitos`. **Start**: PR 6 merged. **Finish**: the whole
remito/consolidación circuit has a UI; doc-10's "Estado (Etapa 17)" headers close the stage.
**Budget note**: pre-authorized degradation — drop `FacturarRemitos.tsx`'s bulk selection
(one remito at a time) if this slice overflows. **Rollback**: screens disappear, API still
serves the shape.

- [ ] 8.1 Create `src/Ways.Web/src/api/remitos.ts` — client + mappers.
- [ ] 8.2 Create `Remitos.tsx` — list + filters (mirrors 7.2).
- [ ] 8.3 Create `Remito.tsx` — draft/detail + `emitir` (`SelectorDeLote` reuse) + `anular`;
  `facturado` renders its invoice link and no actions. *(design.md:416-418)*
- [ ] 8.4 Create `FacturarRemitos.tsx` — cliente + PV picker, `emitido` unlinked list,
  multi-select, summed total, POS payment rows, post the consolidation. *(design.md:419-421)*
- [ ] 8.5 Modify `App.tsx` — routes `/remitos`, `/remitos/nuevo`, `/remitos/:id`,
  `/remitos/facturacion`.
- [ ] 8.6 **[EXPLICIT, new programme rule]** Modify `docs/10-modelo-de-datos.md` — refresh the
  "Estado (Etapa 17)" headers opened at tasks 1.20/4.21 to *"implementada — etapa completa
  (PRs #1-#8)"* — this is the **last** slice; the header must never claim *"implementada"*
  while a write path is still unmerged. *(design.md:465, 600-603 — the stage-16 W1 verify
  remediation, codified forward as a mandatory task instead of a carryover risk)*
- [ ] 8.7 Descriptor tests for every new screen + pure helper (consolidation total reducer).
- [ ] 8.8 Multi-select reducer test.
- [ ] 8.9 Disabled-action matrix by `estado` (`borrador`/`emitido`/`facturado`/`anulado`).
- [ ] 8.10 Test: double click on `emitir`/`anular`/`facturar` issues exactly one POST each.
- [ ] 8.11 **STAGE CLOSE** — full solution test suite run once end-to-end
  (`dotnet test`, no filter) — confirms non-regression across the whole tree.
- [ ] 8.12 **STAGE CLOSE** — full web suite end-to-end (`npx vitest run`, no filter),
  `npm run build` clean, `npm run lint` clean.
- [ ] 8.13 **STAGE CLOSE** — re-verify design.md's binding verify criteria 1-9 against the
  merged stack. *(design.md:628-654)*
- [ ] 8.14 `judgment-day` round, fix confirmed findings, re-judge to a clean round.
- [ ] 8.15 Open PR #8 `feat/stage17-slice8-web-remitos`, merge after a clean round — **stage
  close**.

---

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | ~3 880-4 000 naive; calibrated 1.5-3× per stages 13-16's own record (races, SQLSTATE assertions, rendezvous tests, descriptor tests inflate every slice) |
| 400-line budget risk | **High** — all eight slices sit above the cap on the estimate alone |
| Chained PRs recommended | Yes |
| Suggested split | PR 1 → PR 2 → PR 3 → PR 4 → PR 5 → PR 6 → PR 7 → PR 8, with four pre-authorized cut points (`1a/1b`, `4a/4b`, `6a/6b`, web degradation) |
| Delivery strategy | auto-chain |
| Chain strategy | stacked-to-main |

```text
Decision needed before apply: No
Chained PRs recommended: Yes
Chain strategy: stacked-to-main
400-line budget risk: High
```

`auto-chain` + `stacked-to-main` already resolved in `state.yaml`; no further decision needed
before `sdd-apply`. A **10-12 PR outturn is the expected case**, not the exception — this is the
largest stage of the programme before Etapa 19. `size:exception` anticipated: **No** — the four
pre-authorized splits absorb it.

---

## Summary

**8 slices**, stacked-to-main, merge order `1 → 2 → 3 → 4 → 5 → 6 → 7 → 8`; tracks 1-3
(presupuestos) and 4-6 (remitos) are independent and may interleave. **60 mutation targets**
placed exactly once per design's own Slice column; **30 new indexes** verified by definition,
split 14 (slice 1) + 16 (slice 4); **2 migrations** total, gate-guarded at both schema slices;
**12 `ManejadorDeErrores` branches** split 7/5 across the two schema slices, including the 4th
and 5th occurrences of the `_numero` ordering trap.

**Spec/design conflicts reconciled**: OD8/T1 (anular-convertido terminal, ratified) needed no
task-level change; **OD8/T2** (remito double-anulación scenario, missing from spec) closed by
tasks 5.9/5.11; **OD8/T3** (TXR-anulación composition discriminant test, required by state.yaml)
closed by task 6.21; **OD9's twelve tensions** all ratified in favor of `design`, task-mapped in
decision 7 above.

**New conflicts found this phase, not covered by OD8/OD9, reported and reconciled**:
**CONFLICT #3** — the conversion's `id_punto_venta` agreement (`400 punto_venta_no_coincide`) is
named by `design` but absent from `presupuestos/spec.md`'s prose; resolved in favor of `design`
(task 3.18), same pattern stage-16's CONFLICT #2 established. **CONFLICT #4** — several domain
codes (`remito_no_facturable`, `presupuesto_inconsistente`, `remito_sin_items`,
`articulo_no_es_producto`, `cantidad_de_linea_invalida`) are named by `design`/backstop tables
but left unnamed by the specs; adopted verbatim, no residual ambiguity. No other conflicts were
found: every other design decision restates the proposal's gate contract line-by-line, or is one
of the twelve OD9 tensions already ratified.
