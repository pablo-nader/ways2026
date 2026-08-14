# Proposal: Stage 13 — Stock inteligente (mínimos, alertas y reposición)

## Intent

Stage 12 answered *what is about to spoil*. Stage 13 answers the other half of the same
question: **what is about to run out** (doc-11:165-188).

Today the system cannot answer it at all. `stock.minimo` and `stock.reposicion` have existed
since the Etapa 5 migration (`20260804143427_VentasStockYCuentaCorrienteEtapa5`,
doc-10:508-513) with **exactly the shape this stage needs** — `numeric(12,3)`, nullable, on a
row keyed `(id_articulo, id_punto_venta)`, i.e. per articulo **and** per punto de venta. And
**nobody writes them, nobody reads them, nobody shows them**: `explore.md` §1 confirms the
only file in `src/` that mentions either column is the EF mapping that declares them. Three
stages of reserved space, zero behaviour.

The operational consequence is the ordinary one in a food business: the shelf empties, the
owner finds out when a customer asks, and the reorder decision is made from memory. The
legacy system tried to help here — `alsina/imprimirArticulos.php` — and almost certainly
never ran in production (it opens its own hardcoded `mysqli_connect('127.0.0.1','root','')`
instead of using `conexion.php`, doc-01:170). There is **no observed behaviour to replicate**;
this is designed from zero, which is exactly why the conservative bias below matters.

Stage 13 turns those two dormant columns into a circuit: a reorder point the owner sets per
articulo and punto de venta, a **single read model** that lists what is at or below it grouped
by proveedor habitual, a Tablero tile, an `/export` sibling that *is* the "lista de
reposición" doc-11 asks for, and a rotation figure that makes the reorder point explainable
instead of invented.

The governing constraint is inherited verbatim from stage 12: **the checkout hot path pays
nothing.** Every surface in this stage is pull, read-only, and lives behind
`Politicas.LecturaDeReportes` or `Politicas.GestionDeCatalogo`. No `ServicioDeVentas` write
path is touched (decision 8).

The second governing constraint is honesty about what does not exist yet: there is **no
order-with-state entity** in this system (Etapa 16 owns órdenes de compra) and therefore
**"stock en tránsito" is structurally zero, always** (decision 4). This proposal refuses to
ship a field that says `0` and pretends to mean something.

## Scope

### In Scope

- **Reorder parameters, activated in place**: `stock.minimo` (the reorder point) and
  `stock.reposicion` (the restock target) become readable and writable per
  `(articulo, punto de venta)`. `NULL` means **not managed** — an articulo with no `minimo`
  never alerts (decision 1).
- **A minimum write path** — `PUT /api/stock/minimos`, Admin-only, that **creates the
  `stock` row with `cantidad = 0` when it does not exist and writes zero
  `movimientos_stock` rows**. Setting a threshold is not a movement (decision 6).
- **The reposición read model** — one query, three surfaces (report JSON, `/export` sibling,
  Tablero tile), listing rows where `minimo IS NOT NULL AND cantidad <= minimo`, joined to
  `articulos` and to `articulos.id_proveedor_habitual`, carrying the suggested purchase
  quantity. **The low-stock alert and the purchase suggestion are the same list seen twice**
  (decision 3).
- **Rotation** — average daily consumption over a configurable window, computed from
  `movimientos_stock`, used to (a) show days of coverage next to each alert row and (b)
  suggest a minimum where none is set. **Advisory only; it never fires or suppresses an
  alert** (decision 1, decision 7).
- **Existencias becomes the per-PV stock screen**: the existing report gains `minimo`,
  `reposicion` and a derived `estado` (`bajo` | `ok` | `sin_minimo`), and the screen gains
  inline editing of the two thresholds (decision 6).
- **Two `ParametroConocido` keys**, no migration: `dias_rotacion` (int, default `30`) and
  `dias_cobertura_objetivo` (int, default `7`).
- **Web**: minimum/reposición editing grid on `Existencias.tsx`, a new `Reposicion.tsx`
  screen grouped by proveedor with a download button, and a low-stock tile on `Tablero.tsx`
  following the `PanelDeVencimientos` shape.

### Out of Scope

- **Push alerts of any kind** — no mail sender, no scheduler, no notification table, no job
  runner. The pull channel stage 12 established (its decision 10) is inherited unchanged, now
  with its second consumer (decision 2). The tripwires that would reopen this are named.
- **Order generation** — the reposición list does **not** create a compra borrador, a
  purchase order, or any write into `ServicioDeCompras`. Etapa 16 owns órdenes de compra
  (doc-11:232, backlog row 369) (decision 3).
- **"Stock en tránsito" as a field** — omitted entirely, not hard-coded to zero (decision 4).
- **The full conteo snapshot/freeze/variance workflow** — the backlog row that doc-11:367
  assigns to this stage is **explicitly carved out and re-registered**, not silently dropped
  (decision 5). The standing exclusion in `conteo-de-inventario/spec.md`'s Purpose is **not**
  reopened by this stage.
- **Any synchronous low-stock warning at the POS** — the checkout budget stays blinded
  (decision 8).
- **Consolidated multi-punto-de-venta reposición** — v1 requires a concrete `idPuntoVenta`,
  exactly like `existencias` and `vencimientos`. Recorded as the most likely first follow-up.
- **Bulk operations on minimums** — no "copy minimums from another PV", no mass import, no
  percentage-based recalculation across the catalog. Named in the deferred list.
- **Automatic writing of `minimo` from rotation** — the computed value is displayed as a
  suggestion and never persisted without an explicit Admin action (decision 1).
- **Lot-aware reposición** — the reorder point is per `(articulo, PV)`, never per lot. A lot
  is an expiry identity, not a replenishment unit; stage 12's model is untouched.
- **Cost/price in the reposición list** — no "estimated purchase cost" column. That is a
  rentabilidad-adjacent figure and would drag stage-9 costing semantics into a stock report.
- **The owner's reserved items**: comisiones formula, Supervisor margin, `OperacionDePos`
  read model, cierre de caja por rol, export branding. Untouched.

## Capabilities

### New Capabilities

- **`reposicion-de-stock`** — owns the replenishment meaning end to end: the semantics of
  `minimo`/`reposicion` (`NULL` = unmanaged, the `<=` boundary, the restock target), the
  Admin-only write path and its no-movement rule, the rotation definition (which `motivo`
  values count as consumption, the window and its timezone resolution), the suggestion
  formula and its **honest nulls**, and the reposición report with its export sibling and
  Tablero tile.

Following the stage-11/stage-12 precedent (a new capability owns its report end to end
rather than smearing it across existing specs), the reposición report lives here in full.

### Modified Capabilities

- **`stock`** — one ADDED requirement only: the `stock` row carries reorder parameters that
  are **not** a ledger fact, so writing them creates the row when absent and MUST NOT insert
  a `movimientos_stock` row. This is a statement about `stock`'s own invariant
  (`cantidad = SUM(movimientos)` must survive a row that exists with `cantidad = 0` and zero
  movements), which is why it belongs here and not in the new capability.
- **`reportes-de-gestion`** — the existing "Existencias Report Joins Stock To Artículos Under
  The Same Gate" requirement is restated to include `minimo`, `reposicion` and `estado` in the
  response and its export.
- **`parametros-operativos`** — two new `ParametroConocido` keys, no migration (the
  stage-10/stage-12 pattern).

**Not modified**: `exportacion-de-reportes` (this stage is a consumer of the contract, not an
amender), `articulos` (the `id_proveedor_habitual` circuit is complete since stage 3 and this
stage only reads it), `conteo-de-inventario` (its exclusion stands; see decision 5),
`lotes-y-vencimientos`, and every write-path spec (`comprobantes-venta`,
`comprobantes-compra`, `transferencias-de-stock`).

## Approach

**Two dormant columns, two registry keys, zero DDL.**

The whole stage is a read surface plus one small Admin write. The reorder point is a *fact
the owner states*, stored where it already has a home; the alert is a comparison; the
reposición list is that comparison joined to the supplier the articulo already names; the
rotation figure is a bounded aggregation over a ledger that already exists. Nothing in this
stage creates a new source of truth, and nothing in it can corrupt one — the only write is a
threshold on a row whose quantity it never touches.

That shape is what makes the whole stage reversible at essentially zero cost: **remove the
code and the columns go back to being dormant**, exactly as they have been since Etapa 5. No
migration to roll back, no enum value stranded in a type (stage 12's one genuinely
irreversible artifact has no equivalent here), no data rewritten.

The one place where care is genuinely required is **arithmetic honesty**: which movements
count as consumption, what a suggestion means when its inputs are absent, and what a report
does with rows it cannot classify. Each of those is decided explicitly below, with the
stage-12 precedent cited where one exists — because that stage's own judgment-day season
proved that the expensive bugs in a read-only surface are the ones where a figure is
*plausible and wrong*, not the ones where it is missing.

## Autonomous decisions

Written under delegated technical authority, conservative and reversible bias. Each records
context, the options with their tradeoffs, the decision, and **what it costs to reverse it**.

---

### 1 — The minimum is a **fixed value the owner sets**, with a rotation-computed suggestion shown beside it. Never a computed threshold.

**Context.** doc-11:186 leaves this open: "mínimo fijo por artículo o calculado por rotación".
Both are implementable; `stock.minimo` exists for the fixed one, and `movimientos_stock` has
everything needed for the computed one.

**Options.**

| Option | Pro | Contra |
|---|---|---|
| Fixed value only | Explainable, stable, auditable, zero compute | The owner has to invent a number for every articulo from nothing |
| Computed from rotation only | Self-maintaining, adapts to demand | The threshold **moves every day without anyone touching it**; an articulo alerts today and not tomorrow with no user-visible cause; there is no stored value to audit; a new articulo (no history) has no threshold at all; a seasonal item's threshold collapses out of season |
| **Fixed, with the computed value shown as a suggestion** | Stable and explainable; the computed figure removes the blank-page problem; the owner keeps authorship | Two numbers on screen; the suggestion needs a defined formula |

**Decision.** **Fixed, with a visible suggestion.** `stock.minimo` is the reorder point the
alert compares against — always, only, and exclusively. Rotation produces `minimoSugerido`,
which is **displayed next to the field and never persisted without an explicit Admin action**.

Three rules make the fixed value honest:

- **`minimo IS NULL` means unmanaged.** A NULL minimum never alerts. This is what makes day
  one silent instead of catastrophic: a catalog of hundreds of articulos does not become
  hundreds of alerts the moment the stage ships. A default of `0` would have been the
  opposite: every out-of-stock articulo alerting from the first deploy.
- **The boundary is `cantidad <= minimo`, not `<`.** A reorder point is the level at which
  you order — reaching it *is* the signal. This also makes `minimo = 0` mean the useful
  thing ("tell me when it hits zero") rather than the useless one ("tell me only once it is
  already negative"). Stated explicitly because stage 12's judgment-day season proved
  boundary semantics left implicit get implemented three different ways (its decision 13).
- **`reposicion` is the restock target, not a second threshold.** `minimo` says *when*,
  `reposicion` says *up to what*. The suggested purchase quantity is
  `reposicion − cantidad`, and it is **null, never zero**, when `reposicion` is unset
  (decision 3).

**Cost of reversing.** Switching to a computed threshold later changes one comparison in one
query; the stored column survives as a per-row override, which is what a mature system wants
anyway. Reversing in the other direction — from computed to fixed — would require inventing
the historical values that were never stored. The conservative direction is the one taken.

---

### 2 — Alerts stay **pull**. The second use case does not change the argument; it strengthens the surface.

**Context.** Stage 12's decision 10 fixed the alert channel as pull (report + `/export` +
Tablero tile) and deferred push explicitly *"con un segundo caso de uso real en la mano"* —
which is now: vencimientos + bajo stock. This proposal owns that decision.

**What push would actually cost, priced honestly.** A transport dependency (SMTP provider or
API) with per-tenant configuration and secrets; a scheduler or job runner (nothing in `src`
runs anything outside a request); a delivery-state model (sent / bounced / retried); retry and
bounce handling; per-user subscription preferences and an unsubscribe path; a deduplication
rule so the same articulo does not alert every morning forever; and a security review of
outbound mail from a multi-tenant system. That is a stage, not a slice, and it would be
designed blind: the system has **one** operator today, and no evidence exists that a daily
Tablero glance is failing him.

**What two consumers actually change.** They make the pull surface *better*, not weaker: the
Tablero stops being a stage-12 curiosity with one tile and becomes **the alert tray** — two
tiles, one glance, both linking to their report and both exporting. That is a coherent
product story that a mail per topic would fragment.

**Decision.** **Pull, inherited unchanged.** No push infrastructure in this stage.

**Named tripwires that reopen this** (so "not yet" is not "never"):

1. A person who must react to an alert but does not open the back office daily (e.g. a
   proveedor-facing buyer, or an owner working from the floor).
2. Any stage that needs a scheduler/job runner **for its own reasons** — at that point the
   marginal cost of push collapses. Etapa 16 (recepción parcial / OC lifecycle) and Etapa 14
   (retention of the audit log) are the realistic candidates.
3. A recorded incident of "we found out too late" that a daily glance would have prevented.

**Cost of reversing.** The reports and their query are the payload of any future push; adding
a channel later reuses them entirely. Nothing in this stage has to be undone to add push —
which is precisely why deferring costs nothing.

---

### 3 — The purchase suggestion is an **actionable list, grouped by proveedor**. It does not generate an order or a draft.

**Context.** doc-11:188 asks whether the suggestion "genera directamente una orden de compra
cuando exista la Etapa 16, o queda como listado". Etapa 16 does not exist. What *does* exist
is `comprobantes_compra` with `estado = borrador` (stage 8), so seeding a draft is
technically possible today.

**Options.**

| Option | Pro | Contra |
|---|---|---|
| Listing + export only | Zero write paths touched; the export **is** the deliverable (send it to the proveedor, take it to the phone); matches doc-11's own framing of the legacy idea | The operator retypes quantities into `CompraEditor` if they want a document |
| Seed a compra borrador per proveedor | Saves retyping | Stage 13 would own a `ServicioDeCompras` write path (against this stage's hard constraint); it needs a unit cost per line that this stage has no business deciding; a wrong quantity becomes a **real artifact someone must delete**; and Etapa 16 will model ordering properly, leaving two competing entry points into compras |
| Generate an OC | — | There is no OC entity. Not an option. |

**Decision.** **Listing + export, grouped by proveedor habitual.** No write path into compras.

Two rules that keep the list honest:

- **Rows whose articulo has no `id_proveedor_habitual` are grouped under "Sin proveedor" and
  never omitted.** A replenishment list that silently drops the articulos nobody assigned a
  supplier to lies by omission — the exact criterion that made stage 12 add its fourth
  `sin_fecha` state to the vencimientos report rather than filter those rows out.
- **The suggested quantity is `null` when `reposicion` is unset**, never `0`. A zero in a
  "how much to buy" column is a fabricated answer to a question the system cannot answer;
  `dto-contract-honesty` applies directly.

**Cost of reversing.** Adding "generate a draft/OC from this list" later is purely additive
over a list that already computes the quantity and the supplier grouping — it is one endpoint
consuming an existing read model. Doing it now and having Etapa 16 model ordering differently
would leave a legacy entry point to deprecate.

---

### 4 — "Stock en tránsito" is **omitted from the formula**, not hard-coded to zero.

**Context.** doc-11:169 names it as an input to the purchase suggestion. `explore.md` §5
proves it is structurally zero: `MotivoStock` has no in-transit state; a transferencia writes
its two mirrored rows **in one transaction** (no window where goods are in motion); a compra
posts its movements entirely at `Confirmar` (partial reception is explicitly Etapa 16,
doc-11:232). There is no moment in the current model at which merchandise exists and is not
yet counted somewhere.

**Options.** (a) Ship `enTransito: 0` in the response with a comment; (b) omit the term and
document the formula without it.

**Decision.** **Omit it.** A field that is always `0` is a lie-shaped API: every consumer
treats it as meaningful, the web renders a column of zeros, and when Etapa 16 gives it a real
computation the field's meaning changes *silently* under callers who already believed it.

The formula this stage ships and documents is:

```
sugerido = reposicion IS NULL ? null : max(0, reposicion − cantidad)
```

with a recorded note — in the capability spec's Purpose and in this proposal — that
**`− en_transito` is the term Etapa 16 adds**, once orders have state and an expected arrival.

**One adjacent edge case decided with it: compras en borrador do NOT count as incoming.** A
draft is not a commitment, carries no expected arrival date, and may never be confirmed;
counting it would *suppress* an alert for goods that never arrive — the worst failure mode a
replenishment alert has. Recorded as a candidate input for Etapa 16, where a draft becomes an
order with a state.

**Cost of reversing.** Adding a subtrahend to one expression, plus one field on one DTO.

---

### 5 — The **full conteo snapshot/variance workflow is carved out** of this stage and re-registered in doc 11. Explicitly, in writing, not by silence.

**Context.** doc-11:367 assigns to Etapa 13 the backlog item *"Conteo de inventario completo
(la Etapa 8 entregó una versión mínima, sin workflow de snapshot/variance)"*. The
`conteo-de-inventario` spec's Purpose carries the matching standing exclusion: *"Out of scope:
any full-count snapshot/freeze/variance workflow."* `explore.md` §7 flagged this as real,
unclaimed scope.

**Dimensioning it honestly.** A real snapshot workflow needs: a `conteos` header table
(punto de venta, estado abierto/aplicado/anulado, timestamps) plus `items_conteo` lines
(esperado at snapshot, contado, diferencia); a new enum for the header state; an apply step
that writes N `inventario` movements in **one transaction respecting the pinned lock order** —
which since stage 12 is `(id_articulo, id_punto_venta, id_lote NULLS FIRST)` and must handle
lot-effective articulos per lot; a variance report and its export; and a full counting screen
with a multi-session workflow. Realistically **1200–1600 lines across 4–5 slices, and a
migration that reopens the DB gate** — for a stage whose entire remaining scope needs **zero**
schema change.

And it carries an **unresolved product fork that this stage has no mandate to settle**: the
system does not stop selling while someone counts. Is the variance computed against the frozen
snapshot (in which case sales during the count are absorbed into the difference and the count
is wrong) or against live stock at apply time (in which case the snapshot is decorative)? That
is a business-rule decision deserving its own explore and proposal, not a slice bolted onto a
replenishment stage.

**Options.**

| Option | Verdict |
|---|---|
| Absorb it fully | Rejected: roughly doubles the stage, reopens the DB gate the rest of the stage does not need, and drags in an unsettled product fork |
| Absorb it as a final droppable slice | **Rejected, and this is the trap worth naming**: it needs a migration, and a migration inside a droppable slice means opening the DB gate for something that may be dropped — the worst of both outcomes |
| Carve it out into its own change, re-registered | **Chosen** |

**Decision.** **Carved out.** It becomes its own change (working name
`stage-13b-conteo-por-planilla`), sequenced after this stage and before or alongside Etapa 14,
with which it shares more DNA (both are about reconstructing the truth of what happened) than
with replenishment.

**This decision is not complete until the registration is.** A task in slice 1 updates
`docs/11-programa-post-paridad.md`'s backlog table (line 367) so the row reads its new owner
and cites this decision — the same discipline stage 12 used when it updated doc 10 §6 from
within a slice (its task 1.17). A carve-out recorded only in a proposal is a carve-out that
disappears at the next archive.

**Cost of reversing.** None on this stage's artifacts; the carved-out work is untouched and
fully specified by its own future explore. Re-absorbing it would only mean sequencing it
sooner.

---

### 6 — The minimum is edited **in the Existencias grid**, which becomes the per-punto-de-venta stock screen. Not in the articulo editor.

**Context.** `explore.md` §8 named this the stage's real UX tension: `minimo`/`reposicion` are
per `(articulo, punto de venta)`, but `Articulos.tsx` is tenant-wide. The value has no obvious
home.

**Options.**

| Option | Verdict |
|---|---|
| Section inside `Articulos.tsx` | **Rejected.** A tenant-wide editor holding one PV's value is precisely the scoping mismatch stage 12 spent decision 2 closing at the write path. Showing N PV rows inside an articulo form turns the articulo editor into a stock screen |
| A brand-new "Mínimos" screen | Rejected as a duplicate: it would render the same list, for the same one PV, with the same PV selector as `Existencias.tsx` |
| **Columns + inline edit on `Existencias.tsx`** | **Chosen** |

**Decision.** `Existencias.tsx` becomes the per-PV stock screen: `Artículo / Nombre /
Cantidad / Mínimo / Reposición / Estado`, with the two thresholds editable inline. It is
already the one screen scoped to exactly one punto de venta and listing exactly the
`(articulo, PV)` rows the value belongs to.

Four rules make this safe:

- **The report stays read-only; the write is a separate endpoint under a separate policy.**
  `GET /api/reportes/stock/existencias` keeps `Politicas.LecturaDeReportes` (Supervisor +
  Admin); `PUT /api/stock/minimos` requires `Politicas.GestionDeCatalogo` (Admin) stacked over
  `OperacionDePos`, exactly like `/ajustes`, `/transferencias`, `/conteos` and `/decomiso`
  before it. **A Supervisor sees the columns and cannot edit them** — a testable rule, not a
  UI convention.
- **Writing a minimum for an articulo with no `stock` row creates it with `cantidad = 0` and
  writes zero `movimientos_stock` rows.** The invariant `cantidad = SUM(movimientos)` holds
  trivially at `0 = 0`. The naive implementations — failing with "no stock row", or
  fabricating an `ajuste` movement of zero — are both wrong, one uselessly and one
  destructively (`ck_movimientos_stock_cantidad_no_cero` would reject it anyway). This is the
  one ADDED requirement on the `stock` capability.
- **Reaching an articulo not yet in the grid** uses an articulo search-and-add row (the picker
  pattern the POS and `CompraEditor` already use), **not** a widened join. The existencias
  report keeps its exact current meaning — "what is stocked here" — instead of silently
  becoming "the whole catalog, mostly empty".
- **Validation**: `minimo`/`reposicion` MUST be `>= 0` (`400 minimo_negativo`), and
  `reposicion` MUST NOT be below `minimo` when both are set
  (`400 reposicion_menor_que_minimo`) — a restock target under the reorder point is not a
  configuration, it is a typo. Both are application-level guards, not CHECKs (see the gate
  section).

**Cost of reversing.** Moving the editor elsewhere later is a web-only change over an
unchanged API. Explicitly recorded as the honest residue: **N puntos de venta × M articulos
values to maintain by hand.** A "copiar mínimos de otro punto de venta" bulk action is the
named mitigation, deferred (not built) because it should be designed after seeing how many
values the owner actually sets.

---

### 7 — Rotation is computed in **LINQ, in `ServicioDeReportesDeStock`**. No new raw-SQL file, and `LectorDeSerieTemporal` is not extended.

**Context.** `explore.md` §3 framed this as the stage's one genuine technical fork: extend
`LectorDeSerieTemporal.cs` (stage-10's declared invariant, "one file is one review target and
one grep target") or open a second raw-SQL reader (cohesion by table). Both had citable
precedent.

**The fork dissolves on inspection.** Raw SQL was forced in stage 10 by two specific needs:
a `date_trunc('{granularidad}', ...)` literal that had to be inlined from a validated switch,
and `timezone($1, fecha)` bucketing inside the query. **Rotation needs neither.** It is a
single window, not a time series — one `WHERE` over a date range, one `GROUP BY` over
`(id_articulo)`, one `SUM`. That is plain LINQ over `db.MovimientosStock`, structurally
identical to `ObtenerExistenciasAsync`, and it inherits EF's global filters and the
connection-level RLS GUCs without a single line of hand-rolled connection handling.

**Decision.** **No raw SQL at all.** A private query builder in `ServicioDeReportesDeStock`,
beside `ConstruirQueryDeVencimientos`. The stage-10 "one raw-SQL file" convention is honoured
by **not adding a second one**, and `LectorDeSerieTemporal` — whose two SQL bodies are about
`comprobantes_venta` and `gastos` — keeps its cohesion.

**Three definitional rules, each of which is a real bug if left implicit:**

- **What counts as consumption**: `motivo = venta`, **plus** `motivo = anulacion` rows whose
  `id_comprobante_compra IS NULL`. This is not pedantry: since stage 8, an anulación of a
  *compra* also writes `motivo = anulacion`, and the `stock` spec states that
  `id_comprobante_compra` is populated exactly on `compra` rows and on the `anulacion` rows
  that reverse them. Filtering on `motivo IN (venta, anulacion)` alone would net **purchase
  reversals into sales figures**. `ajuste`, `inventario`, `decomiso`, `transferencia` and
  `reclasificacion` are **not** consumption and are excluded.
- **The window resolves in the punto de venta's `zona_horaria`**, computed in C# from the
  `hoy` that `ResolverContextoAsync` already produces (stage 12, decision 15) and passed as
  instants. This is the stage-11 slice-9 bug class that needed a post-archive hardening commit
  (`08e7707`); it is a binding verify criterion here.
- **Rotation is advisory and never gates an alert.** A wrong rotation figure misleads a
  human reading a suggestion; it can never fire a false alert or suppress a true one, because
  the alert compares `cantidad` to the stored `minimo` and nothing else (decision 1). That
  containment is deliberate and bounds the blast radius of the stage's only non-trivial
  arithmetic.

**Cost of reversing.** If a future rotation surface genuinely needs bucketing (rotation *over
time*, per week), it becomes a third SQL body in `LectorDeSerieTemporal` at that point, with
the granularity/timezone justification that does not exist today.

---

### 8 — **No synchronous low-stock warning at the POS.** The checkout budget stays blinded.

**Context.** `explore.md` §10 raised it as an explicit scope question. Stage 12 made "the
module off costs the checkout nothing" a spec requirement asserted by a **query-count test**;
this stage must not be the one that quietly erodes it.

**The honest technical observation first**: it *could* be nearly free. The checkout's
`INSERT ... ON CONFLICT DO UPDATE ... RETURNING` on `stock` already returns the row; adding
`minimo` to the `RETURNING` list costs **zero extra round-trips**. So this is not a cost
argument. It is a product argument.

**Why not, anyway:**

- The warning would fire **after** the sale is committed — the `RETURNING` happens inside the
  transaction that already sold the goods. It tells the cashier something they cannot act on
  at the counter.
- **The cashier is not the person who reorders.** A notice addressed to someone who cannot
  respond to it is noise.
- Stage 12 already put a `loteVencido` warning on that exact surface. A second,
  non-actionable warning next to an actionable one **trains operators to dismiss both** — the
  most expensive kind of UX regression, and invisible in any test suite.
- It would change the hottest write path's SQL and its response DTO for a back-office
  concern, spending the credibility of a blinded budget on something the Tablero tile already
  covers within one working day.

**Decision.** **No.** No checkout write path is touched by this stage. A success criterion
asserts the checkout's round-trip count is unchanged versus pre-stage-13, by test (stage-12's
pattern), so this is verified rather than promised.

**Cost of reversing.** One column in a `RETURNING` clause and one nullable field on the
checkout response. Recorded precisely so that a future owner who wants it knows it is a
one-slice change and not an architecture decision being locked out.

---

## Modelo de datos propuesto

### Gate verdict: **SIN CAMBIOS DE SCHEMA**

**This stage ships no migration.** No table, no column, no index, no constraint, no enum
value, no view, no trigger, no function, no data statement. `dotnet ef migrations
has-pending-model-changes` MUST stay clean throughout the stage — that is a verify criterion,
not an expectation.

The section is written out in full anyway, gate-ready, because the stage does introduce two
`ParametroConocido` registry entries and because the *non*-decisions below are gate-relevant.

#### A. Columns activated in place — `stock` (existing, unchanged shape)

| Column | Type | Nullability | Status |
|---|---|---|---|
| `minimo` | `numeric(12,3)` | NULL | **Already exists** (Etapa 5 migration, doc-10:511). Gains a reader and a writer. |
| `reposicion` | `numeric(12,3)` | NULL | **Already exists** (idem). Gains a reader and a writer. |

`StockConfiguration.cs:35-36` already maps both with the correct precision. The entity
(`Stock.cs:24-32`) already exposes `decimal? Minimo` / `decimal? Reposicion`. **Nothing to
migrate.**

Scoping (doc 09): `stock` is **operativa** (`id_tenant` + `id_punto_venta`), so the reorder
parameters are per-punto-de-venta and tenant-isolated by the existing hand-rolled tenant
filter plus RLS, with no new scoping question raised.

#### B. No schema change — `parametros`

Two new `ParametroConocido` entries only. **No migration, no data statement**, exactly as
stage 10 did for `zona_horaria`/`comision_porcentaje` and stage 12 for
`lotes_habilitado`/`dias_alerta_vencimiento`:

| Key | CLR type | Default | Role |
|---|---|---|---|
| `dias_rotacion` | `int` | `30` | Consumption window for the rotation figure (decision 7) |
| `dias_cobertura_objetivo` | `int` | `7` | Days of consumption the suggested minimum should cover (decision 1) |

Both resolve through the existing punto de venta → empresa → declared-default precedence and
require no `parametros` row to exist.

#### C. Deliberate non-decisions (gate-relevant)

- **No `CHECK (minimo >= 0)` / `CHECK (reposicion >= minimo)`.** Both are enforced at the
  application layer (`400 minimo_negativo`, `400 reposicion_menor_que_minimo`). Adding a
  CHECK would turn a zero-migration stage into a migration stage for validation that the
  service performs anyway, and it follows the deliberate precedent of the **absent** CHECK on
  `stock_lotes.cantidad` (stage-12 gate, approved) and the still-open "importe CHECK
  micro-gate" carried from stage 8.
- **No new index.** The alert query filters `id_punto_venta` and then evaluates
  `minimo IS NOT NULL AND cantidad <= minimo` over a row set already bounded by the catalog —
  the same bound `ObtenerExistenciasAsync` scans unpaginated today, covered by
  `ix_stock_punto_venta`. A partial index on `minimo IS NOT NULL` is the obvious future
  addition **if** a real tenant's catalog makes it measurable; adding it speculatively would
  cost a migration for an unmeasured gain.
- **No `stock` PK or shape change.** The hottest cache table is untouched, as in stage 12.

#### Model summary for the gate

| Object | Change |
|---|---|
| `stock` | **NONE** — two existing nullable columns gain readers/writers |
| `movimientos_stock` | **NONE** — read only, for rotation |
| `articulos` / `proveedores` | **NONE** — `id_proveedor_habitual` read via existing FK |
| `parametros` | **NONE** (2 registry entries, no rows required) |
| Migrations | **ZERO** |

**No object of the database schema is created, altered or dropped by this stage. Any DDL that
`sdd-apply` would write is a scope violation and reopens the gate.**

## Affected Areas

| Area | Impact | Description |
|---|---|---|
| `src/Ways.Domain/Catalogos/ParametroConocido.cs` | Modified | 2 keys (decision 7, decision 1) |
| `src/Ways.Domain/Stock/` | New | Pure rule for the alert boundary / suggestion arithmetic, unit-testable without a DB (the `ReglaDeLotes` / `PoliticaDeRoles` pattern) |
| `src/Ways.Application/Stock/ServicioDeStock.cs` | Modified | `PUT /api/stock/minimos` write path: row-creation-without-movement, validations |
| `src/Ways.Application/Reportes/ServicioDeReportesDeStock.cs` | Modified | `minimo`/`reposicion`/`estado` on existencias; the reposición read model, its `/resumen` and its export projection; the rotation query |
| `src/Ways.Application/Reportes/ExportacionDeReportes.cs` | Modified | One mapper for the reposición table (stage-11 "one mapping and one route line") |
| `src/Ways.Api/Endpoints/StockEndpoints.cs` | Modified | `PUT /minimos` (Admin) |
| `src/Ways.Api/Endpoints/ReportesEndpoints.cs` | Modified | `/reportes/stock/reposicion`, `/reposicion/resumen`, `/reposicion/export` |
| `src/Ways.Web/src/paginas/Existencias.tsx` | Modified | Reorder columns + inline editing + articulo add-row |
| `src/Ways.Web/src/paginas/Reposicion.tsx` | New | List grouped by proveedor + download |
| `src/Ways.Web/src/paginas/Tablero.tsx` | Modified | Low-stock tile (`PanelDeVencimientos` shape) |
| `docs/11-programa-post-paridad.md` | Modified | Backlog row 367 re-registered (decision 5) |
| Database | **NONE** | See *Modelo de datos propuesto* |

## Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| **Nobody sets any minimum and the stage delivers an empty screen** — the real product risk of a threshold-based feature | High | `NULL` = unmanaged makes day one silent rather than noisy; the editor ships at **slice 3**, not last; `minimoSugerido` removes the blank-page problem (decision 1) |
| **A "plausible and wrong" figure** — the failure class stage 12's judgment-day season kept finding in read-only surfaces | Med | Every arithmetic rule is a named spec scenario: the `anulacion`-of-compra exclusion, the `<=` boundary at exactly `cantidad == minimo`, `sugerido` null instead of 0, "Sin proveedor" never dropped |
| **Rotation misleading for seasonal or newly-created articulos** (no history ⇒ suggested minimum 0) | Med | Advisory only, never gates an alert (decision 7); a zero-history articulo shows no suggestion rather than a suggestion of zero |
| **Alert fatigue from legally-negative stock** (negative balances are legal at the counter, legacy parity) | Med | Only articulos with a configured minimum can appear; the operator opts each articulo in |
| **The Existencias report drifting into a write surface** | Med | The write is a separate endpoint under a separate policy (`GestionDeCatalogo` vs `LecturaDeReportes`), asserted by a Supervisor-cannot-write test |
| **The tile diverging from the report** (stage-12's explicit anti-pattern) | Low | `/resumen` reuses the report method, never a second aggregation query — verbatim `ObtenerResumenDeVencimientosAsync` shape |
| **Per-PV maintenance burden** (N × M values by hand) | Med | Recorded honestly; bulk copy deferred by design until real usage shows the volume |
| **Scope creep back into conteo/variance or push** | Med | Both refused in writing with named reopen conditions (decisions 5 and 2), and the conteo carve-out is registered in doc 11, not just here |
| **The checkout budget eroding by accident** | Low | Decision 8 + a round-trip-count success criterion asserted by test |

## Rollback Plan

**Per slice**: every slice is additive code over an unchanged schema. Reverting a slice
leaves the two `stock` columns dormant exactly as they have been since Etapa 5.

**Runtime**: setting every `minimo` back to `NULL` silences the entire feature without
touching a single quantity, a single movement, or a single other row.

**Whole stage**: revert the code. **There is no migration to roll back and no irreversible
artifact of any kind** — unlike stage 12, whose `motivo_stock` enum values Postgres cannot
drop. This is the cheapest-to-abandon stage of the post-parity program so far, and that is a
deliberate property of the approach, not an accident.

## Dependencies

- **Stage 5** — `stock`, `movimientos_stock`, the two dormant columns, and the sum invariant
  this stage must not perturb.
- **Stage 8** — `id_comprobante_compra` on the ledger, without which the rotation motivo
  filter (decision 7) could not distinguish a sale anulación from a purchase anulación.
- **Stage 10** — the Tablero the tile lands on; `zona_horaria` and the `ResolverContextoAsync`
  precedent for the rotation window.
- **Stage 11** — the `TablaExportable` / `IExportadorDeTabla` / `/export`-sibling house
  standard, consumed verbatim ("one mapping and one route line").
- **Stage 12** — the pull alert channel (its decision 10), the `PanelDeVencimientos` tile
  shape, the three-layer report/tile/export template, and the "listado vs agregado acotado"
  distinction that tells this stage's export which cap shape to use.
- **Stage 3** — `articulos.id_proveedor_habitual`, complete and already read by
  `ServicioDeCompras`; grouping is a JOIN over an existing FK.
- No new NuGet package. No new web dependency. **No migration.**

## Success Criteria

- [ ] `dotnet ef migrations has-pending-model-changes` stays clean; the stage ships **zero**
      migration files.
- [ ] Setting a minimum for an articulo with **no** `stock` row creates the row with
      `cantidad = 0` and inserts **zero** `movimientos_stock` rows — asserted by test.
- [ ] With no minimum configured anywhere, the reposición report returns **zero rows** for a
      punto de venta holding hundreds of stocked articulos.
- [ ] The alert boundary is asserted at exactly `cantidad == minimo` (the row **appears**).
- [ ] `sugerido` is `null`, never `0`, when `reposicion` is unset.
- [ ] An articulo whose `id_proveedor_habitual` is NULL **appears** in the reposición report
      under "Sin proveedor" and in its export.
- [ ] The reposición tile's figures equal the report's, produced from the same method with no
      second aggregation query.
- [ ] The reposición export's figures equal the JSON endpoint's for identical parameters
      (stage-11's binding invariant), and the export refuses rather than truncates at cap.
- [ ] Rotation excludes `anulacion` rows carrying an `id_comprobante_compra`, proven by a
      mixed sequence containing a sale anulación **and** a purchase anulación.
- [ ] Rotation excludes `ajuste`, `inventario`, `decomiso`, `transferencia` and
      `reclasificacion`, proven by a mixed sequence.
- [ ] The rotation window's boundaries resolve in the punto de venta's `zona_horaria`, proven
      with a non-UTC zone (the stage-11 slice-9 bug class).
- [ ] A checkout issues **no more round-trips than before this stage** — query-count test,
      not inspection (decision 8).
- [ ] A Vendedor receives `403` on every new route; a **Supervisor reads** the reorder columns
      and receives `403` on the minimum write.
- [ ] `minimo < 0` and `reposicion < minimo` are rejected before reaching the database with
      their named codes.
- [ ] doc-11's backlog row 367 is updated to reflect the conteo carve-out (decision 5).
- [ ] Domain / Application / Integration / vitest suites green; descriptor tests for every new
      or modified screen (`web-descriptor-tests`).

## Plan de slices (tentative — `sdd-tasks` owns the final breakdown)

Stacked-to-main, one judgment-day round per slice (`protocolo-pr-solo-dev`).

| # | Branch | Content | ~lines |
|---|---|---|---|
| 1 | `feat/stage13-slice1-minimos-api` | 2 `ParametroConocido` keys; pure Domain rule (boundary + suggestion arithmetic); `PUT /api/stock/minimos` (Admin) with row-creation-without-movement and both validations; doc-11 backlog re-registration (decision 5) | ~300 |
| 2 | `feat/stage13-slice2-existencias-minimos` | `minimo`/`reposicion`/`estado` on the existencias report and its export | ~250 |
| 3 | `feat/stage13-slice3-web-minimos` | Existencias grid: reorder columns, inline editing, articulo add-row, descriptor tests | ~350 |
| 4 | `feat/stage13-slice4-reposicion` | The reposición read model (bajo mínimo ⋈ articulo ⋈ proveedor habitual, `sugerido`), endpoint + `/export` sibling | ~380 |
| 5 | `feat/stage13-slice5-rotacion` | Rotation query (motivo filter, zona-correct window, `dias_rotacion`) + rotation columns on the reposición report | ~330 |
| 6 | `feat/stage13-slice6-web-reposicion` | `Reposicion.tsx` grouped by proveedor + download button + descriptor tests | ~350 |
| 7 | `feat/stage13-slice7-tile-y-sugerencia` | Tablero low-stock tile (`/resumen`) + `minimoSugerido` column on the editor — **the designated droppable slice** | ~300 |

Merge order: `1 → 2 → 3 → 4 → 5 → 6 → 7`, with `{2,4}` and `{3,6}` foldable in parallel if
the apply run finds the files genuinely disjoint (report service vs web).

**Pre-approved degradation** (stage-12's decision-11 pattern): if slice 7 overflows,
**ship the tile and drop the `minimoSugerido` column**. The tile is the alert channel this
stage exists to extend; the suggested minimum is the assistive layer, and a fixed minimum
remains fully usable without it. Dropping it is a documented reduction, never a silent one.

**Review Workload Forecast (preliminary — `sdd-tasks` produces the binding one)**

- Estimated total: **~2 260 lines** across 7 slices.
- **Chained PRs recommended: Yes.** `chain_strategy: stacked-to-main`.
- **400-line budget risk: Medium.** Slices 4 and 6 sit closest to the cap; pre-identified
  split points: slice 4 at the report/export boundary, slice 6 at the grouping/download
  boundary, slice 5 at the query/report-columns boundary.
- **`size:exception` anticipated: No.** Unlike stage 12, no slice carries an unsplittable
  migration.
- **Decision needed before apply: No — already resolved**: `auto-chain` + `stacked-to-main`,
  honoured from `state.yaml` without reopening the question. As in every prior stage, overflow
  is expected to come from **test depth**, not scope.

## Deferred / adjacent (recorded, not in scope)

- **Full conteo snapshot/freeze/variance workflow** — carved out into its own change
  (decision 5) and re-registered in doc 11. Its first design question is already known: does
  variance measure against the frozen snapshot or against live stock at apply time?
- **Push alerts** — deferred with three named tripwires (decision 2). The reports built here
  are the payload any future push channel would send.
- **Order/draft generation from the reposición list** — Etapa 16 (decision 3).
- **`− en_transito` in the suggestion formula** — Etapa 16, when orders have state and an
  expected arrival (decision 4). Compras en borrador deliberately do **not** count.
- **Consolidated multi-punto-de-venta reposición** ("what do I buy from this supplier for all
  my locations") — the most likely first follow-up for a multi-PV tenant.
- **Bulk minimum operations** — copy from another punto de venta, mass import, percentage
  recalculation over a category. Deferred until real usage shows the volume (decision 6).
- **Persisting the computed minimum automatically** — refused in v1 (decision 1); would become
  an opt-in per articulo, additive.
- **A partial index on `minimo IS NOT NULL`** — the obvious first migration **if** a real
  catalog makes the scan measurable (gate section C).
- **Low-stock warning at the POS** — refused (decision 8), with its zero-round-trip
  implementation path recorded so a future owner knows the real cost.
- **Lot-aware or category-aware reorder points** — a lot is an expiry identity, not a
  replenishment unit.
- **Mass cost update from the reposición flow** — doc-06 roadmap row (doc-11:378) mentions
  "actualización masiva de costos" as a possible improvement inside this stage. **Not taken**:
  it is a compras write path with pricing semantics, unrelated to replenishment, and it would
  breach this stage's write-path constraint. Recorded so it does not vanish.
- **`articulos_empresas` replace-set concurrency gap** and the **importe CHECK micro-gate** —
  carried from stage 8, still open, untouched here.
- **`ways_owner` as a testcontainer superuser** — repo-wide weakness; **not relevant to this
  stage**, which ships no migration and no RLS policy.
- **Containment/import-boundary lint rule** — stage-10/11 carryover, unaffected.
- **stage-12 backlog items** (the `id_lote` `ThenBy` mutation target, the decomiso
  `ExigirObservaciones` wording, the missing 404 tests on lotes endpoints) — untouched; none
  intersects this stage.

## Proposal question round

Each records the assumption taken, so a correction is cheap. **None of these blocks
spec/design**; all are recorded for the owner.

1. **Is the reorder point a number you set, with the system's calculation shown only as a
   suggestion?** Assumed **yes** (decision 1). The alternative — a threshold that recomputes
   itself daily — is self-maintaining but unexplainable: an articulo alerts today and not
   tomorrow with no user-visible cause, and there is no stored value to audit. *This is the
   most product-weight call of the stage.*
2. **Should an articulo with no minimum configured stay silent?** Assumed **yes** — `NULL`
   means unmanaged, so day one is empty until the owner opts articulos in, rather than
   hundreds of alerts on deploy (decision 1).
3. **Does "bajo mínimo" include the articulo sitting exactly at the minimum?** Assumed
   **yes** (`cantidad <= minimo`) — reaching the reorder point *is* the signal, and it makes
   `minimo = 0` mean "tell me when it hits zero" (decision 1).
4. **Should the reposición list create a compra borrador, or stay a list + spreadsheet?**
   Assumed **list + spreadsheet** (decision 3). Generating a draft needs a unit cost this
   stage has no business deciding, and Etapa 16 will model ordering properly.
5. **Does the full inventory-count workflow (snapshot, freeze, variance) belong in this
   stage?** Assumed **no** — carved out into its own change and **re-registered in doc 11**
   (decision 5). It roughly doubles the stage, needs a migration this stage otherwise avoids
   entirely, and carries an unsettled product question (variance against the snapshot or
   against live stock while the POS keeps selling?).
6. **Should the cashier see "this sale leaves the articulo below minimum" at the counter?**
   Assumed **no** (decision 8) — it fires after the sale is committed, the cashier is not the
   person who reorders, and a second non-actionable warning next to stage 12's expired-lot
   warning trains operators to dismiss both.
7. **Is editing the minimum per punto de venta, from the Existencias screen, the right home?**
   Assumed **yes** (decision 6). The honest residue: N puntos de venta × M articulos values to
   maintain by hand, with bulk copy deferred until the real volume is visible.
8. **Alerts stay pull (screen + tile + spreadsheet), with no email/notification channel?**
   Assumed **yes** (decision 2), now with the second use case in hand as stage 12 required.
   Three tripwires that would reopen it are named.
