# Proposal: Stage 16 — Órdenes de compra

## Intent

doc-11:268-285 asks for the front half of the purchase circuit: **orden de compra → recepción
(total o parcial) → conversión a comprobante de compra**, with the states
`borrador / enviada / recibida parcial / cerrada / anulada`.

Today the circuit starts at *"ya compré"*. `explore.md` proves how literal that is:

| Today | Evidence | Verdict |
|---|---|---|
| The first row of a purchase is the invoice itself | `ServicioDeCompras.cs:148` — `CrearBorradorAsync` on `comprobantes_compra` | There is no record of what we asked for |
| The document we send the supplier does not exist | doc-10:405-406 — *"el número DEL PROVEEDOR… acá no hay correlativo propio"* | Every `comprobantes_compra` number is **theirs**; we emit nothing |
| The legacy never had one either | `alsina/facturacion.php?accion=nuevo` accumulated in `$_SESSION['compra']` and never INSERTed (doc-01:203-208) | **Greenfield.** No parity to replicate, no behaviour to preserve |
| The restock list can only be read | `reposicion-de-stock/spec.md:124-132` | *"qué reponer, por proveedor"* dead-ends in a screen |
| The suggestion formula is knowingly incomplete | `reposicion-de-stock/spec.md:130-132` — *"no order-with-state entity exists"* | Stage 13 named this stage as its unblocker, in its own spec text |

Three consequences the business already pays for. **Nothing is pending**: an order placed by phone
exists only in the operator's memory until the goods arrive, so a supplier who ships half and
forgets the rest is invisible. **Nothing is comparable**: the price we were quoted is gone by the
time the invoice is loaded, so a 12% increase between order and delivery is never noticed. And
**the reposición report cannot close its own loop**: it computes exactly what to buy from whom and
then hands it to a human to retype.

This stage is purely additive: it adds an intention **before** the fact, and it does not change
what a `comprobantes_compra` means or does.

## Scope

### In Scope

- **Two new tables** — `ordenes_compra` + `items_orden_compra` — and **one native enum**
  `estado_orden_compra` (5 values). One migration, fully additive (gate section).
- **The OC lifecycle**: `borrador` (mutable, full replace-set under `FOR UPDATE`, the
  `ActualizarBorradorAsync` pattern) → `enviada` → `recibida_parcial` → `cerrada`, plus `anulada`.
  Every transition through an `UPDATE … RETURNING` state-guarded statement as the **only**
  authority (decision 5), the same bar as `ConfirmarHeaderAsync`/`MarcarAnuladaAsync`.
- **Our own document number** (decision 4): `numero bigint`, per punto de venta, assigned **at
  `enviar`** by the **existing** `AsignadorDeNumeroComprobante` with `tipo_comprobante = 'OC'`.
  Zero new mechanism, zero new table, zero seed.
- **Reception = a linked comprobante de compra** (decision 1): `comprobantes_compra` gains
  `id_orden_compra NULL`. The receiving operator loads the remito as a compra borrador linked to
  the OC and confirms it. **`ConfirmarAsync`'s stock/lote/costo/CC engine is not modified** —
  it gains exactly one guarded call.
- **Received quantities are DERIVED, never stored** (decision 2): the pending quantity per
  artículo is `Σ cantidad_pedida − Σ cantidad recibida` over the OC's linked **confirmed**
  comprobantes. No accumulator column, no second writer, no drift on anulación.
- **The estado is a projection of that derivation** (decision 3), refreshed inside the existing
  transaction of `ConfirmarAsync` **and** `AnularAsync` by one containment class
  (`EscriturasDeOrdenDeCompra`), under the OC row lock taken **before** `proveedores`
  (decision 6 — the stage-15 lock invariant survives verbatim).
- **Manual close** (`POST /{id}/cerrar`) for an order the supplier will never complete, stamped
  with its actor — `id_empleado_cierre NOT NULL ⇒ a human decided`, and a manual close is
  **never** walked back by the projection.
- **Anulación governed by the book** (decision 9): allowed only while nothing was effectively
  received and no linked draft can still be confirmed.
- **Informational price deviation** (decision 8): the read model shows estimated vs real cost per
  line and per order. It never blocks a confirmation and costs **no column**.
- **Pre-load from the reposición list** (decision 10): `POST /api/ordenes-compra` accepts the rows
  the stage-13 report already returns (`IdArticulo`, `Sugerido`, grouped by
  `articulos.id_proveedor_habitual`). Read-only consumption; stage 13's engine is untouched.
- **API + web**: OC list/detail/draft/send/close/annul screens, the reception entry point, and the
  *"generar OC"* button on `Reposicion.tsx`.
- **doc 10 gains a §5-adjacent subsection** with the "Estado (Etapa 16)" annotation, written from
  inside slice 1 (the stage-12 task-1.17 discipline).

### Out of Scope

- **Stock en tránsito in the reposición formula** (decision 11). The inputs ship
  (`fecha_esperada` + derived pending quantities) and the formula stays byte-identical; only the
  spec sentence that justified the omission with *"no order-with-state entity exists"* is
  corrected, because that clause stops being true.
- **A blocking price-deviation control** (decision 8). Thresholds, approval flows and alerts are
  purchase policy the owner has not stated. Deferred with the reopen condition named.
- **A `recepciones_orden_compra` bridge table** (decision 1). It would need a second stock writer
  and break *"only `ConfirmarAsync` moves stock/costo/lotes/CC"*.
- **`cantidad_recibida` as a column** (decision 2) — refuted below with evidence.
- **Notas de crédito de proveedor / devolución al proveedor.** Still nonexistent (doc-10:109-110);
  an over-delivery is recorded honestly as received-and-not-ordered, not corrected.
- **Reserving stock or affecting `costo_nominal` from an OC.** An OC is an intention;
  `costo_unitario_estimado` is never a fact and never reaches `articulos.costo_nominal`.
- **Any change to `ConfirmarAsync`'s stock, lote, costo or cuenta-corriente steps**, to
  `movimientos_stock`, or to the stage-15 ledger. The OC touches no money: while it is an order
  there is no debt.
- **A new authorization policy** (decision 7) — the OC reuses the exact gate `/api/compras`
  already has.
- **Auditing OC transitions in `auditoria`.** Stage 14's first pass is a closed list; the OC's
  actors are stamped on its own rows (creator, sender, closer). Registered, not forgotten.
- **Printing/emailing the OC to the supplier.** The number and the screen ship; the PDF/mail
  channel is the cheapest first extension, recorded.
- **The owner's reserved items** and every carryover: the `importe` CHECK micro-gate, the
  `articulos_empresas` replace-set gap, `ways_owner` superuser, `stage-13b` conteo por planilla.
  Untouched.

## Capabilities

### New Capabilities

- **`ordenes-de-compra`** — owns the stage end to end: what an OC is and when it is mutable, the
  five states and who may write each, our own numbering and when it is consumed, the
  reception-is-a-linked-comprobante rule, the derived pending quantity and its grouping by
  artículo, the estado-as-projection rule with its lock position, manual close vs automatic close,
  the anulación rule expressed over the book, the informational price deviation, the pre-load from
  the reposición list, and the authorization gate.

One capability, following the stage-11/12/13/14/15 precedent: the surface is one document's
lifecycle, and splitting it would smear a single state machine across files.

### Modified Capabilities

- **`comprobantes-compra`** — three additions, no removal:
  - **ADDED**: a comprobante de compra MAY carry `id_orden_compra`; the link may be set or changed
    only while it is `borrador`, and the OC must belong to the **same tenant, proveedor and punto
    de venta** (service check under `FOR SHARE`, the `ExigirCompraLigableAsync` precedent).
  - **ADDED**: confirming a linked comprobante refreshes the OC's estado **in the same
    transaction**, and confirming against an `anulada` OC is refused (`409`).
  - **ADDED**: annulling a linked comprobante refreshes the OC's estado the same way; an
    automatically-closed OC may reopen to `recibida_parcial`/`enviada`, a manually-closed one may
    not.
  - **UNCHANGED**: the stock/lote/costo/`compra`-movement behaviour of confirmar and anular, the
    `numero_externo` uniqueness, the `gastosLigados` count, and the authorization gate.
- **`reposicion-de-stock`** — **MODIFIED, narrowly and only for honesty**: the requirement
  *"Suggested Quantity…"* justifies omitting stock en tránsito with *"that concept is structurally
  absent from this system's model (no order-with-state entity exists)"* (`spec.md:130-132`). After
  this stage the entity exists. The **formula and every scenario stay byte-identical**; the
  justification changes from *structurally absent* to *deliberately deferred, with the inputs now
  available*. Classified as a spec delta rather than "just a web button" precisely because the
  changed sentence is normative text, not commentary. The *"generar OC"* button changes **no**
  requirement of this capability — it is a consumer, and the pre-load rule belongs to the new
  capability.

**Not modified**: `operacion-de-pos` (decision 7 — no new policy, no claim change),
`cuenta-corriente-de-proveedores`, `saldo-de-proveedor`, `gastos`, `stock`,
`lotes-y-vencimientos`, `turnos-de-caja`, `auditoria-de-operaciones`, `arqueo-de-cierre`, and
every venta-side capability.

## Approach

**One document, one book, one projection, one guarded call into a proven engine.**

1. **The OC is an intention; the comprobante is the fact.** Everything that moves — stock, lotes,
   `costo_nominal`, the proveedor ledger — keeps moving *only* inside
   `ServicioDeCompras.EjecutarConfirmarAsync` (`ServicioDeCompras.cs:314-487`). The stage adds no
   second stock writer and no new `motivo_stock`.
2. **The reception book already exists.** It is `items_comprobante_compra` of the linked
   **confirmed** comprobantes. So the received quantity is a `SUM … GROUP BY id_articulo`, not a
   column (decision 2) — the same refusal stage 15 made for a cached `estado_pago`.
3. **The estado is a pure function of that book plus two human decisions** (manual close,
   anulación), so both write paths call the *same* projection and the state can never drift; an
   annulled reception walks the estado back correctly instead of leaving a lie.
4. **The coupling is one call, twice, behind a NULL check.** `ConfirmarHeaderAsync`'s existing
   `RETURNING` gains one column (`id_orden_compra`), and `EjecutarConfirmarAsync` /
   `EjecutarAnulacionAsync` gain one guarded call each. For a comprobante with no OC — 100% of
   today's traffic — the engine emits **zero** extra statements.
5. **The lock goes where the pinned invariant says it can.** `proveedores` is the LAST row lock of
   the transaction (stage-15 design decision 5, cited verbatim at `ServicioDeCompras.cs:469-472`),
   so the OC lock is taken immediately after the header lock, never after (decision 6).
6. **Reuse the numbering we already have.** `numeraciones_comprobante.tipo_comprobante` is a plain
   `varchar(30)` with **no FK to `tipos_comprobante`**
   (`NumeracionComprobanteConfiguration.cs:28-31, 54-65`), so `'OC'` needs no catalog row, no seed
   and no migration (decision 4).
7. **DB CHANGE GATE (CLAUDE.md), exercised in autonomous mode.** Two new tables, one new enum, one
   additive ALTER, **zero data statements**. The contract is the `Modelo de datos propuesto`
   section below.

## Autonomous decisions

Under delegated technical authority, conservative and reversible bias. Decisions 1-2 and 8-11
formalize the six `Orchestrator Decisions` at the foot of `explore.md`; 3-7 are the ones the
proposal had to resolve to make the model complete. Each records context, options with tradeoffs,
the decision, and **what it costs to reverse it**.

---

### 1 — **Reception does NOT move stock by itself**: each physical reception IS a comprobante de compra linked to the OC. Ratified.

**Context.** doc-11:282-285 leaves three coupled questions open: partial reception → one
comprobante or many; whether reception moves stock; how it interacts with lotes. Orchestrator
Decision 1 answers all three at once.

**Options.**

| Option | Pro | Contra |
|---|---|---|
| **A — one comprobante per reception, linked** | The proven engine is untouched: stock, `stock_lotes`, `costo_nominal`, the proveedor ledger and the lock order all keep working exactly as tested. Matches how Argentine deliveries actually arrive (each entrega brings its own remito/factura) | The OC needs 1:N cardinality (decision 12) |
| B — one consolidated comprobante at close | One invoice per order | Reception must move stock **before** any comprobante exists ⇒ a new stock writer, a new `motivo_stock`, a "recibido no facturado" zone, and `ck_comprobantes_compra_confirmada_completa` under tension. It would also fork the lote resolution path (`ServicioDeLotes.ResolverOCrearAsync` runs inside confirm) |
| C — a `recepciones_orden_compra` bridge table | Models goods and invoice arriving apart | All of B's costs plus a third table and a doubled gate surface |

**Decision.** Option A, verbatim. **Verified against the code**: the invariant this protects is
real and load-bearing — `movimientos_stock`, `stock_lotes`, `costo_nominal` and the stage-15
`compra` movement are written in exactly one place each
(`ServicioDeCompras.cs:441-482`), and its lock order carries a written warning against reordering
(`:469-472`). Option B would have to reproduce all of it.

**Cost of reversing.** Adding a bridge table later is additive: today's linked comprobantes remain
valid receptions and the bridge would describe *future* ones. Removing one after receptions were
recorded in it is a data migration with no honest source.

---

### 2 — **`cantidad_recibida` is DERIVED, not accumulated. The column does not ship.** (Refutation of the explore's tentative model.)

**Context.** `explore.md:49` proposes `items_orden_compra.cantidad_recibida numeric(12,3) NOT NULL
DEFAULT 0` and OD1 says *"la OC acumula `cantidad_recibida` por item a partir de los comprobantes
confirmados"*. The orchestrator asked this to be argued against single-truth.

**Options.**

| Option | Verdict |
|---|---|
| **Derive: `Σ items_comprobante_compra.cantidad` over linked confirmed comprobantes, grouped by `id_articulo`** | **Chosen.** The book already exists, is immutable-by-state and is already indexed by comprobante. One truth, zero writers, and **anulación is free**: the sum simply drops |
| Accumulate in a column with a single write authority | Rejected. It would need a **second** writer on the anulación path to decrement, and any missed path (a future direct edit of a confirmed compra, a repair script) leaves a silent permanent lie. doc-10 principle 7 allows a cached number only *with* its book — and here the cache would sit **next to** its book with no arithmetic advantage |
| Accumulate without a decrementer | Rejected outright: an annulled reception would leave the OC permanently over-received |

**Decision.** No column. The derivation is `SUM(cantidad) … WHERE c.id_orden_compra = X AND
c.estado = 'confirmada' GROUP BY id_articulo`, matched against `SUM(cantidad_pedida) GROUP BY
id_articulo` on the OC side.

**Grouping is by `id_articulo`, on both sides, deliberately.** `items_comprobante_compra` allows
two lines for the same artículo (`CalculadorDeCompra` already dedups costs by highest `orden`), so
matching line-to-line is impossible. Grouping both sides makes the derivation total and removes
any need for a `UNIQUE (id_orden_compra, id_articulo)` — which would otherwise be a **new 23505
family** with a race test, bought for nothing.

An artículo received but never ordered is **not an error**: it shows in the read model as
received-and-not-ordered, the same informational posture as the price deviation (decision 8).

**Cost of reversing.** Adding the column later is one additive migration plus a backfill computed
with **this exact derivation** — provable, the stage-15 backfill pattern. Removing it after it
drifted is a reconciliation project with no source of truth.

---

### 3 — **The estado is a projection of the book plus the human decisions**, refreshed by one containment class from both existing write paths.

**Context.** OD1 says the OC *"transiciona sola a `recibida_parcial`/`cerrada`"*. The orchestrator
asked who writes each transition and whether it happens inside `ConfirmarAsync`'s transaction.

**Decision.** `estado` is stored (decision 11 — native enum), and **every** transition goes through
an `UPDATE … RETURNING` state-guarded statement, but the two automatic values are computed from the
derivation of decision 2 rather than incremented:

```
proyectar(oc) =
    anulada                     if estado = 'anulada'                (terminal, never revisited)
    cerrada                     if id_empleado_cierre IS NOT NULL     (a human decided)
    cerrada                     if every ordered artículo is fully received
    recibida_parcial            if something was received
    enviada                     otherwise
```

Two properties follow, and both are the reason for this shape:

- **It is idempotent and repairable.** Running the projection twice changes nothing, and an estado
  that ever diverged can be recomputed from the book — a cached accumulator cannot.
- **Walking back is correct, not a bug.** Annulling the only reception of an automatically-closed
  OC returns it to `enviada`. A **manually** closed OC never moves, which is exactly what
  `id_empleado_cierre` distinguishes (the stage-15 `apertura` precedent: a NULL actor means *no
  human did this*).

**Who writes what:**

| Transition | Writer | Authority |
|---|---|---|
| → `borrador` | `POST /api/ordenes-compra` | `INSERT` |
| `borrador` mutation | `PUT /api/ordenes-compra/{id}` | `SELECT … FOR UPDATE … WHERE estado = 'borrador'` + replace-set (the `ActualizarBorradorAsync` pattern, `ServicioDeCompras.cs:195-263`) |
| `borrador → enviada` | `POST /{id}/enviar` | number assigned first (decision 4), then `UPDATE … WHERE estado = 'borrador' RETURNING` |
| `enviada ⇄ recibida_parcial ⇄ cerrada` | `EscriturasDeOrdenDeCompra.ProyectarEstadoAsync`, called by `EjecutarConfirmarAsync` **and** `EjecutarAnulacionAsync` | lock → re-read → `UPDATE … RETURNING` (decision 6) |
| `enviada/recibida_parcial → cerrada` (manual) | `POST /{id}/cerrar` | `UPDATE … WHERE estado IN ('enviada','recibida_parcial') RETURNING`, stamps `fecha_cierre` + `id_empleado_cierre` |
| `borrador/enviada → anulada` | `POST /{id}/anular` | `UPDATE … WHERE estado IN ('borrador','enviada') RETURNING`, guarded by decision 9 |

**Cost of reversing.** The projection is a containment decision: un-extracting it would be a
refactor nobody would propose. Replacing it with an accumulator is decision 2's rejected branch.

---

### 4 — **Our own number, reusing `AsignadorDeNumeroComprobante` with `tipo_comprobante = 'OC'`, consumed at `enviar`.** No new sequence, no `tipos_comprobante` row.

**Context.** The OC is the **first document this system emits to a supplier**;
`comprobantes_compra.numero_externo` is theirs (doc-10:405-406). The orchestrator asked whether to
reuse the assigner's pattern or build a dedicated sequence.

**Verified, not assumed.** `numeraciones_comprobante` is keyed `(id_punto_venta,
tipo_comprobante)` where `tipo_comprobante` is a plain `varchar(30)` whose **only** FKs are
`puntos_venta` and `tenants` (`NumeracionComprobanteConfiguration.cs:21-31, 54-65`). There is **no
FK to `tipos_comprobante`**, so `'OC'` needs no catalog row.

**Options.**

| Option | Verdict |
|---|---|
| **Reuse the assigner with `'OC'`** | **Chosen.** Zero schema change, zero seed, lazy row creation via `INSERT … ON CONFLICT DO NOTHING` + `UPDATE … RETURNING`, per-PV series, and its concurrency is already proven by `AsignadorDeNumeroComprobanteConcurrenciaTests`. Third reuse of the same mechanism (ventas TX/NCX, stage-7 `RC`) |
| A dedicated `numeraciones_orden_compra` table | Rejected: a second counter mechanism with the same semantics, plus a table, an FK and an index, to avoid a `varchar` value |
| Seed an `OC` row in `tipos_comprobante` | **Rejected on principle**: `tipos_comprobante` is a **global fiscal catalog** (`clase venta|compra`, `signo`, `es_fiscal`, `afecta_stock`). An OC is not a comprobante; putting it there would corrupt a padrón every fiscal read walks, and `ux_tipos_comprobante_codigo` is UNIQUE over `codigo` alone (doc-10:107-109) |
| No number at all (`OC-{id}`) | Rejected: `id_orden_compra` is a tenant-global identity, so the supplier-facing document would leak our aggregate volume and jump unpredictably |

**Decision.** Reuse, with the number consumed **at `enviar`**, not at draft creation: a draft that
is edited or discarded must not burn a number the supplier will never see. `numero` is therefore
`NULL` while `borrador` — the same nullable-until-committed shape
`comprobantes_compra.numero_externo` already has, with the same partial-unique treatment (gate §B).

**Honest residue, stated:** the assigner commits its own small transaction *before* the caller's
(`AsignadorDeNumeroComprobante.cs:29-32, 45-55`), so a failed `enviar` leaves a gap in the OC
series. That is the documented behaviour of the class (*"el número se consume aunque falle el
resto"*) and an OC series is not fiscal — a gap costs nothing. Reproducing it is preferable to a
fourth numbering semantics.

**Cost of reversing.** Moving to a dedicated table later is one additive migration plus a copy of
the counters. Removing the number after suppliers quoted it back to us is impossible.

---

### 5 — **`UPDATE … RETURNING` is the sole transition authority**, and the replace-set is the compras one, copied.

**Context.** The repo has one answer for state machines under concurrency and it is written twice
in the same file: `ConfirmarHeaderAsync` (`:715-750`) and `MarcarAnuladaAsync` (`:751-773`), plus
`BloquearBorradorAsync`'s `SELECT … FOR UPDATE` (`:774-790`) for mutation.

**Decision.** Same shapes, no invention. Every OC transition is a state-guarded `UPDATE … WHERE
estado = <expected> RETURNING`; **0 rows is the race loser** and is reclassified by re-reading the
current estado, exactly as `EjecutarConfirmarAsync:333-354` does. The draft replace-set locks with
`SELECT … FOR UPDATE … WHERE estado = 'borrador'`.

**Cost of reversing.** None: this is the repo's existing standard, and deviating is what would
cost.

---

### 6 — **Lock order: the OC row is locked immediately after the comprobante header, BEFORE `proveedores`.** And the projection needs a `SELECT … FOR UPDATE` *before* the recompute — a single self-referential `UPDATE` would be wrong under READ COMMITTED.

**Context.** Stage 15 pinned the invariant *"`proveedores` is the LAST row lock any transaction
takes, and the ledger `INSERT` follows it immediately"*, and `ServicioDeCompras.cs:469-472` carries
the warning verbatim. Adding a row lock **after** it would break the invariant that keeps three
write paths deadlock-free.

**Decision — new pinned total order:**

```
comprobantes_compra header  →  ordenes_compra  →  lotes  →  stock / stock_lotes
                            →  proveedores  →  ledger INSERT
```

The OC lock sits at position 2 because the header's `UPDATE … RETURNING` (step 1) has already
committed the comprobante's `estado = 'confirmada'` **within the transaction**, so the derivation
is complete from that instant on. No other path locks `ordenes_compra` together with anything
else: the OC's own endpoints (draft, enviar, cerrar, anular) take **only** the OC row, so no cycle
is reachable.

**A correctness trap the design MUST honour** (this is why the coupling is three statements, not
one): under `READ COMMITTED`, an `UPDATE … FROM (SELECT …)` that blocks on the OC row re-evaluates
**only the locked row** (`EvalPlanQual`) when the winner commits — its subquery keeps the snapshot
taken when the statement started. Two concurrent confirmations against the same OC would therefore
project from a stale book, and the loser would overwrite the winner's estado. The fix is the
pattern already in this file: **`SELECT … FOR UPDATE` first**, then re-read the derivation in a
**separate** statement (new snapshot, sees the winner's commit), then `UPDATE … RETURNING`.

**Cost of reversing.** Re-positioning a lock later means re-proving deadlock-freedom against every
write path existing at that moment — cheap now with four, expensive later. Two rendezvous tests
(confirm × confirm on one OC; confirm × anulación of a sibling reception) make the order observable
rather than asserted.

---

### 7 — **Authorization: no new policy.** The OC reuses `/api/compras`' exact gate — reads `OperacionDePos`, writes `OperacionDePos + GestionDeCatalogo`.

**Context.** The orchestrator asked this to be argued against `OperacionDePos`,
`GestionDeCatalogo` and the supervision policies.

**Verified.** `ComprasEndpoints.cs:20-22` puts the whole group under `OperacionDePos`; every write
(`POST`, `PUT`, `/confirmar`, `/anular`, `/precios`) stacks `Politicas.GestionDeCatalogo`
(`:76, 84, 92, 100, 109`). ASP.NET composes with AND, so **a compra is written by Admin only and
read by Vendedor/Supervisor/Admin**.

**Options.**

| Option | Verdict |
|---|---|
| **Mirror the compras gate exactly** | **Chosen.** The OC and the factura are two documents of one circuit; nothing in the business distinguishes who may order from who may load the resulting invoice. A cashier still *reads* what is on the way — the same asymmetry stage 15 chose (read for everyone, write for the gate) |
| A new `GestionDeCompras` policy | Rejected: it would fork the gate of a single circuit into two, and the first divergence between them would be an accident, not a decision. `Politicas.cs` gains a name only when a **new kind of risk** appears (the stage-15 criterion for `SupervisionDeCuentaDeProveedor`) |
| Stack a supervision policy on the OC | Rejected as **backwards**: an OC moves no stock, no cash and no debt. Gating the intention harder than the fact would mean a user could load an invoice but not the order preceding it |
| Writes under `OperacionDePos` alone | Rejected: it would let a Vendedor commit the business to a purchase, loosening a gate the compras circuit already set |

**Decision.** No change to `Politicas.cs`. Group `/api/ordenes-compra` under `OperacionDePos`;
`POST`/`PUT`/`enviar`/`cerrar`/`anular` stack `GestionDeCatalogo`.

**Cost of reversing.** Splitting later is one policy registration plus its call sites. Merging two
policies after they diverged requires deciding which callers were which.

---

### 8 — **Price deviation is INFORMATIONAL, and it costs no column.** Ratified, refined.

**Context.** doc-11:283 leaves it open; OD3 resolves it as informational.

**Decision.** Ratified — the comprobante is the fact and the OC the intention, so the real cost
never has to match. **Refinement**: it also needs **no schema at all**. The deviation is computed
in the read model from data that already exists — `items_orden_compra.costo_unitario_estimado` vs
the effective cost of the linked confirmed comprobantes' lines (the existing
`CalculadorDeCompra.CalcularCostoEfectivoDesdeItem`, so the comparison is IVA-consistent with
`costo_nominal`). No threshold column, no alert table, no approval flow.

Surfaced per line and per order, with an explicit *no comparable* state when
`costo_unitario_estimado IS NULL` — never `0`, the stage-13 `sugerido` discipline.

**Cost of reversing.** A blocking control later is a service rule plus a parameter; nothing in this
model prevents it. Shipping a threshold now would freeze a policy the owner has not stated.

---

### 9 — **Anulación of an OC is governed by the book, not by the state column.** Ratified, made precise.

**Context.** OD6: annul only from `borrador`/`enviada` with no confirmed receptions; an OC with
receptions is **closed**, never annulled.

**Decision.** Ratified, with the rule expressed over the derivation so it cannot drift:

- Allowed only when `estado IN ('borrador','enviada')` **and** the derived received quantity is
  **zero for every artículo** **and** no linked comprobante is in `borrador` (a draft that could
  still be confirmed). Otherwise `409 orden_compra_con_recepciones`.
- A reception that was confirmed and later annulled leaves a derived quantity of zero, so it does
  **not** block the annulment — the honest reading of *"nothing was effectively received"*, and
  the reason the rule is stated over the book rather than over history.
- **Second guard, defence in depth**: confirming a comprobante whose OC is `anulada` is refused
  (`409 orden_compra_anulada`), checked under the OC row lock that decision 6 already takes. This
  closes the race between an annulment and a concurrent confirmation.
- `anulada` is terminal: the projection never moves an OC out of it.

**Cost of reversing.** Loosening later (allowing annulment with receptions) is a service-rule edit.
Tightening after annulled orders already carry receptions would need a repair pass.

---

### 10 — **Integration with stage 13 is unidirectional: the OC pre-loads from the reposición list.** Ratified.

**Context.** OD5 and doc-11:186-188 (*"si la sugerencia de compra genera directamente una orden de
compra cuando exista la Etapa 16"* — stage 13 archived it as *"queda como listado"*).

**Decision.** `POST /api/ordenes-compra` accepts a normal item list; the web fills it from the
grouped restock rows (`agruparPorProveedor.ts` + `Reposicion.tsx`). **Nothing in stage 13 is
touched** — its zero-schema gate stays ratified and its endpoint keeps its shape
(`dto-contract-honesty`). The mapping is `FilaDeReposicion.{IdArticulo, Sugerido} →
{IdArticulo, CantidadPedida}`, filtered by proveedor.

**Two rules the pre-load inherits and must not lose:** the *"Sin proveedor"* bucket **cannot**
produce an OC (there is no supplier to send it to) and `sugerido = null` lines are excluded rather
than defaulted to `0` (the stage-13 honest-nulls rule).

**Cost of reversing.** Removing the button leaves the API intact. The direction we are *not*
taking (stage 13 writing OCs itself) would have reopened an archived capability.

---

### 11 — **`estado_orden_compra` is a native Postgres enum**, five values, declared in lifecycle order. Ratified.

**Context.** OD4, against stage 14's opposite call for `auditoria.accion` (`text` + CHECK).

**Both precedents are right, because the two columns are different animals.** doc-10 principle 4
forbids enums for **padrones editable by the user**; it *prescribes* them for state machines
(*"enum nativo de Postgres solo para estados de máquina de estados"*, doc-10:19-21).
`auditoria.accion` is an open catalog that grows every stage; `estado_orden_compra` is a closed
five-value state machine with one writer per transition, matched on by the projection and by every
listing filter. Its sibling `estado_compra` is already a native enum
(`ComprobanteCompraConfiguration.cs:69-72`), as are `estado_turno`, `motivo_stock`,
`tipo_movimiento_cc` and `tipo_movimiento_cc_proveedor`.

**Decision.** `CREATE TYPE estado_orden_compra AS ENUM ('borrador','enviada','recibida_parcial',
'cerrada','anulada')`, declaration order = C# member order, registered **only** via
`npgsql.MapEnum<EstadoOrdenCompra>()` in **both** `WaysDbContextFactory.cs` and
`DependencyInjection.cs`, **never** also with `HasPostgresEnum` (doc-10:451-454).

**No speculative value** — each of the five has a writer on day one (decision 3). A sixth value
(e.g. `recibida_total` distinct from `cerrada`) is deliberately absent: full reception *is* the
close.

**Cost of reversing.** Adding a value later is one irreversible-but-bounded `ALTER TYPE … ADD
VALUE` in its own migration (it cannot be referenced in the transaction that adds it — proven in
stage 12). Removing one is impossible, which is why none is speculative.

---

### 12 — **Cardinality 1 OC → N comprobantes via a composite FK on `comprobantes_compra`.** Ratified, with the alternate key made explicit.

**Context.** OD2 chooses the direct FK, mirroring `gastos.id_comprobante_compra`.

**Decision.** `comprobantes_compra.id_orden_compra integer NULL`, FK **composite**
`(id_orden_compra, id_tenant)` RESTRICT — like every other operativa FK in this schema, and unlike
a simple FK which would drop the cross-tenant guard. 1:N falls out for free: N receptions each
carry the same OC.

**Refinement the explore did not state**: the composite FK needs
`ak_ordenes_compra_id_orden_compra_id_tenant` on the **new** table. That is not a hidden ALTER —
it ships with the table (gate §B) — but it is a real object with its own implicit unique index, and
it is counted.

**Cost of reversing.** Dropping the column loses the link and nothing else: `comprobantes_compra`
keeps every effect it ever had. Going to a bridge table is decision 1's recorded direction.

---

## Modelo de datos propuesto

> **DB CHANGE GATE — this section is the contract.** It states the complete model at table level.
> Anything `sdd-apply` writes that is not here is a **scope violation that reopens the gate**. On
> implementation, **doc 10 is updated** (a §5-adjacent subsection + the "Estado (Etapa 16)"
> annotation), following the convention already used there.

**Gate verdict proposed: ONE migration**, named `OrdenesDeCompraEtapa16`. PostgreSQL 17.
**One new enum type. Two new tables. ONE additive ALTER over an existing table. ZERO data
statements.** Unlike stage 15, nothing here is non-additive and nothing is irreversible.

### A. New enum type — `estado_orden_compra`

```sql
CREATE TYPE estado_orden_compra AS ENUM
    ('borrador', 'enviada', 'recibida_parcial', 'cerrada', 'anulada');
```

Declaration order = C# member order of `EstadoOrdenCompra` (decision 11). Five values, one writer
each: `borrador` ← `POST /api/ordenes-compra`; `enviada` ← `POST /{id}/enviar`;
`recibida_parcial`/`cerrada` ← `EscriturasDeOrdenDeCompra.ProyectarEstadoAsync` (and `cerrada` also
← `POST /{id}/cerrar`); `anulada` ← `POST /{id}/anular`. **No speculative value.**

### B. New table — `ordenes_compra`

**Scoping category (doc 09): operativa** (`id_tenant` + `id_punto_venta` NOT NULL) — the same
category as `comprobantes_compra`, which is its sibling document. **No deviation**: unlike the
stage-14/15 tables, every OC belongs to exactly one punto de venta (the local the goods will reach)
and to exactly one actor.

**`EntidadBase`: YES** — and this is the argued difference against the two most recent precedents.
`movimientos_cuenta_corriente_proveedor` and `auditoria` refuse `EntidadBase` because an immutable
append-only movement has no `updated_at` and no soft delete. An OC is **the opposite**: it is
mutable throughout `borrador` (full replace-set), it is edited again at `enviar`, `cerrar` and
`anular`, and a draft that is abandoned needs the ordinary soft delete every mutable document in
this repo has. It therefore inherits `EntidadTenant` (hence `EntidadBase`) exactly like
`ComprobanteCompra`, with the standard tenant query filter and `EstamparTenant()` — no cloned
filter, no explicit `id_tenant` write.

```sql
ordenes_compra (              -- [operativa]
    id_orden_compra    integer     GENERATED BY DEFAULT AS IDENTITY,
    id_tenant          integer     NOT NULL,
    id_punto_venta     integer     NOT NULL,   -- a qué local llega la mercadería
    id_proveedor       integer     NOT NULL,
    id_empleado        integer     NOT NULL,   -- quién la creó
    numero             bigint      NULL,       -- correlativo propio por PV; se asigna al ENVIAR
    fecha_emision      timestamptz NOT NULL,   -- IRelojDelSistema, sin DEFAULT now()
    fecha_envio        timestamptz NULL,       -- cuándo salió al proveedor (par de `numero`)
    fecha_esperada     date        NULL,       -- ETA declarada (insumo del tránsito diferido)
    fecha_cierre       timestamptz NULL,       -- sólo con estado = 'cerrada'
    id_empleado_cierre integer     NULL,       -- NOT NULL ⇒ cierre MANUAL (nunca se revierte)
    observaciones      text        NULL,
    estado             estado_orden_compra NOT NULL,
    created_at, updated_at, deleted_at,
    CONSTRAINT pk_ordenes_compra PRIMARY KEY (id_orden_compra)
);
```

**16 columns** (13 + the 3 of `EntidadBase`). `numeric` types follow doc-10 principle 5 on the
child table. `fecha_emision` has **no `DEFAULT now()`**: `IRelojDelSistema` is the repo's single
time source and a DB default would silently defeat `RelojFijo` in tests (the stage-14/15
criterion). `numero` is `bigint` because `numeraciones_comprobante.proximo_numero` is (doc-10,
`AsignadorDeNumeroComprobante.cs:24-25`) — never `int`.

**Constraints:**

| Element | Name | Definition |
|---|---|---|
| PK | `pk_ordenes_compra` | `(id_orden_compra)`, `GENERATED BY DEFAULT AS IDENTITY` (`UseIdentityByDefaultColumn`) |
| AK | `ak_ordenes_compra_id_orden_compra_id_tenant` | `UNIQUE (id_orden_compra, id_tenant)` — required by the composite FKs from `items_orden_compra` and `comprobantes_compra` (decision 12). Same shape as `ak_comprobantes_compra_id_comprobante_compra_id_tenant` (`ComprobanteCompraConfiguration.cs:49-50`). **Structurally unviolable** (`id_orden_compra` is unique by identity) |
| FK 1 | `fk_ordenes_compra_tenant` | `(id_tenant) → tenants(id_tenant)` RESTRICT |
| FK 2 | `fk_ordenes_compra_punto_venta` | `(id_punto_venta, id_tenant) → puntos_venta(...)` RESTRICT |
| FK 3 | `fk_ordenes_compra_proveedor` | `(id_proveedor, id_tenant) → proveedores(...)` RESTRICT. The AK already exists (`ProveedorConfiguration.cs:151`) |
| FK 4 | `fk_ordenes_compra_empleado` | `(id_empleado) → usuarios(id_usuario)` RESTRICT — **simple, not composite**, the documented deviation of doc-10:563-567 (a composite AK would force `id_tenant NOT NULL` on `usuarios` and break the platform-staff NULL sentinel). Same criterion as `fk_comprobantes_compra_empleado` (`:130-134`) |
| FK 5 | `fk_ordenes_compra_empleado_cierre` | `(id_empleado_cierre) → usuarios(id_usuario)` RESTRICT, nullable, simple — same reason as FK 4 |
| CHECK 1 | `ck_ordenes_compra_envio_completo` | `((numero IS NULL) = (fecha_envio IS NULL)) AND (estado IN ('borrador','anulada') OR numero IS NOT NULL)` — the number and the send date arrive **together**, and every state past `enviada` has one. `anulada` is admitted without a number because a draft may be annulled before being sent (decision 9). Same family as `ck_comprobantes_compra_confirmada_completa` |
| CHECK 2 | `ck_ordenes_compra_cierre` | `((fecha_cierre IS NULL) = (estado <> 'cerrada')) AND (id_empleado_cierre IS NULL OR fecha_cierre IS NOT NULL)` — `cerrada` and `fecha_cierre` are the same fact, and a closer without a close date is irrepresentable (decision 3) |
| RLS | `ordenes_compra_tenant` | `migrationBuilder.HabilitarRlsDeTenant("ordenes_compra")` → `ENABLE` + `FORCE ROW LEVEL SECURITY` + `USING/WITH CHECK (app_es_plataforma() OR id_tenant = app_tenant_actual())`. **Standard policy, no deviation** |

**Indexes — counted from the start, including every `ForeignKeyIndexConvention` support index (the
lesson of stage 14's gate amendment 1 and stage 15's binding count):**

| # | Index | Columns | Role |
|---|---|---|---|
| 1 | `ix_ordenes_compra_tenant` | `(id_tenant)` | RLS predicate + support for FK 1. Mirrors `ix_comprobantes_compra_tenant` |
| 2 | `ix_ordenes_compra_punto_venta_fecha` | `(id_punto_venta, id_tenant, fecha_emision)` | The PV listing **and** support for FK 2 by leading-column prefix — exactly how `ix_comprobantes_compra_punto_venta_fecha` covers `fk_comprobantes_compra_punto_venta` (`:94-95`, no separate PV index exists there) |
| 3 | `ix_ordenes_compra_proveedor` | `(id_proveedor, id_tenant)` | The per-proveedor listing **and** support for FK 3. Mirrors `ix_comprobantes_compra_proveedor` |
| 4 | `ix_ordenes_compra_empleado` | `(id_empleado)` | Support for FK 4, **simple** (a composite index led by `id_tenant` would NOT cover a simple FK — the exact trap of stage 14's amendment) |
| 5 | `ix_ordenes_compra_empleado_cierre` | `(id_empleado_cierre)` | Support for FK 5, simple, same reason |
| 6 | `ux_ordenes_compra_numero` | `(id_tenant, id_punto_venta, numero)` **UNIQUE, PARTIAL** `WHERE numero IS NOT NULL` | Our own series is unique per PV. Partial because a draft has no number — the same shape as `ux_comprobantes_compra_numero_externo` (`:86-89`) and the same guarantee `comprobantes_venta` has over `(id_punto_venta, id_tipo_comprobante, numero)` |
| 7 | *(implicit)* `ak_ordenes_compra_id_orden_compra_id_tenant` | `(id_orden_compra, id_tenant)` | The unique index Postgres creates for the alternate key |

**FK-coverage audit (the binding count):** 5 FKs, 5 support indexes, **zero indexes added by the
convention that this contract did not name**. FK 1 → 1; FK 2 → 2 (prefix); FK 3 → 3; FK 4 → 4;
FK 5 → 5. No index is led by `id_tenant` except 1 (whose FK is `id_tenant` itself) and 6 (unique,
declared for its own reason). **Total on this table: 7 indexes + 1 PK.**

### C. New table — `items_orden_compra`

**Child scope: `id_tenant` only, no own FK to `puntos_venta`** (derived from the parent) — the
criterion `ItemComprobanteCompraConfiguration.cs:12-14` states verbatim. `EntidadBase`: **YES**,
like `items_comprobante_compra` (the replace-set rewrites them).

```sql
items_orden_compra (
    id_item                 integer     GENERATED BY DEFAULT AS IDENTITY,
    id_tenant               integer     NOT NULL,
    id_orden_compra         integer     NOT NULL,
    orden                   integer     NOT NULL,
    id_articulo             integer     NOT NULL,      -- mismo criterio que items_comprobante_compra
    descripcion             text        NOT NULL,      -- snapshot: el proveedor lee un nombre
    cantidad_pedida         numeric(12,3) NOT NULL,
    costo_unitario_estimado numeric(14,4) NULL,        -- intención, jamás un hecho
    created_at, updated_at, deleted_at,
    CONSTRAINT pk_items_orden_compra PRIMARY KEY (id_item)
);
```

**11 columns.** `numeric(12,3)` for quantities, `numeric(14,4)` for costs (doc-10 principle 5, the
`items_comprobante_compra` precision). **No `cantidad_recibida`** (decision 2). `id_articulo` is
**NOT NULL** for the same reason the compra item is: a line without an artículo cannot be received
into stock.

**Constraints:**

| Element | Name | Definition |
|---|---|---|
| PK | `pk_items_orden_compra` | `(id_item)` |
| FK 6 | `fk_items_orden_compra_tenant` | `(id_tenant) → tenants(id_tenant)` RESTRICT |
| FK 7 | `fk_items_orden_compra_orden_compra` | `(id_orden_compra, id_tenant) → ordenes_compra(...)` RESTRICT, against the AK of §B |
| FK 8 | `fk_items_orden_compra_articulo` | `(id_articulo, id_tenant) → articulos(...)` RESTRICT |
| CHECK 3 | `ck_items_orden_compra_cantidad_positiva` | `cantidad_pedida > 0` — mirrors `ck_items_comprobante_compra_cantidad_positiva` |
| CHECK 4 | `ck_items_orden_compra_costo_no_negativo` | `costo_unitario_estimado IS NULL OR costo_unitario_estimado >= 0` — `>= 0`, not `> 0`: a bonificación line is real (the `ck_items_comprobante_compra_costo_no_negativo` reasoning), and NULL means *not quoted* |
| RLS | `items_orden_compra_tenant` | `HabilitarRlsDeTenant("items_orden_compra")`. **Standard policy, no deviation** |

**Indexes:**

| # | Index | Columns | Role |
|---|---|---|---|
| 8 | `ix_items_orden_compra_tenant` | `(id_tenant)` | RLS + support for FK 6 |
| 9 | `ix_items_orden_compra_orden_compra` | `(id_orden_compra, id_tenant)` | Support for FK 7 |
| 10 | `ix_items_orden_compra_articulo` | `(id_articulo, id_tenant)` | Support for FK 8 + the per-artículo derivation of decision 2 |
| 11 | `ux_items_orden_compra_orden` | `(id_orden_compra, orden)` **UNIQUE** | Mirrors `ux_items_comprobante_compra_orden`. It does **not** cover FK 7 (second column differs), which is why index 9 exists — exactly the pair `items_comprobante_compra` already carries |

**FK-coverage audit:** 3 FKs, 3 support indexes, zero convention-added surprises.
**Total on this table: 4 indexes + 1 PK.**

### D. ALTER on `comprobantes_compra` — the link

```sql
ALTER TABLE comprobantes_compra ADD COLUMN id_orden_compra integer NULL;

ALTER TABLE comprobantes_compra
    ADD CONSTRAINT fk_comprobantes_compra_orden_compra
    FOREIGN KEY (id_orden_compra, id_tenant)
    REFERENCES ordenes_compra (id_orden_compra, id_tenant);   -- RESTRICT, MATCH SIMPLE
```

| Element | Name | Definition |
|---|---|---|
| FK 9 | `fk_comprobantes_compra_orden_compra` | `(id_orden_compra, id_tenant) → ordenes_compra(...)` RESTRICT, nullable, **MATCH SIMPLE** (the default): with `id_orden_compra` NULL the constraint is not checked — the `fk_auditoria_punto_venta` / `gastos.id_comprobante_compra` precedent |
| Index 12 | `ix_comprobantes_compra_orden_compra` | `(id_orden_compra, id_tenant)` — support for FK 9, declared explicitly with doc-10 naming instead of letting EF autogenerate `IX_comprobantes_compra_id_orden_compra_id_tenant` (the `ArticuloEmpresaConfiguration`/`NumeracionComprobanteConfiguration.cs:44-49` naming trap) |

Adding a **nullable** column with no default is a metadata-only change in PG 11+: **no table
rewrite**, no lock beyond the brief `ACCESS EXCLUSIVE` every `ALTER` takes, and existing rows keep
their exact meaning (an unlinked compra is a compra that came without an order — 100% of today's
rows and a permanently legitimate state).

**No CHECK ties `id_orden_compra` to `estado`**: a link is set while `borrador` and frozen
afterwards by the replace-set's own guard, and the proveedor/PV agreement is a **cross-table** rule
the schema cannot express — it is enforced in the service under `FOR SHARE`
(`ExigirCompraLigableAsync`'s pattern, `ServicioDeGastos.cs:187-197`).

### E. Error backstops (`db-error-backstops` APPLIES)

| New constraint | Client-input reachable? | Backstop |
|---|---|---|
| `ux_ordenes_compra_numero` (index 6) | **No** under normal operation — the only writer is `AsignadorDeNumeroComprobante`, whose atomicity is already proven | **`23505` mapping REQUIRED anyway, and it carries the ordering trap for the third time**: the name contains `_numero`, so `ClasificarUnicidad`'s generic `_numero` branch (the `ux_clientes_numero` family) would classify it wrong. It MUST resolve by **exact name, above** that call — the identical treatment `ux_comprobantes_venta_numero` (`ManejadorDeErrores.cs:127-129`) and `ux_comprobantes_compra_numero_externo` (`:136-138`) already document. Code: `numero_de_orden_duplicado`, 409. Tests: a raw out-of-band insert asserting `23505` **and** the translated domain code, plus a concurrency test of two simultaneous `enviar` on the same PV proving **two distinct numbers and no 409** |
| `ux_items_orden_compra_orden` (index 11) | No — `orden` is server-assigned inside the replace-set | Exact-name `23505` → `orden_de_item_duplicado`, 409, mirroring the `ux_items_comprobante_compra_orden` branch (`:144-146`). **Race-test exemption documented**, same family and same reason as that precedent |
| `ak_ordenes_compra_...` (§B) | **No** — structurally unviolable (`id_orden_compra` unique by identity) | **No `23505` mapping. Exemption documented** per the skill's gate table and the `ak_gastos_id_gasto_id_tenant` precedent (no `ak_*` in this repo has a mapping) |
| FK 3 `..._proveedor`, FK 2 `..._punto_venta` | **Yes** — both come from the request body | Service pre-checks 404 first (`ResolverProveedorAsync` / `ResolverPuntoVentaAsync`, `ServicioDeCompras.cs:1073-1081`; the ordering rule "an apocryphal PV must 404, never 409") + the generic `23503` → `400 referencia_invalida` prefix mapping (`:224`). One integration test per FK asserting the **translated domain code** |
| FK 8 `..._articulo` | **Yes** — item lines | Same shape as the compras draft validates today + generic mapping |
| FK 9 `..._orden_compra` (§D) | **Yes** — `idOrdenCompra` on the compra draft | Service pre-check under `FOR SHARE` (tenant + proveedor + PV agreement, decision 12) returning 404/409 before any write + generic mapping as backstop. **Race test**: linking to an OC being annulled concurrently |
| FK 1 / FK 6 `..._tenant` | No — session-derived | Generic mapping. **Exemption documented**; SQLSTATE-asserting test required anyway |
| FK 4 / FK 5 `..._empleado*` | No — always `contexto.UsuarioId`, server-derived; `usuarios` is soft-deleted so the row is never physically removed | Generic mapping. **Exemption documented** (the `fk_auditoria_actor` precedent) |
| FK 7 `..._orden_compra` (items) | No — the parent id of the same transaction | Generic mapping. **Exemption documented** |
| CHECK 1 / CHECK 2 | No — the service validates the transition before any write, and both columns are server-derived | Exact-name `23514` mappings following the `ck_comprobantes_compra_*` family already in `ManejadorDeErrores`, each proven by a raw-insert `23514` test so the constraint exists rather than being assumed |
| CHECK 3 / CHECK 4 | **Yes** — quantities and costs are request input | Service validation first (400 with a domain code), exact-name `23514` mapping as the out-of-band backstop, one test each |

**`ManejadorDeErrores.cs` IS MODIFIED** by this stage — unlike stage 15 — because
`ux_ordenes_compra_numero` introduces a genuinely new `23505` family **and** the third occurrence
of the `_numero` ordering trap. That branch must sit **above** the generic `ClasificarUnicidad`
call, with the same exact-match `when` shape the two existing traps use.

### F. Deliberate non-decisions (gate-relevant)

- **No `cantidad_recibida` column** (decision 2) and **no `UNIQUE (id_orden_compra, id_articulo)`**
  — the derivation groups both sides, so no artículo uniqueness is needed and **no second 23505
  family** is created.
- **No `estado_recepcion` / `porcentaje_recibido` / `total` column on the OC.** An OC's monetary
  total is an estimate derived from its lines; caching it would be a second truth about a number
  that is not even a fact (contrast `comprobantes_compra.total`, which **is** the invoice's).
- **No CHECK on `costo_unitario_estimado > 0`** and no `importe`-style CHECK — the `importe` CHECK
  micro-gate is an **open carryover the owner reserved** (carried since stage 12, listed untouched
  by stages 14 and 15). This stage does not pre-empt it.
- **No `ALTER TYPE … ADD VALUE` anywhere**, no new value on `estado_compra`, `motivo_stock`,
  `tipo_movimiento_cc_proveedor` or `categoria_gasto`. **Nothing irreversible ships.**
- **No data statement of any kind.** There is no history to backfill: the entity is greenfield and
  every existing `comprobantes_compra` row keeps `id_orden_compra NULL` legitimately.
- **No new `tipos_comprobante` row** (decision 4) and no change to `numeraciones_comprobante`'s
  schema.
- **No index on `estado`** — a five-value discriminator that composes with indexes 2 and 3; adding
  it speculatively is a migration for an unmeasured gain (the stage-13 gate criterion).
- **No partitioning, retention or TTL.** One row per order.
- **No change to `movimientos_stock`, `stock`, `stock_lotes`, `lotes`, `gastos`, `proveedores`,
  `articulos`, or the stage-15 ledger.**
- **No database-level immutability.** Same honest residue stages 14 and 15 recorded: theatre while
  `ways_owner` is a superuser.

**Ordering inside the migration**: `CREATE TYPE` → `CREATE TABLE ordenes_compra` (+ AK, FKs,
CHECKs, indexes) → `CREATE TABLE items_orden_compra` → `ALTER TABLE comprobantes_compra` (column,
FK, index) → `HabilitarRlsDeTenant` on **both** new tables, last (the stage-15 convention, kept
even though there is no data statement whose correctness could depend on it).

### Model summary for the gate

| Object | Change |
|---|---|
| `estado_orden_compra` | **NEW TYPE** — enum, 5 values (`borrador, enviada, recibida_parcial, cerrada, anulada`), each with a writer |
| `ordenes_compra` | **NEW TABLE** — 16 columns, 1 PK, 1 AK, **5 FKs**, **2 CHECKs**, **7 indexes** (incl. the AK's implicit one and 1 partial UNIQUE), RLS estándar, `EntidadBase` **YES** |
| `items_orden_compra` | **NEW TABLE** — 11 columns, 1 PK, **3 FKs**, **2 CHECKs**, **4 indexes** (1 UNIQUE), RLS estándar, `EntidadBase` **YES** |
| `comprobantes_compra` | **ALTER** — `+ id_orden_compra integer NULL` + composite FK + 1 support index (metadata-only, no rewrite) |
| Data statements | **ZERO** |
| Existing enums / types | **NONE** — no `ALTER TYPE`, nothing irreversible |
| `numeraciones_comprobante` / `tipos_comprobante` | **UNCHANGED** — `'OC'` needs no schema and no seed (decision 4) |
| `ManejadorDeErrores.cs` | **MODIFIED** — 2 exact-name `23505` branches (one of them the `_numero` ordering trap) + 4 exact-name `23514` branches (§E) |
| `Politicas.cs` | **UNCHANGED** (decision 7) |
| Migrations | **ONE** (`OrdenesDeCompraEtapa16`) |
| **New indexes, total** | **12** — 7 on `ordenes_compra` + 4 on `items_orden_compra` + 1 on `comprobantes_compra`, excluding the 2 new PK indexes |

## Affected Areas

| Area | Impact | Description |
|---|---|---|
| `src/Ways.Domain/Compras/` | New | `EstadoOrdenCompra`, `OrdenCompra`, `ItemOrdenCompra`, and `ProyectorDeEstadoDeOrden` — the pure function of decision 3, unit-testable without a DB (the `PoliticaDeRoles` pattern) |
| `src/Ways.Infrastructure/Migrations/` | New | `OrdenesDeCompraEtapa16` — the single migration of the gate section |
| `src/Ways.Infrastructure/Persistencia/Configuraciones/` | New + Modified | `OrdenCompraConfiguration`, `ItemOrdenCompraConfiguration` (shaped on `ComprobanteCompraConfiguration` / `ItemComprobanteCompraConfiguration`), `ComprobanteCompraConfiguration` (+ `IdOrdenCompra`, FK 9, index 12), `DbSet`s, `MapEnum` in **both** option builders |
| `src/Ways.Application/Compras/ServicioDeOrdenesDeCompra.cs` | New | Draft CRUD (replace-set under `FOR UPDATE`), `enviar` (numbering), `cerrar`, `anular`, and the read model with pending quantities + price deviation |
| `src/Ways.Application/Compras/EscriturasDeOrdenDeCompra.cs` | New | The ONE `SELECT … FOR UPDATE` + derivation + `UPDATE … RETURNING` of decision 6 — the single projection authority, called from both existing write paths |
| `src/Ways.Application/Compras/ServicioDeCompras.cs` | Modified | `id_orden_compra` in `ConfirmarHeaderAsync`'s `RETURNING`; **one guarded call** in `EjecutarConfirmarAsync` (after step 1, before lotes) and one in `EjecutarAnulacionAsync`; the link accepted and validated in the draft replace-set. **Steps 2-6 byte-identical** |
| `src/Ways.Application/Ventas/AsignadorDeNumeroComprobante.cs` | **Unmodified** | Reused with `tipo_comprobante = 'OC'` (decision 4). Its namespace stays put — a move would be a gratuitous diff, and stage 7 already reused it cross-domain |
| `src/Ways.Api/Endpoints/OrdenesDeCompraEndpoints.cs` | New | `/api/ordenes-compra`: `GET /`, `GET /{id}`, `POST /`, `PUT /{id}`, `POST /{id}/enviar`, `POST /{id}/cerrar`, `POST /{id}/anular` — group under `OperacionDePos`, writes stacking `GestionDeCatalogo` (decision 7) |
| `src/Ways.Api/Endpoints/ComprasEndpoints.cs` | Modified | The draft request gains an optional `idOrdenCompra`; no route, no policy and no response shape changes (`dto-contract-honesty`) |
| `src/Ways.Api/Seguridad/ManejadorDeErrores.cs` | Modified | Gate §E |
| `src/Ways.Web/src/paginas/OrdenesDeCompra.tsx` + `OrdenDeCompra.tsx` | New | List + detail/draft with pending quantities, price deviation and the reception entry point; `react-async-state` compliant, `web-descriptor-tests` covered |
| `src/Ways.Web/src/paginas/Reposicion.tsx` | Modified | A per-proveedor *"generar OC"* action feeding the draft (decision 10); the `"Sin proveedor"` bucket is rendered without it |
| `src/Ways.Web/src/paginas/Compras.tsx` | Modified | The linked OC shown on a compra, and the entry point from an OC to a new reception draft |
| `docs/10-modelo-de-datos.md` | Modified | The two new tables (a §5-adjacent subsection), `comprobantes_compra.id_orden_compra`, and the "Estado (Etapa 16)" annotations |
| `docs/11-programa-post-paridad.md` | Modified | Etapa 16 status block with its four open decisions resolved (orchestrator, outside the phase) |

## Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| **The projection reads a stale book under concurrency** (READ COMMITTED `EvalPlanQual`) and one confirmation overwrites another's estado | **High if unmanaged** | Decision 6 pins lock-then-re-read-then-update as three statements, never one; a confirm × confirm rendezvous test on the same OC makes it observable |
| **Breaking the stage-15 lock invariant** by taking the OC lock after `proveedores` | Med-High | The OC lock is pinned at position 2 with the total order restated (decision 6); the invariant's own warning comment (`ServicioDeCompras.cs:469-472`) is extended, not replaced |
| **Regressing the compra engine** while adding the coupling | Med-High | The coupling is one guarded call per method behind `id_orden_compra IS NOT NULL`; an unlinked confirm must emit **zero** extra statements, asserted as a test, and the existing confirm/anular suites must pass unchanged |
| **A manually-closed OC reopened by an annulled reception** | Med | `id_empleado_cierre NOT NULL` short-circuits the projection (decision 3), with a scenario per direction |
| **The derivation disagreeing with what the operator sees** (duplicate lines, substitutions, over-delivery) | Med | Grouping by artículo on **both** sides is stated as the rule (decision 2), with scenarios for duplicate OC lines, an artículo received but not ordered, and an over-delivery |
| **The `_numero` ordering trap struck a third time** | Med | Gate §E requires the exact-name branch above `ClasificarUnicidad` plus a test asserting the translated code, not the SQLSTATE alone |
| **A burnt OC number after a failed `enviar`** | Low | Accepted and documented (decision 4): an OC series is not fiscal. Made explicit so nobody "fixes" it by moving the assignment inside the main transaction and reintroducing the contention the class exists to avoid |
| **Scope creep into a bridge table, blocking price control, or stock en tránsito** | Med | All three refused in writing with reopen conditions (decisions 1, 8, 11 / Out of Scope) |
| **Reviewer overload** (schema + a touched engine + read model + two screens) | **High** | Six stacked-to-main slices with three pre-authorized split points, `judgment-day` before every PR |
| **Raw-ADO `DateTimeOffset` written without UTC normalization** (a real 500 in PR #129) | Low-Med | The projection writer copies the existing `ParametrosDeComando`/`AgregarParametro` usage; a test at a non-zero offset, since `RelojFijo` in `Z` cannot see this class of bug (stage-14 verify W2) |

## Rollback Plan

**Fully reversible, and cheaply — this stage is purely additive.**

**Per slice.** Slices 2-6 are additive code over an unchanged schema: reverting one removes a
surface or a write path and leaves both tables intact and consistent.

**Slice 1 (the schema).** Rollback is `ALTER TABLE comprobantes_compra DROP CONSTRAINT
fk_comprobantes_compra_orden_compra` → `DROP COLUMN id_orden_compra` → `DROP TABLE
items_orden_compra` → `DROP TABLE ordenes_compra` → `DROP TYPE estado_orden_compra`. In that order
there is **no dependent object**: nothing but `items_orden_compra` and the dropped column
references the OC, and no other column uses the type.

**Why nothing is destroyed.** No existing row is rewritten and no existing column changes meaning:
`id_orden_compra` starts NULL everywhere and stays NULL for every purchase that did not come from
an order. `movimientos_stock`, `costo_nominal` and the proveedor ledger are byte-identical whether
this stage ships or not, because the engine that writes them is unchanged.

**No irreversible database artifact of any kind**: no `ALTER TYPE … ADD VALUE`, no dropped column,
no rewritten row, no data statement.

**Whole stage.** Revert the code, run the five statements above, restore the doc-10 wording and the
`reposicion-de-stock` sentence.

## Dependencies

- **Etapa 8** (archived) — `comprobantes_compra` / `items_comprobante_compra`, `ServicioDeCompras`
  with its `UPDATE … RETURNING` authority and its replace-set, `CalculadorDeCompra`. The engine
  this stage feeds and must not disturb.
- **Etapa 15** (archived) — the pinned lock order this stage extends, and the containment/
  single-write-authority pattern `EscriturasDeOrdenDeCompra` copies.
- **Etapa 13** (archived) — `GET /api/reportes/stock/reposicion` and
  `agruparPorProveedor.ts`, consumed read-only (decision 10). Its zero-schema gate stays ratified.
- **Etapa 12** (archived) — lote resolution inside `ConfirmarAsync`, consumed unchanged: a
  reception resolves lotes exactly where it always did.
- **Etapa 5 / Etapa 1** — `AsignadorDeNumeroComprobante` + `numeraciones_comprobante`,
  `PoliticaDeRoles`, `Politicas`, the composite-FK/AK conventions.
- `IRelojDelSistema`, `IContextoDeUsuario`, `EstrategiaSinReintento`, `ManejadorDeErrores`,
  `HabilitarRlsDeTenant` — all existing, **no new wiring**.
- Skills: `db-error-backstops` (per constraint), `react-async-state` + `web-descriptor-tests` (web
  slice), `dto-contract-honesty`, `mutation-proof-tests` (incl. rules 11-12 born in stage 15),
  `work-unit-commits`, `judgment-day` before every PR.
- No new NuGet package, no new web dependency, no scheduler, no queue.

## Success Criteria

- [ ] Exactly **one** migration ships, named `OrdenesDeCompraEtapa16`; the only DDL is the gate
      section's and there is **no data statement**;
      `dotnet ef migrations has-pending-model-changes` is clean afterwards.
- [ ] The migration creates **exactly 12 new indexes** (7 + 4 + 1) and **no** unnamed EF-generated
      FK support index — verified against `pg_indexes`, the stage-15 discipline.
- [ ] RLS proven on both new tables: a tenant reading with another tenant's GUC sees **zero** rows;
      an INSERT with a foreign `id_tenant` is refused (`42501`), asserted by SQLSTATE.
- [ ] A draft OC has `numero IS NULL`; `enviar` assigns the next number **for that punto de venta**
      and two concurrent `enviar` produce **two distinct numbers with no 409**.
- [ ] `CHECK 1` and `CHECK 2` are proven by raw-insert `23514`, and every new FK by its translated
      `400 referencia_invalida`.
- [ ] `ux_ordenes_compra_numero` resolves to `numero_de_orden_duplicado` (**not** the
      `ux_clientes_numero` family) — the ordering trap proven by assertion, not by reading.
- [ ] Confirming a compra **without** `id_orden_compra` emits **zero** extra statements and leaves
      the existing confirm/anular suites green and unchanged.
- [ ] Confirming a linked reception moves the OC to `recibida_parcial`; confirming the remainder
      moves it to `cerrada`; both happen **in the same transaction** as the confirm, and a failure
      anywhere in that transaction leaves the OC untouched (fault-point test).
- [ ] Annulling the only reception of an automatically-closed OC returns it to `enviada`; annulling
      a reception of a **manually** closed OC leaves it `cerrada`.
- [ ] Two concurrent confirmations of two receptions of the same OC both commit, with **no
      deadlock** and an estado consistent with the book (the projection never reads a stale sum).
- [ ] Confirming a comprobante linked to an `anulada` OC is refused with `409
      orden_compra_anulada`.
- [ ] An OC with a derived received quantity of zero can be annulled even if it once had a
      reception that was later annulled; an OC with any effective reception, or with a linked
      draft, cannot (`409 orden_compra_con_recepciones`).
- [ ] Pending quantities are correct with duplicate OC lines for one artículo, with an artículo
      received but never ordered, and with an over-delivery — none of them an error.
- [ ] The price deviation shows *no comparable* (never `0`) when `costo_unitario_estimado IS NULL`,
      and **never** blocks a confirmation.
- [ ] Linking a compra draft to an OC of another proveedor, another punto de venta or another
      tenant is refused before any write; the link cannot be changed once the compra is confirmed.
- [ ] Authorization matrix: a Vendedor **reads** OCs (200) and **cannot** create, send, close or
      annul one (403); an Admin can. `Politicas.cs` is unchanged.
- [ ] The *"generar OC"* action pre-loads only rows with a proveedor and a non-null `sugerido`; the
      `"Sin proveedor"` bucket offers no OC. `GET /api/reportes/stock/reposicion` keeps its exact
      response shape.
- [ ] doc 10 carries both tables, the new column and the "Estado (Etapa 16)" annotations; the
      `reposicion-de-stock` spec sentence no longer claims that no order-with-state entity exists.
- [ ] Domain / Application / Integration / vitest suites green; descriptor tests for both new
      screens.

## Plan de slices (tentative — `sdd-tasks` owns the final breakdown)

Stacked-to-main, one `judgment-day` round per slice (`protocolo-pr-solo-dev`).

| # | Branch | Content | ~lines |
|---|---|---|---|
| 1 | `feat/stage16-slice1-schema` | The migration (type, 2 tables, 8 FKs, 4 CHECKs, 11 indexes, the ALTER + FK 9 + index 12, RLS last) + entities + EF configs + `MapEnum` in both builders + RLS/SQLSTATE/CHECK tests + doc 10 | ~460 |
| 2 | `feat/stage16-slice2-borrador-y-envio` | `ServicioDeOrdenesDeCompra` draft CRUD (replace-set under `FOR UPDATE`) + `POST/PUT/GET` + `enviar` with the `'OC'` numbering + the `ux_ordenes_compra_numero` backstop in `ManejadorDeErrores` + the concurrency test of the assigner | ~450 |
| 3 | `feat/stage16-slice3-ligadura` | `id_orden_compra` on the compra draft + its `FOR SHARE` validation + `EscriturasDeOrdenDeCompra` (lock → derive → `UPDATE … RETURNING`) + the two guarded calls in `ConfirmarAsync`/`AnularAsync` + the pinned lock order + the confirm × confirm and confirm × anulación races | ~430 |
| 4 | `feat/stage16-slice4-cierre-y-anulacion` | `POST /cerrar` (manual, actor-stamped) + `POST /anular` (the book-based rule) + the `anulada`-OC refusal inside confirm + the 409 matrix + the authorization matrix | ~300 |
| 5 | `feat/stage16-slice5-lectura` | Paginated list + detail read model: pending quantities per artículo, received-not-ordered, price deviation with its honest nulls | ~350 |
| 6 | `feat/stage16-slice6-web` | `OrdenesDeCompra.tsx` + `OrdenDeCompra.tsx` (list, draft, send, receive, close, annul) + the `Reposicion.tsx` *"generar OC"* action + the link on `Compras.tsx` + descriptor tests | ~470 |

Merge order `1 → 2 → 3 → 4 → 5 → 6`. Slice 1 blocks everything (it owns the only migration); 3
depends on 2 for the entity surface; 4 depends on 3 for the projection; 5 depends on 3 for the
derivation; 6 depends on 5.

**Pre-approved degradation** (the stage-12 decision-11 / stage-14 / stage-15 pattern), in priority
order:

1. **If slice 1 overflows** — split at the table boundary: `1a` (type + `ordenes_compra` +
   `items_orden_compra` + their tests) and `1b` (the `comprobantes_compra` ALTER + doc 10). The
   split keeps **one** migration, which is the invariant that must not be degraded.
2. **If slice 3 overflows** — split at the write-path boundary: `3a` (the link + validation + the
   projection class + the confirm call) and `3b` (the anulación call + both races).
3. **If slice 6 overflows** — ship the list, the detail and the draft, and drop the
   `Reposicion.tsx` action (the API still serves it). A documented reduction, never silent.
4. **Never degraded**: the projection's lock-then-re-read discipline, the zero-extra-statements
   proof for unlinked confirms, and the `_numero` ordering-trap assertion. An engine regression or
   a stale-book projection is worse than no OC at all, so those are split, never trimmed.

**Review Workload Forecast (preliminary — `sdd-tasks` produces the binding one)**

- Estimated total: **~2 460 lines** across 6 slices. **Calibrated against the programme's own
  record**: stages 13-15 consistently came in **1.5-3x** their naive production-code estimate
  because test depth (races, SQLSTATE assertions, fault points, descriptor tests) is what inflates
  a slice — every slice here carries at least one of those, and slice 3 carries three.
- `Decision needed before apply: No` — `auto-chain` + `stacked-to-main` already resolved in
  `state.yaml`.
- `Chained PRs recommended: Yes` — `chain_strategy: stacked-to-main`.
- `400-line budget risk: High` — slices 1, 2, 3 and 6 all sit at or above the cap on the estimate
  alone, so the calibration above says they *will* exceed it. Three split points are pre-authorized
  and a 7-8 PR outturn is the expected case, not the exception.
- `size:exception` anticipated: **No** — the splits absorb it.

## Refutaciones y refinamientos a las Orchestrator Decisions

All six are ratified in substance — the code supports every one. Two carry a correction the
orchestrator must arbitrate, and three claims inherited from the explore's tentative model **are
refuted with evidence**.

| # | Orchestrator Decision | Verdict |
|---|---|---|
| 1 | Reception does not move stock; each reception is a linked comprobante; the OC **accumulates `cantidad_recibida`** and transitions itself | **Ratified in substance, corrected in mechanism.** The engine-preservation half is confirmed against the code (`ServicioDeCompras.cs:441-482`). The accumulation half is **refuted**: see refutation 1 — the quantity is derived, not accumulated (decision 2), and the automatic transition is a **projection of that derivation** (decision 3), which is what makes an annulled reception self-correcting |
| 2 | 1 OC → N comprobantes via `comprobantes_compra.id_orden_compra NULL` | **Ratified, refined.** The FK must be **composite** `(id_orden_compra, id_tenant)` like every other operativa FK, which requires a **new alternate key on `ordenes_compra`** the explore never names (decision 12, gate §B). Unlike stage 15's §D, this is not an ALTER of an existing table — it ships with the new one |
| 3 | Price deviation informational, never blocking | **Ratified, refined**: it also needs **zero schema**. The deviation is computed in the read model from `costo_unitario_estimado` vs the existing `CalculadorDeCompra.CalcularCostoEfectivoDesdeItem` of the linked lines (decision 8) |
| 4 | `estado_orden_compra` = native enum, 5 values | **Ratified** with the argument the decision asked for: doc-10 principle 4 *prescribes* enums for state machines and forbids them for user-editable padrones; `auditoria.accion` is an open catalog growing every stage, this is a closed five-value machine with one writer per transition. Sibling `estado_compra` is already native (`ComprobanteCompraConfiguration.cs:69-72`) |
| 5 | The reposición formula is not touched; stock en tránsito deferred with the inputs ready | **Ratified for the formula, REFUTED for the spec.** `reposicion-de-stock/spec.md:130-132` justifies the omission with *"no order-with-state entity exists"* — a **normative claim that this stage makes false**. The formula and every scenario stay byte-identical, but the requirement text needs a narrow honesty amendment, so `reposicion-de-stock` **is** a modified capability, not an untouched one |
| 6 | Annul only from `borrador`/`enviada` with no confirmed receptions; otherwise close | **Ratified, made precise and hardened.** Expressed over the **derived** quantity rather than over history (so a confirmed-then-annulled reception does not block an honest annulment), extended to refuse when a linked **draft** could still be confirmed, and paired with a second guard — confirming against an `anulada` OC is refused under the same OC row lock — which closes the annul × confirm race the decision did not name (decision 9) |

**Refuted (explore's tentative model / open questions — not Orchestrator Decisions):**

1. **`items_orden_compra.cantidad_recibida numeric(12,3) NOT NULL DEFAULT 0` (`explore.md:49`) does
   NOT ship.** The reception book already exists (`items_comprobante_compra` of the linked
   confirmed comprobantes), so the column would be a **second truth about the same quantity**, and
   maintaining it would require a **decrementer on the anulación path** — a writer the explore never
   names. Stage 15 refused a cached `estado_pago` for exactly this reason. Derivation also removes
   the need for a `UNIQUE (id_orden_compra, id_articulo)` and therefore a whole new 23505 family
   (decision 2).
2. **The explore's FK list (`explore.md:53`) is incomplete.** It names five candidate FKs and omits
   the `id_tenant` FKs of **both** tables, the `id_empleado` FK, the closer FK, and the alternate
   key the composite link requires. The real shape is **9 FKs and 12 new indexes** (7 + 4 + 1),
   counted in gate §B/§C/§D — the stage-14 amendment-1 lesson applied from the start.
3. **The candidate lock position implied by "transiciona sola" is unsafe twice over.** First,
   placing the OC lock after `proveedores` would break the stage-15 invariant whose warning is
   written into the code (`ServicioDeCompras.cs:469-472`). Second, a single self-referential
   `UPDATE … FROM (SELECT …)` would project from a **stale snapshot** under READ COMMITTED
   (`EvalPlanQual` re-checks only the locked row), so two concurrent receptions of one OC would
   race. The pinned answer is `SELECT … FOR UPDATE` → re-read → `UPDATE … RETURNING`, at position 2
   of the total order (decision 6).

**New decisions the explore did not raise at all:** the OC's own numbering (decision 4 — the
explore's tentative model has no `numero` column, which would have left the document we send a
supplier identified by an internal identity id), the authorization gate (decision 7), and the
manual-vs-automatic close distinction (decision 3) without which an annulled reception would silently
reopen an order a human had deliberately closed.

## Proposal question round

Execution mode is `automatic-autonomous`, so these were resolved rather than asked. Each records the
assumption taken so a correction is cheap. **None blocks spec/design.**

1. **Does the supplier actually receive this document, or is the OC an internal note?** Assumed
   **the supplier receives it** — which is the only reason it needs a number of our own
   (decision 4). If it is purely internal, the number and its unique index are removable and slice
   2 shrinks.
2. **Does a delivery normally arrive with its own remito/factura?** Assumed **yes** (decision 1),
   the Argentine norm. If deliveries routinely arrive unaccompanied and are invoiced weeks later,
   option B/C reopens — and that is the single assumption that would most change this design.
3. **Is a price increase between order and invoice something to police or something to see?**
   Assumed **to see** (decision 8). A threshold that blocks confirmation is purchase policy the
   owner has not stated; adding it later is a service rule, not a migration.
4. **Who may commit the business to a purchase?** Assumed **the same person who may load its
   invoice** (Admin, decision 7). If ordering should be looser (a supervisor drafting, an admin
   sending), that is one policy plus a split of the write gate.
5. **Should an OC that will never be completed disappear or stay closed?** Assumed **stay closed**,
   actor-stamped (decision 3) — a purchase circuit's history is worth more than a tidy list.
6. **Is an artículo received but never ordered an error?** Assumed **no, informational**
   (decision 2) — a substitution is normal supplier behaviour and nothing in stock is wrong.
7. **Should the OC feed the reposición suggestion right away?** Assumed **not yet** (decision 11 /
   OD5): the inputs ship, the formula waits, and the reopen condition is the first customer who
   over-orders because the report ignores what is already on the way.
