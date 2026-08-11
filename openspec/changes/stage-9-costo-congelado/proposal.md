# Proposal: Stage 9 — Costo congelado en la línea de venta

## Intent

Implement Etapa 9 of `docs/11-programa-post-paridad.md` (doc-11:76-96) — freeze the article cost
on every sale line at emission time.

`items_comprobante_venta` already freezes everything the ticket needs to be reprinted honestly:
`descripcion`, `codigo_barra`, `id_area`, `precio_unitario`, `id_lista_precio`, `id_oferta`,
`id_alicuota_iva`/`porcentaje_iva` (`ItemComprobanteVenta.cs:14-19`, spec `comprobantes-venta` /
*Snapshot Immutability of Items*). It freezes **no cost**.

The only cost the system has is `articulos.costo_nominal`: a single mutable value, overwritten by
every confirmed compra (`ServicioDeCompras` → `CalculadorDeCompra.ResolverActualizacionesDeCosto`,
doc-10:408), with **no history at all** — stage 8 explicitly decided the compra anulación does not
even revert it (stage-8 decision 10c). So the margin of a sale stops being reconstructible the
moment the next purchase of that article is confirmed.

This is the one stage in the whole post-parity program whose postponement **destroys data**
(doc-11:84-88). Every day of delay is another day of sales whose profitability is permanently
unknowable. Stage 10 (aggregation + dashboard) needs this dimension to exist; nothing else in the
program does.

## Scope

### In Scope

- Two new columns on `items_comprobante_venta`: `costo_unitario numeric(14,2) NULL` and
  `costo_es_estimado boolean NOT NULL DEFAULT false`, with their CHECKs. One migration.
- Snapshot capture at emission inside `ServicioDeVentas` (`MaterializarItems` →
  `LineaDelPlan` → `EjecutarTransaccionAsync`). **Zero extra queries**: `articuloPorId`
  (`ServicioDeVentas.cs:96-98`) already materializes the `Articulo`, `CostoNominal` included.
- Same rule for NCX (nota de crédito) lines — see decision 3.
- One-shot best-effort backfill of existing rows, inside the same migration, marked
  `costo_es_estimado = true` (decision 2, and the RLS trap in decision 6).
- Domain doc-comment (`ItemComprobanteVenta.cs`), spec delta, tests (domain/integration), and the
  doc-10 §4 schema note.

### Out of Scope

- **Everything from stage 10**: margin aggregation, dashboards, reports, comparisons. This stage
  only lands the datum.
- Exposing the cost in any API response, ticket or POS screen (decision 5).
- Per-article / per-proveedor cost history, weighted average cost, FIFO/LIFO valuation.
- Reconstructing real historical costs from `items_comprobante_compra` (see *Deferred*).
- Any change to how compras writes `costo_nominal`, to the sale transaction's shape, to
  numbering, stock, cuenta corriente, anulación or authorization.
- Multi-currency costs — the system has exactly one currency today.

## Capabilities

### New Capabilities

- None.

### Modified Capabilities

- `comprobantes-venta`: *Comprobante Schema At Rest* gains the two columns; *Snapshot Immutability
  of Items* gains `costo_unitario`/`costo_es_estimado` to its frozen list; one new requirement
  pinning the three-state cost semantics, the NCX rule, the unsigned-per-unit convention, and the
  no-exposure rule.

## Approach

One column pair, one capture point, one backfill. The capture rides the data the checkout already
has in hand, so the sale transaction gains **no query, no lock and no failure mode** — the guarded
path stays guarded (stage-8 discipline: add next to `ServicioDeVentas`, never rewrite it).

Cost lands as a *third kind of value*, not a number that is always there:

| `costo_unitario` | `costo_es_estimado` | Meaning |
|---|---|---|
| `NOT NULL` | `false` | Real snapshot taken at emission. Trustworthy margin. |
| `NOT NULL` | `true` | Backfilled approximation. Usable only if the consumer says so. |
| `NULL` | `false` | Cost unknown (article had no `costo_nominal`, or free-concept line). |

Stage 10 then reports margin over the first bucket by default, and must state coverage instead of
silently averaging over lies.

## Resolved product decisions

**Decision 1 — Freeze `articulos.costo_nominal` exactly as-is, IVA included, no normalization.**
Verified in code, not assumed: stage 8 pinned that `costo_nominal` receives the **effective
IVA-included cost** rounded `AwayFromZero` (`CalculadorDeCompra.cs:86-95`, doc-10:438-441;
`ComprasLifecycleTests.ConfirmarEscribeMovimientoCacheYCostoNominalJuntos` asserts `121.00` for a
`100.00` cost at 21%). The POS only emits TX/NCX, which are `discrimina_iva = false`, so
`precio_unitario` is likewise the **final IVA-included** consumer price. **Both sides are already
in the same base**: `margen = total − costo_unitario × cantidad` is directly meaningful with no
conversion. Normalizing the cost to net would force a division by `porcentaje_iva` at write time,
lose a rounding cent, and misalign against a price that stays gross. Since purchase and sale use
the *same* `articulo.IdAlicuotaIva`, the gross margin is the net margin scaled by `(1 + iva)` —
and `porcentaje_iva` is already frozen on the line, so stage 19 (fiscal, IVA-discriminating
comprobantes) can derive the net margin later **without a schema change**. Recorded for stage 10:
*margin is computed IVA-included on both sides*.

**Decision 2 — Mark the backfill with `costo_es_estimado`, not with a cut-off date.** A date
inference is wrong on three counts: the cut-off is not a single value (each tenant/database
migrates at its own moment, a re-run shifts it, a later legacy import would create a second one),
it forces every consumer forever to hardcode a magic timestamp, and it cannot represent an
estimated row created *after* the cut-off. One boolean is self-describing, costs one byte, is
directly filterable (`WHERE NOT costo_es_estimado`) and lets the stage-10 dashboard **exclude or
badge** the estimated portion with no date logic. Honest caveat that must reach the spec: the
backfill uses *today's* cost, which under inflation is typically **higher** than the cost at sale
time — so an estimated margin is a pessimistic lower bound, not noise around the truth. Stage 10
must exclude estimated lines **by default**, with an explicit opt-in.

**Decision 3 — A nota de crédito freezes its own cost at its own emission; it does not copy the
original line.** Verified in code: an NCX travels the *same* `EmitirAsync` path with its own
lines, its prices re-resolved live by `ServicioDeOfertas` (never copied), and
`id_comprobante_asociado` is **optional even for an NCX** and links **comprobante to comprobante,
never line to line** (`ReglaDeComprobantes.ValidarComprobanteAsociado`, `ServicioDeVentas.cs:77-82`)
— there is no `id_item_original`, and the NC's articles and quantities need not match the
original's. Copying is therefore not generally computable, and article-matching heuristics
(partial returns, mixed lines) would guess wrong silently. Freezing at NC time is also the
posture already taken for price. **Sign convention (binding invariant):** `costo_unitario` is
stored **unsigned per-unit**, exactly like `precio_unitario`; the sign lives in `cantidad`
(`CalculadorDeTotales.cs:32-42`), so `costo_unitario × cantidad` is negative on an NCX and the
margin reverses on its own with no branch. *Accepted residual*: if the cost moved between the sale
and the return, the reversal does not cancel the original margin to the cent. Bounded by the cost
delta, and preferable to a heuristic. Anulación writes no items, so a frozen cost is never
rewritten — stage 10 excludes `estado = anulado` as it already must for revenue.

**Decision 4 — `costo_unitario` is NULLABLE.** `articulos.costo_nominal` is itself
`numeric(14,2) NULL` (`Articulo.cs:46-51`) and a large part of a real catalog has no cost loaded;
`items_comprobante_venta.id_articulo` is also nullable (free-concept lines, doc-10:339). NOT NULL
would force writing `0`, and `0` is **not** an unknown cost — it is a legitimately stated cost of
zero (a bonificación), and stage 8 already treats `costo <= 0` as a distinct case
(`CalculadorDeCompra.cs:135`). Collapsing the two would hand stage 10 a fake 100% margin on every
costless article, which is worse than a gap: it is a confident wrong number. NULL means *unknown*
and forces the consumer to say what it does about it.

**Decision 5 — The cost never leaves the server through the sale surface.** `ItemEmitido` /
`ComprobanteEmitido` and the POS screens stay byte-unchanged. A Vendedor emitting a ticket has no
business reading the purchase cost, and the emit response is the widest-reach payload in the
system. Margin becomes visible in stage 10, through its own aggregated and role-gated endpoints.
Keeps this stage additive at the schema layer only.

**Decision 6 — The backfill must defeat RLS explicitly, and prove it did.** Non-obvious trap
verified in code: every tenant table is `FORCE ROW LEVEL SECURITY` with
`USING (app_es_plataforma() OR id_tenant = app_tenant_actual())`
(`RlsMigrationBuilderExtensions.cs:65-77`), and the application role is asserted **not** to have
`BYPASSRLS` in Production (`InicializadorDeBaseDeDatos.VerificarRolSinBypassAsync`). A plain
`UPDATE` inside the migration would therefore match **zero rows and report success** — the worst
possible failure mode for a one-shot data fix. The backfill runs in platform mode (`SET LOCAL
app.acceso = 'plataforma'` within the migration transaction, scoped and reset), and a
multi-tenant integration test must prove rows of **every** tenant were touched. Idempotent by
construction (`WHERE costo_unitario IS NULL`), so a re-run is harmless.

## Modelo de datos propuesto

> **DB CHANGE GATE — for the owner's explicit approval before any migration is generated.**
> This proposal writes no code and applies nothing.

**Table affected:** `items_comprobante_venta` (`[operativa]`, tenant-scoped, doc-10:337-350).
**No new table, no new enum, no new index, no FK, no change to any existing column, constraint,
index or RLS policy.**

**New columns (both additive):**

| Column | Type | Null | Default | Meaning |
|---|---|---|---|---|
| `costo_unitario` | `numeric(14,2)` | YES | — | Snapshot of `articulos.costo_nominal` at emission. Per unit, **unsigned**, **IVA included** (decision 1). `NULL` = cost unknown. |
| `costo_es_estimado` | `boolean` | NO | `false` | `true` only on rows filled by the backfill (decision 2). |

*Why `numeric(14,2)` and not the `(14,4)` of `items_comprobante_compra.costo_unitario`*: the
source column `articulos.costo_nominal` is itself `numeric(14,2)`, so 4 decimals would advertise
precision that does not exist; `(14,2)` also keeps the margin arithmetic in the same scale as
`precio_unitario`.

**New CHECK constraints** (naming mirrors `ck_items_comprobante_compra_costo_no_negativo`):

1. `ck_items_comprobante_venta_costo_no_negativo`:
   `costo_unitario IS NULL OR costo_unitario >= 0`
2. `ck_items_comprobante_venta_estimado_con_costo`:
   `NOT costo_es_estimado OR costo_unitario IS NOT NULL`
   (an "estimated" mark with no cost is meaningless — makes it unrepresentable)

**Backfill (one-shot, inside the same migration, idempotent):**

```sql
-- runs in platform mode (decision 6) inside the migration transaction
UPDATE items_comprobante_venta i
   SET costo_unitario    = a.costo_nominal,
       costo_es_estimado = true
  FROM articulos a
 WHERE a.id_articulo = i.id_articulo
   AND a.id_tenant   = i.id_tenant
   AND i.id_articulo IS NOT NULL
   AND a.costo_nominal IS NOT NULL
   AND i.costo_unitario IS NULL;
```

- Applies to **all** existing item rows, TX and NCX alike, of every tenant.
- Soft-deleted articles are **included** on purpose (`deleted_at` is not filtered): a removed
  article still has the only cost we will ever know for that line.
- Rows with `id_articulo IS NULL` or with no `costo_nominal` stay `(NULL, false)` — honest gap.
- No batching: `items_comprobante_venta` is a single set-based statement at current volume.

**Down:** drop both CHECKs and both columns. Fully reversible, no data outside the two columns is
touched.

## Affected Areas

| Area | Impact | Description |
|---|---|---|
| `src/Ways.Domain/Ventas/ItemComprobanteVenta.cs` | Modified | Two properties + the snapshot doc-comment (:14-19) gains the cost |
| `src/Ways.Infrastructure/.../ItemComprobanteVentaConfiguration.cs` | Modified | Two `Property` mappings + two `HasCheckConstraint` |
| `src/Ways.Infrastructure/Persistencia/Migraciones/` | New | One additive migration + the platform-mode backfill |
| `src/Ways.Application/Ventas/ServicioDeVentas.cs` | Modified | `LineaDelPlan` gains `CostoUnitario`; set in `MaterializarItems`, written in `EjecutarTransaccionAsync` |
| `openspec/specs/comprobantes-venta/spec.md` | Modified | Delta: schema at rest, snapshot list, new cost-semantics requirement |
| `docs/10-modelo-de-datos.md` §4 | Modified | Schema note for the two columns (same trailing-blockquote convention as stages 5-8) |
| `tests/Ways.IntegrationTests`, `tests/Ways.Domain.Tests` | Modified | Snapshot, NCX sign, NULL cases, CHECKs, multi-tenant backfill |
| API responses / `src/Ways.Web` | **Untouched** | Decision 5 |

## Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| Backfill silently updates 0 rows (RLS `FORCE` + no `BYPASSRLS`) | **High** | Decision 6: platform mode inside the migration + a multi-tenant test asserting rows of every tenant were touched |
| Estimated cost read as real → invented historical margin | Med | `costo_es_estimado` + spec text stating it is a pessimistic bound; stage 10 excludes by default |
| Touching `ServicioDeVentas`, the most-tested path in the project | Med | No new query/lock/branch; the field rides `articuloPorId`, already read; full stage 5-8 suite green as the slice gate |
| Cost leaking to the cashier via the emit response | Low | Decision 5 + a test asserting the response DTO shape is unchanged |
| `costo_nominal = 0` confused with "no cost" | Low | Decision 4 keeps `NULL` and `0` distinct, with a scenario for each |
| NCX margin not cancelling the original to the cent | Low | Accepted and documented (decision 3); bounded by the cost delta |
| Scope creep into stage 10 (someone adds "just one aggregate") | Med | Out of Scope is explicit; no read endpoint ships in this stage |

## Rollback Plan

1. **Before merge**: revert the branch. Nothing external changed.
2. **After merge, before deploy**: revert the commit and delete the migration.
3. **After deploy**: `Down` drops both CHECKs and both columns; every other column, index, policy
   and row is untouched, so the system returns exactly to its stage-8 state. The only loss is the
   frozen costs captured meanwhile — which is precisely the pre-stage-9 status quo.
4. **Partial rollback** (keep the columns, distrust the backfill):
   `UPDATE items_comprobante_venta SET costo_unitario = NULL, costo_es_estimado = false WHERE costo_es_estimado;`
   — real snapshots survive, estimates disappear.

## Dependencies

- None. Stage 9 has no prerequisite (doc-11:90).
- **Stage 10 depends on this one** for its margin dimension; every other stage-10 aggregate does not.

## Success Criteria

- [ ] A sale emitted after the migration has `costo_unitario` on every line whose article has a
      `costo_nominal`, equal to that value, with `costo_es_estimado = false`.
- [ ] An article with `costo_nominal = NULL` produces a line with `costo_unitario = NULL` and
      `costo_es_estimado = false` — never `0`.
- [ ] An article with `costo_nominal = 0` produces `costo_unitario = 0`, distinguishable from the
      previous case.
- [ ] An NCX line stores an unsigned `costo_unitario` frozen at its own emission, and
      `costo_unitario × cantidad` comes out negative.
- [ ] The backfill marks every reachable pre-existing row of **every** tenant with
      `costo_es_estimado = true`, proven in a multi-tenant fixture; re-running is a no-op.
- [ ] Both CHECKs reject their violations at the database level (schema backstop tests, the
      stage-8 `ComprasSchemaBackstopTests` pattern).
- [ ] `ComprobanteEmitido` / `ItemEmitido` and every web payload are byte-unchanged.
- [ ] The emission query budget is unchanged (the existing guard test still passes with the same
      constant).
- [ ] Full suite green: Domain, Application, Integration, vitest — none of them by adjusting an
      existing expectation about the sale transaction's shape.

## Note for sdd-tasks

**One slice, one PR.** Estimated ~250-400 changed lines including tests: migration + configuration
+ two entity properties + ~5 lines in `ServicioDeVentas` + spec delta + doc-10 note + tests. Under
the 400-line review budget; no chaining expected. The DB CHANGE GATE carries a **STOP task**: the
*Modelo de datos propuesto* section above must be approved by the owner before the migration is
generated. judgment-day applies as usual before the PR.

## Deferred / adjacent (recorded, not in scope)

- **Real historical cost reconstruction.** `items_comprobante_compra` *does* keep per-purchase
  cost history (`costo_unitario numeric(14,4)`, doc-10:404). A future change could improve the
  estimate by picking, per sale line, the last confirmed purchase cost **before** that sale's
  date, instead of today's `costo_nominal`. Deliberately out of scope: it needs a per-article
  temporal join, it only helps articles that were purchased through the system (none of the
  legacy-era ones), and it can be re-run later over exactly the rows this stage marks
  `costo_es_estimado = true` — the boolean makes that future refinement **cheap**, which is a
  second argument for decision 2.
- **Weighted-average / FIFO valuation, cost per punto de venta, cost in another currency.**
- **Purchase-IVA-discriminated (net) margin** — derivable later from the already-frozen
  `porcentaje_iva`, no schema change needed (decision 1).

## Proposal question round

Recorded for the owner's review before `sdd-tasks`. Each was resolved with a founded
recommendation above; correcting any of them here is cheap, after `sdd-apply` it is not.

1. **Is today's `costo_nominal` a good enough estimate for past sales, given inflation?** Assumed
   **yes, as a marked pessimistic bound** (decision 2). If the owner considers it misleading even
   when flagged, the alternative is to ship the columns with **no backfill** — historical rows stay
   `NULL` = honestly unknown. Cost of changing later: one `UPDATE`, trivial.
2. **Should the margin be computed IVA-included on both sides?** Assumed **yes** (decision 1) —
   it is what both stored values already are. If the owner reasons about margin **net of IVA**,
   nothing changes in this stage's schema (the net figure is derivable), but stage 10 must state
   which base it displays.
3. **Should a nota de crédito reverse the *original* margin exactly, or freeze its own cost?**
   Assumed **freeze its own** (decision 3) — the code has no line-to-line link to copy from.
4. **Should any operator see the cost/margin on the sale screen?** Assumed **no** (decision 5).
   If the owner wants it for Admin, it is a stage-10 decision, not a schema one.
5. **Is one boolean column acceptable, or must the change be strictly one column?** Assumed the
   **boolean is worth it** (decision 2). Dropping it would force date-based inference forever.
