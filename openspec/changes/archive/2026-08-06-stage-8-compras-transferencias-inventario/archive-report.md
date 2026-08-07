# Archive Report: Stage 8 — Comprobantes de Compra, Transferencias e Inventario

**Change**: `stage-8-compras-transferencias-inventario`
**Archived**: 2026-08-06
**Archived to**: `openspec/changes/archive/2026-08-06-stage-8-compras-transferencias-inventario/`
**Artifact store**: openspec
**Execution mode**: autonomous (binding for every phase — the user delegated all decisions, including the DB Change Gate, to the orchestrator)

## THIS IS THE FINAL ARCHIVE OF THE DOC-10 LEGACY-PARITY PROGRAM

Etapa 8 is the last row of doc 10's stage table (doc-10:639). With this archive, all 8 stages of
`docs/10-modelo-de-datos.md` are implemented, verified and archived. **There is no etapa 9.** Any
residual work identified during this stage (full-count inventory, a real proveedores current
account, órdenes de compra, libro IVA compras) becomes a normal post-parity change, not a
scheduled stage.

## Summary

Stage 8 closed the last three inbound gaps in the rewrite: goods never entered the system
(legacy C3 never persisted, doc-01:203-208), three `motivo_stock` values were reserved with no
writer, and moving stock between locales was manual folklore (doc-10:472-474). It shipped:

- `comprobantes_compra` + `items_comprobante_compra` with the `borrador → confirmada → anulada`
  lifecycle, `numero_externo` identity/dedupe, the one-transaction confirm (stock entry +
  `costo_nominal` overwrite + cache + `precio_sugerido`), and anulación by contramovimientos with
  a negative-stock refusal.
- Transferencias entre locales: one atomic transaction, two mirrored `movimientos_stock` rows, a
  deliberate back-office tightening that refuses to go negative (the opposite of the checkout
  rule).
- A minimal conteo de inventario: the counted total as input, the server derives the signed delta
  under the stock row lock, distinct from `ajuste` for traceability.
- `gastos.id_comprobante_compra` (the deferred FK, finally landed) and a derived, embryonic
  proveedor saldo read (`Σ compras confirmadas − Σ gastos`), with per-compra payment status.
- Three platform-seeded compra-clase `tipos_comprobante` rows (`C-FA`/`C-FB`/`C-FC`), prefixed to
  never shadow a venta code.

**Delivery**: 6 PR slices + 2 micro-PRs, all merged (PRs #68–#74), chained stacked-to-main per
`protocolo-pr-solo-dev`. Final suites: **Domain 378, Application 212, Integration 691, vitest
414**. 121 dedicated stage-8 integration cases.

**Judgment-day totals**: Slice 1 — both judges CLEAN round 1 (1 MINOR deferred to slice 2). Slice
2 (the compra lifecycle centerpiece) got its own **dedicated** judgment-day round — round 1 found
**2 BLOCKERs, same root cause** (a narrowed `RETURNING` broke the binding pseudocode: pre-lock
`discriminaIva` corrupted `costo` under a tipo-switch race, and unvalidated completeness fell to a
bare 500), fixed by restoring the guard **inside** the `WHERE` clause (ADR-16); round 2 both
judges CLEAN, with the original design pseudocode itself adjudicated unimplementable-as-written.
Slice 3 — standard round, closed clean. Slice 4 — judge A CLEAN + a doc nit, judge B 1 MAJOR
coverage gap (test-only fix), round 2 orchestrator-verified clean. Slice 5 (web compras) —
**3 rounds**: round 1 found 2 BLOCKERs + 1 MAJOR, round 2 found a residual inner-button gate,
round 3 closed with a prescribed 2-line fix, triple mutation evidence throughout. Slice 6 (the
final slice) — round 1 found three convergent MAJORs + minors; **round 2 both judges CLEAN**
(3 remaining items were coverage-only minors, verified correct by inspection).

**Greenfield delivered**: unlike stages 5–7, none of the three features (compras, transferencias,
inventario) had a working legacy precedent to port — legacy C3 "never persisted"
(doc-01:203-208), transferencias were "a mano y que Dios ayude" (doc-10:472-474), and inventario
had no legacy surface at all beyond a read-only dashboard (doc-01:341-347). Every rule in this
stage was a **decision**, not a port — "Superar al legacy" (doc-10:639) was delivered in full: an
inbound ledger with no silent-repricing, no negative-inventing back-office writes, and no
D3/D7-class bug reachable by construction.

## THE AUTONOMOUS DECISION LOG

The user delegated **all** decisions for this change to the orchestrator, including the DB Change
Gate. Every decision below carries provenance (a doc-10 line reference or a verified code
citation) and was recorded in `proposal.md`'s decision ledger and in `state.yaml` for this final
summary.

### Gate self-approval

The DB Change Gate was exercised by the orchestrator under the user's standing autonomous
delegation ("si hay que hacer modificaciones en la estructura de la db aprobalas vos... maneja
todo autonomo"). The gate step still happened — a full model summary was presented and evaluated
in `proposal.md` — it simply did not wait for the user. **Approved model**: two new `[operativa]`
tables (`comprobantes_compra`, `items_comprobante_compra`), the `estado_compra` enum, the two
deferred FK columns landing together (`movimientos_stock.id_comprobante_compra`,
`gastos.id_comprobante_compra`), two beyond-doc-10 additions (`id_articulo NOT NULL` on compra
items; `ck_comprobantes_compra_confirmada_completa`), and the C-prefixed compra-tipo seed.
**Conditions**: dual-path seed with the `AND EXISTS` fresh-DB guard proven on both a fresh and a
stage-7-migrated database; compra codes globally unique against every venta code with a
resolution test; the dedupe unique tenant-scoped and partial; every new constraint with a
`db-error-backstops` mapping plus a race test. All four conditions were met and proven by test.

### The 11 proposal decisions

1. **Scope — the one autonomous scope call, flagged for this summary.** Compras + transferencias
   + a **minimal** inventario. The stage table names only "Comprobantes de compra + transferencias
   de stock" (doc-10:639), but `motivo_stock` reserves `inventario` (doc-10:449-450) and this is
   the last stage — leaving one enum value writer-less would spawn a phantom stage 9. Shipped: a
   per-articulo conteo (`motivo = inventario`, distinct from `ajuste`). Not shipped: any
   full-count snapshot/freeze/variance workflow.
2. **Compra lifecycle exactly per doc-10:401-404**: `borrador` (no ledger effect, the one
   deliberate mutable-document exception) → `confirmar` (one transaction) → `anulada`
   (contramovimientos). Confirmada and anulada are immutable.
3. **`precio_sugerido` is a suggestion, never auto-applied.** Computed via the existing pure
   `SugeridorDePrecio`; applying it is a separate explicit action through
   `ServicioDePrecios.AbrirNuevoPrecioAsync`. Never inside the confirm transaction.
4. **`numero_externo` is the identity; there is no correlativo propio** (doc-10:374-375).
   `NumeracionComprobante` is never involved.
5. **Transferencia = one transaction, two mirrored movements**, with a **verified correction to
   the incoming brief**: the insufficient-origin-stock refusal is NOT "the same rule class as
   venta" — it is the opposite. `stock/spec.md` states checkout availability is not checked
   (legacy parity); refusing a transfer is a deliberate tightening under the principle **counter
   operations never block on stock; back-office stock-reducing operations do**.
6. **Proveedor cuenta corriente stays embrionaria — flagged for this summary.** doc-10:408-409
   verbatim forbids an extra table. No movement table, no saldo cache, no imputación, no aging —
   the saldo is a derived read, explicitly documented as an approximation, not an invariant. This
   is a **deliberate departure** from stage 7's richer-ledger precedent (`movimientos_cuenta_corriente`
   with `saldo_resultante` snapshots).
7. **Compra-clase `tipos_comprobante` are platform-seeded with prefixed codes** (`C-FA`/`C-FB`/
   `C-FC`) — the key gate finding: `ux_tipos_comprobante_codigo` is unique on `codigo` alone and
   `ResolverTipoComprobanteAsync` resolves by codigo alone, so an unprefixed compra code could
   shadow a venta code and break the sale path.
8. **The two deferred FKs land together, in this stage's migration** — scheduled debt (doc-10:451
   /457-465 for stock, doc-10:416/426-434 for gastos) being paid, not new design.
9. **Sum-invariant discipline: every stock writer is append-only ledger + same-transaction cache
   upsert. No endpoint accepts a client-supplied delta.** The conteo supplies the counted total;
   the server derives the delta under the row lock. This is what keeps the legacy D3 restore bug
   (doc-01:241) and the D7 bug class unrepresentable.
10. **Anulación of a confirmed compra**: contramovimientos, refused with a `409` when it would go
    negative (deliberate asymmetry — a sale may go negative, an anulación may not),
    `costo_nominal` not reverted.
11. **Authorization mirrors the manual stock writer precedent**: compra write paths, transferencias
    and conteos stack `GestionDeCatalogo` over `OperacionDePos` (Admin-only) — the verified shape
    of the existing `POST /api/stock/ajustes`. Reads (compra list, detail, proveedor saldo) stay
    on `OperacionDePos`.

### Judge-driven design corrections

- **The unimplementable CONFIRMAR pseudocode → WHERE-guard.** Slice 2's dedicated judgment-day
  round 1 found two BLOCKERs sharing one root cause: the design's narrowed `RETURNING` on the
  estado-guarded `UPDATE` broke the binding pseudocode two ways — a pre-lock `discriminaIva` read
  could corrupt `costo` under a concurrent tipo-switch race, and unvalidated completeness fell
  through to a bare 500 instead of a mapped business error. Fixed by moving the completeness guard
  **into the `UPDATE`'s `WHERE` clause** (ADR-16), with double mutation evidence. Round 2: both
  judges CLEAN, and the original design pseudocode was itself adjudicated unimplementable-as-
  written (a CHECK constraint would have fired mid-`UPDATE`) — the deviation is documented in
  `design.md`'s transaction block and in `tasks.md` 2.5.
- **`ResultadoConteo` contract.** The conteo response evolved mid-stage (micro-PR #73) to
  `ResultadoConteo(IdPuntoVenta, IdArticulo, Cantidad, CantidadAnterior, Delta,
  MovimientoRegistrado)` — the server's write-time truth computed under the row lock, so the web
  client never derives ledger claims from a stale pre-fetch. Documented in `design.md`'s
  Response-DTO note (added at verify).
- **Three sibling-replication fixes → rule 10 broadened.** Slice 6 found the incomplete-line
  pattern and the `stock_insuficiente_*` recovery copy needed replication across
  `CompraEditor.tsx`, `Transferencias.tsx` and `ConteoDeInventario.tsx` — the third occurrence of
  the sibling-surface class. `react-async-state` rule 10 ("sibling surfaces") was **broadened**
  from a copy-replication rule to any correctness pattern that recurs across write screens.

## Verify Result

**PASS WITH WARNINGS** (2026-08-06, main `371be17`). 0 CRITICAL. 3 WARNINGS — all documentation
drift, **reconciled pre-archive**: the design's anulación code annotated SUPERSEDED (shipped as
`compra_anulacion_stock_negativo` per spec — spec name canonical), `tasks.md` 5.4's stale nav-role
note corrected, and the response-DTO note added to `design.md`. 4 SUGGESTIONS (coverage minors,
moved to the backlog below). All suites re-run live and exact: Domain 378, Application 212,
Integration 691, vitest 414 — 121 dedicated stage-8 integration cases. Every success criterion in
`proposal.md` was evidenced; the lock-order matrix is cycle-free across every new writer; the
arqueo derivation was verified byte-untouched via `git log`; doc-10's stage table rows 1–8 are all
closed. Verdict: ready to archive — closing the doc-10 legacy-parity program.

## PROGRAM CLOSE-OUT

`docs/10-modelo-de-datos.md`'s legacy-parity program is now **fully archived**, all 8 stages:

| Stage | Change name | Archive folder |
|---|---|---|
| 1 | `stage-1-organization-and-catalogs` | `openspec/changes/archive/2026-08-02-stage-1-organization-and-catalogs/` |
| 2 | `stage-2-clientes-proveedores` | `openspec/changes/archive/2026-08-02-stage-2-clientes-proveedores/` |
| 3 | `stage-3-articulos-y-precios` | `openspec/changes/archive/2026-08-03-stage-3-articulos-y-precios/` |
| 4 | `stage-4-ofertas` | `openspec/changes/archive/2026-08-04-stage-4-ofertas/` |
| 5 | `stage-5-pos-ventas` | `openspec/changes/archive/2026-08-04-stage-5-pos-ventas/` |
| 6 | `stage-6-turnos-caja` | `openspec/changes/archive/2026-08-05-stage-6-turnos-caja/` |
| 7 | `stage-7-cuenta-corriente` | `openspec/changes/archive/2026-08-05-stage-7-cuenta-corriente/` |
| 8 | `stage-8-compras-transferencias-inventario` | `openspec/changes/archive/2026-08-06-stage-8-compras-transferencias-inventario/` (this archive) |

**There is no etapa 9 in doc 10.** The rewrite no longer has a "pending model."

### Standing product decisions awaiting Pablo

- **Cierre role tightening option** (carried from stage 6): whether to tighten the cierre-de-turno
  role gate beyond the current `OperacionDePos` posture — flagged, not decided, in
  `operacion-de-pos/spec.md`'s cierre scenario note.
- **Vendedor UI reachability of the estado de cuenta screen** (the stage-7 product flag): the
  backend read policy (`OperacionDePos`) already admits Vendedor, but the web nav entry point
  decision is still open. Stage 8 was explicit about **not** recreating this class of mismatch —
  every stage-8 write screen is Admin-only end to end, matching its nav (decision 11's consistency
  note) — but the stage-7 instance itself remains unresolved and is carried forward.

### Backlog (from this stage's verify SUGGESTIONS and deferred items)

1. **4 coverage suggestions from this verify** (non-blocking, minor test-coverage gaps identified
   during the verify pass — see the verify session detail for exact locations; none affect
   correctness).
2. **Recargo por medio dormant** — carried from earlier stages, still unactivated.
3. **`articulos_empresas` replace-set concurrency chip** — the `ServicioDeArticulos` open wound
   spawned during stage-4 slice 2, still unrelated to and untouched by stage 8 (explicitly called
   out as out-of-scope in the proposal).
4. **Proveedor CC full-ledger** as a possible future change — decision 6 deliberately kept the
   proveedor account embryonic (derived read only, no table); a real ledger with imputación and
   aging, mirroring stage 7's client-side richer ledger, is a candidate post-parity change, not a
   gap.
5. **The PascalCase `detalle` serializer quirk** — a pre-existing minor naming inconsistency noted
   during this stage's work, non-blocking, carried forward for a future cleanup pass.

## Specs Synced

| Domain | Action | Details |
|---|---|---|
| `comprobantes-compra` | Created | New capability spec, 7 requirements, 21 scenarios |
| `transferencias-de-stock` | Created | New capability spec, 5 requirements, 12 scenarios |
| `conteo-de-inventario` | Created | New capability spec, 5 requirements, 12 scenarios |
| `saldo-de-proveedor` | Created | New capability spec, 4 requirements, 10 scenarios |
| `stock` | Modified | Purpose sentence removed (manual fix per delta note); 2 requirements MODIFIED (`Stock Schema At Rest`, `Cantidad Is Always The Sum Of Its Movimientos`) — added text + 1 new scenario each; 4 other requirements preserved byte-for-byte |
| `gastos` | Modified | Purpose clause resolved (manual fix per delta note); 1 requirement MODIFIED (`Gasto Schema At Rest`) — added `id_comprobante_compra` + 1 new scenario; 1 requirement ADDED (`A Comprobante Compra Link Requires Categoria Proveedor`); 4 other requirements preserved byte-for-byte |
| `arqueo-de-cierre` | Modified | 1 requirement ADDED (`A Proveedor Gasto Linked To A Compra Introduces No New Derivation Term`); 5 existing requirements preserved byte-for-byte |
| `proveedores` | Modified | 2 requirements ADDED (`Proveedor Referenced By A Comprobante Compra Cannot Be Removed`, `Proveedor Saldo Read Entry Point`); 4 existing requirements preserved byte-for-byte |
| `operacion-de-pos` | Modified | 1 requirement ADDED (`Compra, Transferencia And Conteo Write Paths Stack GestionDeCatalogo Over OperacionDePos`); 6 existing requirements preserved byte-for-byte |

## Archive Contents

- `proposal.md` ✅ (verbatim copy)
- `design.md` ✅ (verbatim copy)
- `tasks.md` ✅ (verbatim copy, all 6 slices' tasks checked `[x]`)
- `state.yaml` ✅ (archive-scoped: `phase: archive`, `status: done`, `archive.status: done` with a
  one-line close-out note)
- `specs/` ✅ (9 files, verbatim copies of the change's delta/new specs — the source of what was
  merged)
- `archive-report.md` ✅ (this file)

## Source Of Truth Updated

The following specs now reflect stage 8's behavior:

- `openspec/specs/comprobantes-compra/spec.md` (new)
- `openspec/specs/transferencias-de-stock/spec.md` (new)
- `openspec/specs/conteo-de-inventario/spec.md` (new)
- `openspec/specs/saldo-de-proveedor/spec.md` (new)
- `openspec/specs/stock/spec.md` (merged)
- `openspec/specs/gastos/spec.md` (merged)
- `openspec/specs/arqueo-de-cierre/spec.md` (merged)
- `openspec/specs/proveedores/spec.md` (merged)
- `openspec/specs/operacion-de-pos/spec.md` (merged)

## Deferred / Follow-ups

Per the verify report's suggestions and this stage's own deferred/adjacent list:

- 4 coverage suggestions from verify (non-blocking, test-coverage minors).
- Full-count inventory workflow (snapshot/freeze/session/variance) — future change, decision 1
  scope boundary.
- A real proveedores current account with its own movement ledger — future change, decision 6
  scope boundary, doc-10:409.
- Órdenes de compra / pedidos a proveedor, recepción parcial, notas de crédito de proveedor —
  explicitly out of scope.
- Libro IVA compras / crédito fiscal / FE — compra tipos are `es_fiscal = false`.
- `ServicioDeArticulos` `articulos_empresas` replace-set concurrency gap — carried since stage 4,
  unrelated to stage 8.
- Vendedor UI reachability of the estado de cuenta screen — the stage-7 product flag, still
  awaiting Pablo's navigation decision.
- Recargo por medio — dormant, carried from earlier stages.
- PascalCase `detalle` serializer quirk — minor cleanup candidate.

## SDD Cycle Complete

The change has been fully planned, implemented, verified and archived. This closes the doc-10
legacy-parity program in its entirety — there is no next stage to continue with.

## Engram Note

This executor's toolset for this archive run was Read/Write/Edit/Glob only (no `mem_*` tools
available). Per the skill contract, Engram persistence is skipped silently; `openspec/` remains the
single source of truth for this change, consistent with stage-4/5/6/7 precedent recorded in this
change's own `state.yaml` notes.
