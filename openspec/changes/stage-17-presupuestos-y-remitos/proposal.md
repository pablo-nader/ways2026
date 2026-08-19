# Proposal: Stage 17 — Presupuestos y remitos

## Intent

doc-11:307-324 asks for the **sale side of the document circuit**: a **presupuesto** that moves
neither stock nor cash, carries an expiry, is convertible into a sale and **keeps the price it
offered**; and a **remito** that delivers without invoicing, where **the stock leaves at delivery
time** and a later invoice **consolidates one or more remitos into a single comprobante**.

Today the sale circuit has exactly one shape: someone stands at the counter and pays. `explore.md`
proves how literal that is, and it also found something worse than a missing feature.

| Today | Evidence | Verdict |
|---|---|---|
| A quote does not exist as data | grep over `docs/01-features-existentes.md` and all of `alsina/`: **0 hits** for `presupuesto`/`remito` | **Greenfield.** No parity to port, no behaviour to preserve |
| A delivery without an invoice does not exist | `movimientos_stock.motivo` has 8 values, none of them a delivery (`MotivoStock.cs:18-28`) | Goods can only leave through a sale |
| The price offered to a customer survives nowhere | `ServicioDeVentas.cs:86-91` — the pricing engine is *"la autoridad de precio ÚNICA"*, resolved at checkout | Whatever was quoted yesterday is gone today |
| **`PRE` is seeded active and passes the POS gate** | `InicializadorDeBaseDeDatos.cs:79` seeds `('PRE','Presupuesto', …, afecta_stock false)` with `activo = true`; `ServicioDeVentas.ResolverTipoComprobanteAsync` (`:923-937`) only evaluates `!Activo`, `Clase != Venta` and `EsFiscal` | **A latent phantom sale lives in `main`** |

That last row is the one that ordered this stage's first slice. `afecta_stock` **is read nowhere in
any write path** — grep over `Ways.Application` returns one comment (`ServicioDeCuentaCorriente.cs:287`)
and two read-model projections. So a `POST /api/ventas` with `codigoTipoComprobante = "PRE"` and real
product lines passes the resolver today and `EjecutarTransaccionAsync` decrements stock and consumes
cuenta corriente exactly as if it were a `TX`. The reason `RC` — the other `afecta_stock = false`
type — never had this problem is **structural, not declarative**: `RC` is emitted by
`ServicioDeCuentaCorriente.EjecutarTransaccionAsync` (`:275-344`), never reaches
`ResolverTipoComprobanteAsync`, and carries zero items by construction.

Three consequences the business already pays for. **Nothing can be promised**: a price quoted over
the counter or by phone exists only in the operator's memory, so a customer who returns three days
later gets today's price and an argument. **Nothing can be delivered on account**: goods that leave
the store must be invoiced at that exact moment, which is why the delivery-address modal of stage 5
(`comprobantes_venta.direccion_entrega`) is as far as the circuit ever got. And **the padrón lies**:
a type documented as *"presupuesto: no"* against `afecta_stock` (doc-10:89) is one HTTP request away
from behaving like a ticket.

This stage is **purely additive to the sale engine**: `ServicioDeVentas.EjecutarTransaccionAsync`
keeps its pinned statement order and, for an ordinary counter sale, emits **zero extra statements**.

## Scope

### In Scope

- **Four new tables** — `presupuestos` + `items_presupuesto`, `remitos` + `items_remito` — **two new
  native enums** (`estado_presupuesto`, `estado_remito`), **one `ALTER TYPE … ADD VALUE`** on
  `motivo_stock` (`remito`, the ninth), **two additive ALTERs** (`comprobantes_venta`,
  `movimientos_stock`) and **three data statements**. Two migrations (gate section, decision 11).
- **Closing the `PRE` finding, with two independent nets** (decision 2): the seeded `PRE` row is
  **deactivated** (idempotent data statement **and** a seed change — both, or a fresh database stays
  incoherent), and `ResolverTipoComprobanteAsync` gains **one boolean clause** refusing any tipo with
  `afecta_stock = false`. The transaction lambda is **not touched**.
- **The presupuesto lifecycle**: `borrador` (mutable, full replace-set under `FOR UPDATE`) →
  `enviado` → `convertido`, plus `anulado`. Own number per punto de venta assigned **at `enviar`**
  through the **existing** `AsignadorDeNumeroComprobante` with `tipo_comprobante = 'PRES'` — zero
  schema, zero seed (`NumeracionComprobanteConfiguration.cs:28-31, 54-65`, verified).
- **Expiry as the governing mechanism, and `vencido` as a DERIVED state** (decision 3):
  `vencimiento date NOT NULL` from `enviado` onward, compared against a *"hoy"* resolved in the
  punto de venta's own `zona_horaria` — the binding criterion `lotes-y-vencimientos/spec.md:318-320`
  already imposes on the vencimientos report. **No `vencido` enum value, no scheduler.**
- **Conversion with the price frozen** (decision 4): `GET /api/presupuestos/{id}/para-venta` pre-loads
  the POS for display, and `POST /api/ventas` accepts `idPresupuestoOrigen`. When it is present, the
  **presupuesto's own snapshot replaces the pricing engine as the price authority** — the price still
  comes from the server, never from the cart — the lines cannot be edited, and the presupuesto is
  marked `convertido` by **one state-guarded `UPDATE … RETURNING`** inside the same transaction.
- **The remito as the FOURTH formal stock write site** (decision 6): `ServicioDeRemitos` with its own
  `borrador → emitido → facturado` lifecycle plus `anulado`, its own series `'REM'`, FEFO resolution
  in the decide phase, and the **same lock order implemented independently, with its own concurrency
  test**. The `stock` capability's *"identical at all three write sites"* guarantee is **amended to
  four, explicitly**, as a capability delta.
- **Consolidated invoicing, non-fiscal, shipped in this stage** (decision 7):
  `POST /api/remitos/facturacion` emits **one itemless comprobante** of a **new tipo `TXR`**
  (`afecta_stock = false`) that links N remitos, takes payments and cuenta corriente exactly like a
  sale, and writes **zero stock movements** — the goods left at remito time. The **fiscal** type of
  that consolidation stays deferred to Etapa 19 (doc-11:373).
- **Links by FK, never by copy** (decision 8): `comprobantes_venta.id_presupuesto_origen NULL`
  (1:1, guaranteed by a partial unique index) and `remitos.id_comprobante_venta NULL` (N:1), both
  **composite** against alternate keys — `ak_comprobantes_venta_id_comprobante_venta_id_tenant`
  **already exists** (`ComprobanteVentaConfiguration.cs:40-41`, verified as stage 15 verified
  `gastos`); `presupuestos` and `remitos` ship their own.
- **API + web**: `/api/presupuestos` (list, detail, draft, `enviar`, `para-venta`, `anular`),
  `/api/remitos` (list, detail, draft, `emitir`, `anular`) and `/api/remitos/facturacion`; the
  presupuesto and remito screens, and the POS entry point for a conversion.
- **doc 10 gains the four tables** and the *"Estado (Etapa 17)"* annotations, written from inside the
  schema slices (the stage-12 task-1.17 discipline).

### Out of Scope

- **Stock reservation by a presupuesto** (decision 5). A quote is a quote, not a hold. There is no
  model support and adding it would be a fifth stock writer for a business case nobody has stated.
  Deferred with the reopen condition named.
- **Repricing at conversion.** doc-11:309 says *"conserva el precio ofrecido"*, textually. The
  reliquidación of Etapa 7 is **not** replicated; expiry is the governance mechanism instead
  (decision 4).
- **Editing the cart of a converted quote.** A conversion emits the presupuesto **exactly**. Adding
  or altering a line means an ordinary sale (live prices) or a new quote — decision 4 argues why the
  middle ground is the expensive one.
- **The fiscal consolidation type** (doc-11:373). Etapa 19 owns which fiscal comprobante an invoice
  over remitos is; this stage ships the circuit with the non-fiscal `TXR` and the relationship ready.
- **`presupuesto → remito` directly.** doc-11:309 says *"convertible en venta"*. A quote that must be
  delivered before being invoiced is quote → sale, or quote → (manual) remito. Registered, not built.
- **Partial conversion of a presupuesto** (converting half the lines). All or nothing; the state
  `convertido` is what makes 1:1 honest.
- **Rentabilidad / reportes de gestión over the remito circuit.** The cost snapshot **does** ship on
  the remito line (it is unrecoverable after the goods leave — the stage-9 argument); the *report*
  consumption is deferred, because a report can be computed at any later date and a cost cannot.
- **A new authorization policy** (decision 10) — both documents reuse the exact gate `/api/ventas`
  already has.
- **Auditing presupuesto/remito transitions in `auditoria`.** Stage 14's first pass is a closed list;
  actors are stamped on the documents' own rows and on the stock ledger.
- **Printing/emailing a quote or a delivery note to the customer.** The number and the screen ship;
  the PDF/mail channel is the cheapest first extension, recorded.
- **The owner's reserved items** and every carryover: the `importe` CHECK micro-gate, the
  `articulos_empresas` replace-set gap, `ways_owner` superuser, `stage-13b` conteo por planilla.
  Untouched.
- **Any change to the checkout's pinned statement order**, to `AsignadorDeNumeroComprobante`, to the
  stage-15 proveedor ledger, or to `ServicioDeCompras`.

## Capabilities

### New Capabilities

- **`presupuestos`** — what a quote is and when it is mutable, its four states and who writes each,
  its own numbering and when it is consumed, the expiry rule and the timezone in which *"hoy"* is
  resolved, the frozen-price snapshot, the conversion contract (price authority, immutability of the
  lines, the 1:1 guarantee), and the authorization gate.
- **`remitos`** — what a delivery note is, its four states, its numbering, the FEFO resolution and
  the stock exit at `emitir`, the reversal at `anular`, the **product-only line rule**, and the
  consolidated invoicing contract: the itemless `TXR` comprobante, the N:1 link, and the rule that
  **the goods leave exactly once**.

Two capabilities, following the stage-11/12/13/14/15/16 precedent: each is one document's lifecycle,
and merging them would smear two independent state machines into one file.

### Modified Capabilities

- **`comprobantes-venta`** — four additions and one honesty amendment:
  - **ADDED**: the POS checkout MUST refuse any `tipos_comprobante` row with `afecta_stock = false`
    (`400 tipo_comprobante_invalido`), independently of the request's lines.
  - **ADDED**: a comprobante MAY carry `id_presupuesto_origen`; at most one comprobante per
    presupuesto (partial unique index); when present, the presupuesto's snapshot — never the request —
    supplies prices, discounts, IVA and the customer.
  - **ADDED**: a comprobante of tipo `TXR` consolidates N remitos, **carries zero items**, and writes
    **no** `movimientos_stock`. Its printed detail is the remitos' own frozen items.
  - **ADDED**: annulling a `TXR` comprobante returns its remitos to `emitido` and clears their link,
    in the same transaction; it reverses cuenta corriente exactly as today and **must not** create
    stock (the item loop is empty by construction).
  - **AMENDED for honesty**: *"RC Joins The POS-Emittable Tipos"* (`spec.md:104-109`) claims RC is
    emittable *"through the same `ServicioDeVentas` checkout entry point as TX/NCX"*. The code says
    otherwise (`ServicioDeCuentaCorriente.cs:275-363` owns it end to end, with its own
    `ResolverTipoRcAsync`), and after decision 2 that sentence would read as a promise the resolver
    now breaks. The requirement is corrected to *"emittable through the POS surface, by its own
    service"* — **normative text made false by this stage**, the same class of amendment stage 16
    made to `reposicion-de-stock`.
  - **UNCHANGED**: the numeración atomicity, the snapshot immutability of items, the credit-limit
    gating, the anulación stock reversal **for comprobantes that carry items**, and the
    authorization gate.
- **`stock`** — three deltas, no removal:
  - **MODIFIED — *"Lock Order Extends To The Lot Dimension, Identical At All Three Write Sites"***
    (`spec.md:178-189`) becomes **four write sites**: `ServicioDeVentas`, `ServicioDeCompras`,
    `ServicioDeStock` **and `ServicioDeRemitos`**, with the contract that made the rule honest
    carried over verbatim — the same `ORDER BY id_articulo, id_punto_venta, id_lote NULLS FIRST`,
    **implemented independently**, with **its own concurrency test**, and **the duplication is not
    refactored away**.
  - **MODIFIED — *"Stock Schema At Rest"***: `movimientos_stock` gains a nullable `id_remito`
    (composite FK, `ON DELETE RESTRICT`), populated only for `motivo = remito` rows and for the
    `motivo = anulacion` rows that reverse them; every other motivo leaves it NULL.
  - **MODIFIED — *"Cantidad Is Always The Sum Of Its Movimientos"***: restated over **nine** motivo
    values (`remito` joins the eight).
- **`lotes-y-vencimientos`** — **MODIFIED, narrowly**: *"FEFO Is The Server-Computed Default"*
  (`spec.md:175-186`) is written over *"a sale line"*. A remito line of a lot-effective artículo
  resolves FEFO by the **same rule, in the same decide-then-commit read phase**, and freezes
  `id_lote` on `items_remito` exactly as a sale item does. The ordering, the honour-when-supplied
  rule and the one-lot-per-line rule are **byte-identical**; only their subject widens.
- **`auxiliary-catalogs`** — **MODIFIED**: the platform-managed `tipos_comprobante` padrón ships
  `PRE` with `activo = false` (a code with no writer is not offered) and gains `TXR`
  (`clase venta`, `letra 'X'`, `signo +1`, `discrimina_iva false`, `es_fiscal false`,
  **`afecta_stock false`**). The read-only-for-tenants rule is unchanged.

**Not modified**: `operacion-de-pos` (decision 10 — no new policy, no claim change), `precios`,
`ofertas`, `resolucion-de-ofertas` (consumed unchanged), `turnos-de-caja`, `consumo-cuenta-corriente`,
`transferencias-de-stock`, `conteo-de-inventario`, `ordenes-de-compra`, `comprobantes-compra`, and
every proveedor-side capability.

## Approach

**Two documents, two own tables, one untouched checkout, and one honest rule: the goods leave
exactly once.**

1. **The checkout is preserved by construction, not by discipline.** Both documents live in their own
   tables, so `EjecutarTransaccionAsync` never learns what a quote or a delivery note is. Its pinned
   statement order (`ServicioDeVentas.cs:762-919`) is unchanged, and an ordinary counter sale emits
   **zero** extra statements — the same bar stage 16 held for `ServicioDeCompras.ConfirmarAsync`.
2. **The `PRE` hole is closed before anything is built.** Two independent nets (decision 2): the
   catalog row stops being offered, and the resolver stops accepting the whole class. Either one
   alone would work; both are cheap; and a mutation test binds each one separately, so removing
   either fails the suite.
3. **The presupuesto's promise is the price, and expiry is what bounds it.** The quote freezes what
   the pricing engine resolved at the moment it was sent; the conversion replays that snapshot rather
   than re-resolving. What stops a three-month-old price from being honoured is not a repricing rule
   but the **vencimiento**, evaluated in the punto de venta's own timezone.
4. **The remito is the fourth stock writer and says so out loud.** The `stock` spec's *"three write
   sites"* is a promise about how the lock order is proven, not a cap on the number of writers.
   Extending it means writing the fourth implementation independently and giving it its own
   concurrency test — which is exactly what the original rule demanded of the other three.
   Reusing the checkout's loop would couple the untouchable service; that is the worse trade.
5. **Consolidated invoicing works because the comprobante is itemless.** The goods already left under
   the remito's own movements; a comprobante with items would be reversed into existence by
   `AnularAsync` (which reverses `items_comprobante_venta` unconditionally). Zero items makes the
   double-decrement trap and the phantom-restock trap **unreachable**, not merely avoided — and it is
   how an invoice over remitos actually reads in the field: *"Factura por Remitos N° …"*.
6. **Reuse the numbering we already have, for the third, fourth and fifth time.**
   `numeraciones_comprobante.tipo_comprobante` is a plain `varchar(30)` whose only FKs are
   `puntos_venta` and `tenants` (verified, `NumeracionComprobanteConfiguration.cs:28-31, 54-65`), so
   `'PRES'`, `'REM'` and `'TXR'` cost **zero schema and zero seed** — `TXR` needs a
   `tipos_comprobante` row only because it **is** a comprobante de venta, unlike an OC.
7. **DB CHANGE GATE (CLAUDE.md), exercised in autonomous mode.** Four tables, two enums, one
   irreversible `ALTER TYPE … ADD VALUE`, two additive ALTERs, three data statements, **30 new
   indexes counted**. The contract is the `Modelo de datos propuesto` section below.

## Autonomous decisions

Under delegated technical authority, conservative and reversible bias. Decisions 1-8 formalize the
seven `Orchestrator Decisions` at the foot of `explore.md`; 9-13 are the ones the proposal had to
resolve to make the model complete. Each records context, options with tradeoffs, the decision, and
**what it costs to reverse it**.

---

### 1 — **Presupuesto = its own table** (`presupuestos` / `items_presupuesto`), a structural mirror of stage 16's OC. Ratified.

**Context.** OD1. The alternative is a `comprobantes_venta` row of tipo `PRE`.

**Options.**

| Option | Pro | Contra |
|---|---|---|
| **A — own table** | The checkout stays byte-identical **by construction**; the `PRE` phantom sale disappears because there is no writer to reach; own states and expiry without forcing `estado_comprobante` (binary `emitido \| anulado`, doc-10 §4); series `'PRES'` free | A second materialization of a price snapshot; the seeded `PRE` row is left orphaned (decision 2 resolves it) |
| B — `comprobantes_venta` of tipo `PRE` | No new table | Requires `vencimiento`, `convertido` and a mutable-draft concept on **the hottest table in the system**, and above all requires `EmitirAsync` to know how *not* to move stock and cuenta corriente — the exact statement-level surgery the stage-16 criterion forbids |

**Decision.** Option A, verbatim. **Verified against the code**: the stock loop
(`ServicioDeVentas.cs:866-885`) and the cuenta-corriente loop (`:890-914`) are unconditional over the
plan's items and pagos. Option B's *"don't move anything"* has no place to live except inside them.

**Cost of reversing.** Collapsing quotes into `comprobantes_venta` later is a data migration with a
natural source (the quote rows) but no reason to run. Going the other way after `PRE` comprobantes
existed would mean splitting a live table.

---

### 2 — **The `PRE` finding is closed in this stage, with two nets — and the guard is unconditional on `afecta_stock`, not conditional on product lines.** (Substance of OD2 ratified; its *mechanism* refuted.)

**Context.** OD2 asks for (a) `activo = false` on the seeded `PRE` row and (b) a surgical guard in
`ResolverTipoComprobanteAsync` reading *"`!tipo.AfectaStock` **with product lines** ⇒ 400"*.

**Refutation of (b)'s shape, with evidence.** The resolver runs at `ServicioDeVentas.cs:67`, **40
lines before** `MaterializarItems` (`:107`) and before `articulos` is even loaded (`:98-100`) — and
`EsProducto` is an artículo attribute. A line-conditional guard therefore needs a signature change, a
second query or a reordering of the decide phase, all to buy a **weaker** rule: it would still admit
an itemless `PRE` sale, a semantics nobody wants.

**The unconditional form is safe, and this was verified rather than assumed.** The only other
`afecta_stock = false` type today is `RC`, and `RC` **never reaches this resolver**:
`ServicioDeCuentaCorriente.ResolverTipoRcAsync` (`:358-363`) resolves it inside its own service.
So the guard's blast radius is exactly the set of types nobody may emit at the counter.

**Decision — two independent nets:**

- **Net 1, the catalog.** `UPDATE tipos_comprobante SET activo = false WHERE codigo = 'PRE';`
  (idempotent, the exact shape the `RC`/`C-*` migrations already use in their `Down`,
  `CuentaCorrienteEtapa7.cs:74`). **The row is deactivated, never deleted** — it is a global padrón
  and doc-10:83 documents its code.
- **Net 1b, the seed — mandatory, and this is the part that is easy to miss.** The seeder only runs
  against an **empty** database and executes **after** migrations
  (`InicializadorDeBaseDeDatos.cs:432`), so on a fresh install the data statement matches zero rows
  and the seeder then inserts `PRE` with `Activo` defaulted to `true`. **Deactivating without
  touching the seed leaves every new tenant with the hole reopened.** `TiposComprobanteBase` gains an
  explicit `Activo` field, `false` for `PRE` alone. Both, or neither is worth shipping.
- **Net 2, the resolver.** One clause added to the existing boolean chain at `ServicioDeVentas.cs:930`:
  `|| !tipo.AfectaStock`. No new statement, no signature change, no new error code — the same
  `400 tipo_comprobante_invalido`. The transaction lambda is **not touched**.

**Binding mutation tests** (the `mutation-proof-tests` contract): net 1 is proven by a test that
asserts a freshly seeded database has `PRE` inactive **and** by a `POST /api/ventas` with `"PRE"`
returning 400; net 2 is proven by an **out-of-band insert of an active, non-fiscal, venta-class type
with `afecta_stock = false`** followed by a 400 — a test that **still fails** if the clause is removed
even with `PRE` deactivated, which is what makes the two nets independently binding rather than
mutually masking.

**Cost of reversing.** Reactivating `PRE` is one `UPDATE` and one tuple edit. Removing the resolver
clause is one line — and the mutation test above is what makes that removal loud.

---

### 3 — **`estado_presupuesto` has FOUR values; `vencido` is DERIVED, never stored — and `vencimiento` is a `date`, resolved in the punto de venta's zona horaria.** (Refutation of the explore's tentative five-value enum.)

**Context.** `explore.md:51` proposes `borrador|enviado|vencido|convertido|anulado`. The orchestrator
asked the `date` vs `timestamptz` question to be argued against the repo's UTC normalization
convention.

**Why `vencido` does not ship as a value.** No writer exists for it. Making a quote expire *by
itself* requires a scheduler, and this repo has none — stage 16 recorded *"no scheduler, no queue"*
as a dependency fact. A stored `vencido` would therefore be either a lie until someone touched the
row, or a new piece of infrastructure bought for a boolean. Expiry is a **pure function**:

```
vencido(p) = p.estado = 'enviado' AND p.vencimiento < hoy(zona del punto de venta)
```

which is the same posture as stage 16's derived pending quantity, stage 15's refusal of a cached
`estado_pago`, and — exactly on point — `ReglaDeLotes.EstaVencido(DateOnly? fecha, DateOnly hoy)`
(`ReglaDeLotes.cs:90`), a domain function with no database column behind it.

**`date`, not `timestamptz` — argued against the convention.** The repo's rule is that **instants**
are `DateTimeOffset` normalized to UTC (the global Npgsql convention born from the 500 in PR #129).
A `vencimiento` is **not an instant**: it is the calendar day printed on a document the customer
reads. `FechaDelRango` (`Exportacion/FechaDelRango.cs:9-16`) documents the exact failure mode this
avoids — a local `23:59:59.999-03:00` lands in the **next** UTC day, so a quote stamped *"válido
hasta el 30/09"* stored as an instant would die at 21:00 on the 30th for an Argentine customer and
live an extra day for one at `+05:30`. The precedent is not a preference, it is a **binding verify
criterion** already written into a spec: *"'Hoy' MUST be resolved in the punto de venta's own
`zona_horaria` parametro, never in server/UTC time — this is a binding verify criterion, not a
nicety"* (`lotes-y-vencimientos/spec.md:318-320`). `DateOnly → date` is also the established mapping
of `ordenes_compra.fecha_esperada`, `comprobantes_compra.fecha_comprobante` and `lotes.fecha_vencimiento`.

**Decision.** `CREATE TYPE estado_presupuesto AS ENUM ('borrador','enviado','convertido','anulado')`
— four values, declaration order = lifecycle = C# member order, **one writer each**. `vencimiento
date`, NULL only while `borrador`, NOT NULL from `enviado` (CHECK 1). *"Hoy"* is resolved through the
`zona_horaria` parametro of the punto de venta (`ParametroConocido.ZonaHoraria`, the
`ServicioDeReportesDeVentas.ResolverZonaAsync` pattern) and never from `DateTime.UtcNow`.

**No CHECK comparing `vencimiento` with `fecha_emision`.** The comparison crosses a `date` and a
`timestamptz` and is therefore only meaningful **inside a timezone**, which a CHECK cannot carry
immutably. The service validates `vencimiento >= hoy(zona)` at `enviar`; the schema stays honest
about what it can actually guarantee.

**Cost of reversing.** Adding a fifth value later is one irreversible-but-bounded `ALTER TYPE … ADD
VALUE` plus the writer that justifies it. Widening `date` to `timestamptz` later is a rewriting
migration **and** a semantic change to every quote already printed.

---

### 4 — **Price frozen, and the presupuesto REPLACES the pricing engine as the price authority for a conversion.** The lines of a converted quote are immutable. (OD4 + OD7 ratified in substance, made precise.)

**Context.** doc-11:309 says *"conserva el precio ofrecido"* textually, and OD7 says the conversion
pre-loads the POS from the presupuesto side with *"cero cambio al checkout"*.

**The trap the explore did not name.** `ServicioDeVentas` re-resolves every price server-side —
*"la autoridad de precio ÚNICA, nunca lo que mostró el carrito"* (`:86-91`). If `/para-venta` merely
pre-filled the cart and the POS posted it as an ordinary sale, the checkout would **silently reprice**
to today's list, and the stage's central promise would be broken by the very mechanism meant to
deliver it.

**Options.**

| Option | Verdict |
|---|---|
| Accept `precioUnitario` from the request when a quote is present | **Rejected.** It punches a hole in the single price authority — the one rule stage 5 defended hardest — and it makes the cart trustworthy for money |
| A dedicated emission path for conversions (a second checkout) | **Rejected.** It would duplicate stock, lotes, pagos, cuenta corriente and numeración: a **fifth** stock write site for a document that is an ordinary sale |
| **The quote's own snapshot is the price authority** | **Chosen.** When `idPresupuestoOrigen` is present, the decide phase reads the presupuesto's items **server-side** and skips `ServicioDeOfertas.ResolverAsync` for those lines. The price still comes from the server; it just comes from an earlier server decision |

**Decision.** `SolicitudDeVenta` gains an optional `idPresupuestoOrigen`. When present:

- `lineas` MUST be absent or empty (`400`), per `dto-contract-honesty` — a field that would be
  ignored is not accepted.
- The customer is the quote's customer; a conflicting `idCliente` is refused rather than silently
  overridden.
- `precio_unitario`, `descuento`, `id_lista_precio`, `id_oferta`, `porcentaje_iva` and
  `id_alicuota_iva` come **frozen** from `items_presupuesto`.
- Everything downstream is **unchanged and shared**: the `articulos` snapshot, `EsProducto`, the
  FEFO/lote decision, `ValidadorDePagos`, and the whole transaction.
- `costo_unitario` is frozen from **today's** `costo_nominal`, not from quoting time — the stage-9
  rule is *the cost at emission*, and a quote never froze a cost.
- The conversion is refused (`409 presupuesto_vencido` / `presupuesto_no_convertible`) when the quote
  is expired, not `enviado`, or already converted.

**Why the lines cannot be edited.** A quote that can be modified on the way to a sale is a quote that
promised nothing, and the alternative — per-line reconciliation between "quoted" and "sold" — is the
most complex machinery of Etapa 7 rebuilt for a document that has no obligation to carry it. If the
customer wants something else, the operator sells normally or quotes again.

**Cost of reversing.** Allowing edits later is a service rule plus a per-line provenance flag.
Removing the freeze after customers were quoted is a promise already broken.

---

### 5 — **A presupuesto reserves NO stock.** Ratified, deferred with its reopen condition.

**Context.** doc-11:321 leaves it open; OD3 resolves it as no reservation.

**Decision.** Ratified. A quote is a price commitment, not a hold. There is no model support (no
`motivo_stock` value, no reserved-quantity column — grep confirmed), and adding one would create a
**fifth** stock writer plus a release path (expiry, cancellation, partial conversion) for a business
rule the owner has not stated. The honest residue is stated rather than hidden: **a quote may be
converted into a sale that drives stock negative**, which the system already allows at the counter
(`stock/spec.md:75-79`, *"negative stock is allowed (legacy parity)"*).

**Cost of reversing.** Adding reservations later is a new table plus a release path — additive, and
the quote rows already carry the quantities it would need. Removing reservations after operators
relied on them is a promise withdrawn.

---

### 6 — **Remito = its own table with `ServicioDeRemitos` as the FOURTH formal stock write site**, and the `stock` guarantee is amended to four explicitly. Ratified.

**Context.** OD5. `stock/spec.md:178-189` says the lock order *"MUST be implemented identically and
independently at all three write sites … the duplication is not refactored away"*. Stage 16
deliberately avoided becoming the fourth; this stage cannot.

**Options.**

| Option | Verdict |
|---|---|
| **Own table + own service, fourth write site** | **Chosen.** The remito carries items and moves stock; it is a genuine write path. The spec's rule is a **method of proof**, not a cap: honour it by writing the fourth implementation independently, with its own concurrency test and the same intentional duplication |
| A `comprobante_venta` of a new tipo `REM` with a sibling service | Rejected. It is not the `RC` case (RC has zero items): it would duplicate the stock loop anyway, only inside `comprobantes_venta`, and inherit the binary `estado_comprobante` that cannot express `facturado` |
| Reuse `ServicioDeVentas`' stock loop from the remito service | Rejected. Extracting the loop couples the untouchable service to a new caller and destroys the "independent implementation" property the spec asks for — the duplication is the design, not an accident |

**Decision.** `ServicioDeRemitos.EmitirAsync` is the fourth write site, with the lock order
`ORDER BY id_articulo, id_punto_venta, id_lote NULLS FIRST` implemented **independently**, the
aggregate `stock` upsert before its `stock_lotes` rows, and **its own rendezvous test** (a remito and
a checkout competing for the same artículo and lot). The spec requirement's title and body are
amended to *"…At All Four Write Sites"*, naming `ServicioDeRemitos`.

**New motivo, and its irreversibility register.** `ALTER TYPE motivo_stock ADD VALUE 'remito'` — the
ninth value, additive, the stage-12 precedent (`decomiso`/`reclasificacion`). **IRREVERSIBLE,
ACCEPTED**: Postgres cannot remove an enum value. It is accepted because the value **ships with its
writer** in the same stage (unlike stage 12, where two values waited slices for theirs), and because
the alternative — reusing `ajuste` or `venta` — would make the ledger lie about why the goods left.
A remito's anulación reverses with `motivo = anulacion` carrying the same `id_remito`, the exact
compra precedent.

**A remito line MUST be a product** (`EsProducto = true`, `400` otherwise). A service is not loaded
onto a truck. This makes *"every remito line moves stock"* a **total** rule and removes the
skip-entirely branch the checkout needs (`ServicioDeVentas.cs:867`).

**Cost of reversing.** Removing the fourth write site means removing the remito. The enum value stays
forever — that is the price, paid knowingly.

---

### 7 — **Consolidated invoicing ships in this stage, non-fiscal, as an ITEMLESS comprobante of a new tipo `TXR` that writes ZERO stock.** (A decision the explore left implicit — and where two real traps live.)

**Context.** doc-11:311 puts consolidation inside this stage's scope (*"la facturación posterior
consolida uno o varios remitos en un comprobante"*); doc-11:373 defers only **which fiscal
comprobante** it is, to Etapa 19.

**Trap 1 — double decrement.** If the consolidation were an ordinary `POST /api/ventas` with the
remitos' lines, the checkout would decrement stock **a second time** for goods that already left.

**Trap 2 — phantom restock.** If the consolidation comprobante carried items, `AnularAsync` would
insert inverse movements for every item with `id_articulo NOT NULL`
(`comprobantes-venta/spec.md:130-133`) and **create stock that does not exist**.

**Decision.** The consolidation comprobante is **itemless by construction**, exactly like `RC`
(`ServicioDeCuentaCorriente.cs:287-325`, *"cero items por construcción"*). Both traps become
**unreachable**, not merely avoided:

- `POST /api/remitos/facturacion` takes N remito ids of the **same tenant, cliente and punto de
  venta**, all `emitido` and unlinked, locks them in **ascending `id_remito`** order, emits one
  comprobante of tipo **`TXR`** with `subtotal/descuento_total/total` summed from their frozen lines,
  writes pagos and — if cuenta corriente was used — the `Consumo` movement through the existing
  `EscriturasDeCuentaCorriente`, and links the remitos with **one state-guarded
  `UPDATE … WHERE estado = 'emitido' RETURNING`** whose row count must equal the request's (any
  other count is the race loser → `409`).
- It writes **zero** `movimientos_stock` rows. The invariant is stated positively: **the goods leave
  exactly once** — at the counter for an ordinary sale, at the remito for a delivery, never both.
- The printed detail is the remitos' own immutable items, joined in the read model — doc-10 principle
  6 is satisfied because those items are themselves frozen snapshots.
- It requires an **open turno** (it takes money), unlike the remito itself (decision 13).

**Why a new `tipos_comprobante` row, when stage 16 refused one for the OC.** Stage 16's refusal was
*"an OC is not a comprobante"*. A consolidated invoice **is** a comprobante de venta; the padrón is
its correct home. `TXR` carries `afecta_stock = false` — which is now *true in the strong sense*
(decision 2's guard makes it unemittable at the counter, exactly like `PRE` and `RC`) — and it makes
the comprobante self-describing in every listing, export and read model. It also buys the cheapest
possible discriminator for the annulment path: `MarcarAnuladoAsync`'s existing
`UPDATE … RETURNING id_punto_venta` (`ServicioDeVentas.cs:746-757`) gains **one column**
(`id_tipo_comprobante`) — the stage-16 `ConfirmarHeaderAsync` pattern verbatim — so the un-link call
runs **only** for a `TXR` and an ordinary sale emits **zero** extra statements.

**Cost of reversing.** Etapa 19 replacing `TXR` with a fiscal type is additive: existing `TXR`
comprobantes stay valid history, and the consolidation path gains a type parameter. Dropping
consolidation entirely after remitos exist would strand delivered goods as permanently unbillable.

---

### 8 — **Links by FK, no bridge tables** — 1 presupuesto → ≤1 venta (guaranteed by a partial unique index), N remitos → 1 comprobante. Ratified, with the alternate keys verified.

**Context.** OD6, mirroring stage 16's `comprobantes_compra.id_orden_compra`.

**Verified, not assumed.** `comprobantes_venta` **already has**
`ak_comprobantes_venta_id_comprobante_venta_id_tenant` (`ComprobanteVentaConfiguration.cs:40-41`), so
`remitos.id_comprobante_venta` needs **no new alternate key** on the hot table — the check stage 15
ran against `gastos`, run here and passed. `presupuestos` and `remitos` ship their own AKs for the
FKs pointing at them.

**Decision.**

- `comprobantes_venta.id_presupuesto_origen integer NULL`, composite FK
  `(id_presupuesto_origen, id_tenant)` RESTRICT, **plus a partial unique index** so the 1:1 is a
  database guarantee rather than a service promise. The state-guarded `UPDATE` of decision 4 already
  serializes two concurrent conversions; the index is what makes the guarantee survive a repair
  script.
- `remitos.id_comprobante_venta integer NULL`, composite FK RESTRICT, **not unique** — N:1 is the
  point.
- **No bridge table** in either direction, and **no denormalized copy** of anything.

**Cost of reversing.** Dropping either column loses the link and nothing else; every document keeps
every effect it ever had. Introducing a bridge later is additive.

---

### 9 — **`convertido` is terminal; `facturado` is not.** The asymmetry is deliberate and each half has a reason.

**Context.** Both are "the document did its job" states, and both can have their consequence annulled.

**Decision.**

| Situation | Rule | Why |
|---|---|---|
| A sale born from a quote is **annulled** | The quote stays `convertido`; the comprobante keeps `id_presupuesto_origen` and the read model shows *"convertido → venta N (anulada)"* | Reopening it would resurrect a frozen price **after** its expiry could have passed — the exact leak decision 4's expiry rule exists to close. `AnularAsync` therefore needs **no** presupuesto coupling at all |
| A `TXR` consolidation is **annulled** | Its remitos return to `emitido` and their link is cleared, in the same transaction | The opposite choice strands delivered goods as permanently unbillable — a business dead end, not a tidy invariant. Annulling an invoice (wrong customer, wrong prices) is ordinary |

The annulment coupling is **one guarded call** in `AnularAsync`, reached only when the returned
`id_tipo_comprobante` is `TXR` (decision 7). For 100% of today's traffic the path is unchanged and
emits zero extra statements.

**Cost of reversing.** Making `convertido` reopenable later is a service rule plus an expiry re-check.
Making `facturado` terminal later is a service rule. Neither touches schema.

---

### 10 — **Authorization: no new policy.** Both documents sit under `Politicas.OperacionDePos` alone — reads **and** writes.

**Context.** The orchestrator asked this to be argued against the existing gates.

**Verified.** `/api/ventas` groups under `OperacionDePos` and stacks **nothing** on the checkout or on
`/{id}/anulacion` (`VentasEndpoints.cs:16-43`, with the reason written in the code: *"Un Vendedor
tiene que poder vender"*). `GestionDeCatalogo` is stacked only on catalog writes and on
`POST /api/stock/ajustes`.

**Options.**

| Option | Verdict |
|---|---|
| **Mirror the ventas gate exactly (`OperacionDePos`, nothing stacked)** | **Chosen.** Quoting and delivering are selling. A salesperson who may commit a sale — which moves stock, cash and debt — may certainly commit a quote, which moves nothing, and a delivery, which moves the same stock a sale would |
| Stack `GestionDeCatalogo` on the remito because it moves stock | Rejected as **backwards**. The manual `ajuste` is Admin-only because it is **discretionary stock writing with no document and no customer**; a remito has both. Gating a delivery harder than the sale of the same goods would be an accident dressed as caution |
| A new `GestionDeDocumentosDeVenta` policy | Rejected. `Politicas.cs` gains a name only when a **new kind of risk** appears (the stage-15 criterion for `SupervisionDeCuentaDeProveedor`); this is the same risk the POS already accepts |
| Put the consolidation under a supervision policy | Rejected. It creates a debt exactly as a cuenta-corriente sale does, and that sale is not supervised |

**Decision.** No change to `Politicas.cs`. `/api/presupuestos`, `/api/remitos` and
`/api/remitos/facturacion` group under `OperacionDePos`.

**Cost of reversing.** Tightening later is one policy registration plus its call sites. Loosening after
operators lost the ability to quote is a support ticket per day.

---

### 11 — **TWO migrations, not one**, with the `ALTER TYPE … ADD VALUE` isolated in the remito one.

**Context.** Stage 16 made *"exactly one migration"* its own invariant. This stage is bigger and
carries an irreversible type change.

**Decision.** `PresupuestosEtapa17` and `RemitosEtapa17`, in that order.

- The two documents are **independent**: nothing in `presupuestos` references `remitos`. Splitting the
  schema follows the slice boundary instead of fighting it, and each schema slice ships with its own
  RLS/SQLSTATE/CHECK tests.
- Postgres **forbids using an enum value in the transaction that adds it** (proven in stage 12), so
  `'remito'` must not be referenced by any `Sql()` of its own migration — isolating it removes the
  whole class of mistake and keeps the presupuesto migration fully reversible.
- The `PRE` data statement rides with `PresupuestosEtapa17` (it is net 1 of decision 2); the `TXR`
  insert rides with `RemitosEtapa17` (its consumer is the consolidation).

**Cost of reversing.** Merging two migrations before either has run is a text edit; after they run, it
is impossible — which is why the split is chosen now rather than discovered later.

---

### 12 — **Both state machines are native Postgres enums.** Ratified.

doc-10 principle 4 forbids enums for **user-editable padrones** and *prescribes* them for state
machines (*"enum nativo de Postgres solo para estados de máquina de estados"*, doc-10:19-21).
`estado_presupuesto` and `estado_remito` are closed sets with **one writer per transition**, matched
on by every listing filter — the same animal as `estado_compra`, `estado_orden_compra`, `estado_turno`
and `motivo_stock`. Registered **only** via `npgsql.MapEnum<T>()` in **both** `WaysDbContextFactory.cs`
and `DependencyInjection.cs`, never also with `HasPostgresEnum` (doc-10:451-454).

**No speculative value**: `vencido` is refuted in decision 3, and a `recibido`/`entregado` distinction
on the remito is deliberately absent — leaving the store **is** the delivery.

**Cost of reversing.** One `ALTER TYPE … ADD VALUE` per new value, irreversible but bounded.

---

### 13 — **A remito does NOT require an open turno; the consolidation DOES.**

**Context.** `ExigirTurnoAbiertoBajoLockAsync` is statement 0 of the sale transaction
(`ServicioDeVentas.cs:773`) because a sale is cash in a drawer.

**Decision.** The remito moves goods, not money: requiring an open turno would block a warehouse
dispatch because the cashier had not opened the till, and it would attach a delivery to an arqueo it
never touched. The **consolidation** takes payments, so it re-checks the turno under `FOR SHARE` as
its first statement, exactly like the checkout and like `ServicioDeCuentaCorriente` (`:285`). The
presupuesto, likewise, requires no turno.

**Cost of reversing.** Adding the requirement later is one call plus a 409 path; removing it after
operators worked around it by opening dummy turnos is a data-quality repair.

---

## Modelo de datos propuesto

> **DB CHANGE GATE — this section is the contract.** It states the complete model at table level.
> Anything `sdd-apply` writes that is not here is a **scope violation that reopens the gate**. On
> implementation, **doc 10 is updated** (a §4-adjacent subsection for both documents plus the
> "Estado (Etapa 17)" annotations), following the convention already used there.

**Gate verdict proposed: TWO migrations** (`PresupuestosEtapa17`, `RemitosEtapa17`). PostgreSQL 17.
**Two new enum types. One `ALTER TYPE … ADD VALUE` (IRREVERSIBLE, accepted). Four new tables. TWO
additive ALTERs over existing tables. THREE data statements + one seed change. 30 new indexes.**

### A. New enum types

```sql
CREATE TYPE estado_presupuesto AS ENUM ('borrador', 'enviado', 'convertido', 'anulado');
CREATE TYPE estado_remito      AS ENUM ('borrador', 'emitido', 'facturado', 'anulado');
```

Declaration order = lifecycle = C# member order (decision 12). Writers, one per value:
`borrador` ← `POST`; `enviado` ← `POST /{id}/enviar`; `convertido` ← the guarded `UPDATE` inside the
sale transaction (decision 4); `emitido` ← `POST /{id}/emitir`; `facturado` ← the consolidation
(decision 7); `anulado` ← `POST /{id}/anular`. **No speculative value.**

### B. `ALTER TYPE` on `motivo_stock` — **the only irreversible artifact of this stage**

```sql
ALTER TYPE motivo_stock ADD VALUE 'remito';   -- noveno valor
```

**IRREVERSIBILITY REGISTER.** Postgres cannot remove an enum value. Accepted because the value ships
**with its writer in the same slice**, the alternative (reusing `ajuste` or `venta`) would make the
ledger lie, and the precedent is stage 12's `decomiso`/`reclasificacion`. It rides in
`RemitosEtapa17` and **no `Sql()` of that migration may name it** (decision 11).

### C. New table — `presupuestos`

**Scoping category (doc 09): operativa** (`id_tenant` + `id_punto_venta` NOT NULL) — the same
category as `comprobantes_venta`, its sibling document. **`EntidadBase`: YES** — a quote is mutable
throughout `borrador` (full replace-set), edited again at `enviar`/`anular`, and an abandoned draft
needs the ordinary soft delete. Inherits `EntidadTenant` with the standard tenant filter and
`EstamparTenant()`.

```sql
presupuestos (                -- [operativa]
    id_presupuesto   integer     GENERATED BY DEFAULT AS IDENTITY,
    id_tenant        integer     NOT NULL,
    id_punto_venta   integer     NOT NULL,
    id_cliente       integer     NOT NULL,   -- Consumidor Final por defecto, como la venta
    id_empleado      integer     NOT NULL,   -- quién lo creó
    numero           bigint      NULL,       -- correlativo propio por PV; se asigna al ENVIAR
    fecha_emision    timestamptz NOT NULL,   -- IRelojDelSistema, sin DEFAULT now()
    fecha_envio      timestamptz NULL,       -- par de `numero`
    vencimiento      date        NULL,       -- NOT NULL desde 'enviado' (CHECK 1) — decisión 3
    observaciones    text        NULL,
    subtotal         numeric(14,2) NOT NULL,
    descuento_total  numeric(14,2) NOT NULL,
    total            numeric(14,2) NOT NULL,
    estado           estado_presupuesto NOT NULL,
    created_at, updated_at, deleted_at,
    CONSTRAINT pk_presupuestos PRIMARY KEY (id_presupuesto)
);
```

**17 columns** (14 + 3). `numero` is `bigint` because `numeraciones_comprobante.proximo_numero` is.
`fecha_emision` has **no `DEFAULT now()`**: `IRelojDelSistema` is the single time source and a DB
default would silently defeat `RelojFijo` in tests.

| Element | Name | Definition |
|---|---|---|
| PK | `pk_presupuestos` | `(id_presupuesto)`, identity by default |
| AK | `ak_presupuestos_id_presupuesto_id_tenant` | `UNIQUE (id_presupuesto, id_tenant)` — required by the composite FKs from `items_presupuesto` and `comprobantes_venta`. Structurally unviolable |
| FK 1 | `fk_presupuestos_tenant` | `(id_tenant) → tenants` RESTRICT |
| FK 2 | `fk_presupuestos_punto_venta` | `(id_punto_venta, id_tenant) → puntos_venta` RESTRICT |
| FK 3 | `fk_presupuestos_cliente` | `(id_cliente, id_tenant) → clientes` RESTRICT (AK exists, `ClienteConfiguration.cs:41`) |
| FK 4 | `fk_presupuestos_empleado` | `(id_empleado) → usuarios(id_usuario)` RESTRICT — **simple, not composite**, the documented deviation (a composite AK would force `id_tenant NOT NULL` on `usuarios` and break the platform sentinel), same criterion as `fk_comprobantes_venta_empleado` |
| CHECK 1 | `ck_presupuestos_envio_completo` | `((numero IS NULL) = (fecha_envio IS NULL)) AND ((numero IS NULL) = (vencimiento IS NULL)) AND (estado IN ('borrador','anulado') OR numero IS NOT NULL)` — number, send date and expiry arrive **together**, and every state past `enviado` has all three. `anulado` is admitted without them (a draft may be annulled before being sent) |
| RLS | `presupuestos_tenant` | `HabilitarRlsDeTenant("presupuestos")` → `ENABLE` + `FORCE` + `USING/WITH CHECK (app_es_plataforma() OR id_tenant = app_tenant_actual())`. **Standard, no deviation** |

**Indexes:**

| # | Index | Columns | Role |
|---|---|---|---|
| 1 | `ix_presupuestos_tenant` | `(id_tenant)` | RLS predicate + support for FK 1 |
| 2 | `ix_presupuestos_punto_venta_fecha` | `(id_punto_venta, id_tenant, fecha_emision)` | PV listing **and** support for FK 2 by leading-column prefix (the `ix_comprobantes_venta_punto_venta_fecha` shape) |
| 3 | `ix_presupuestos_cliente` | `(id_cliente, id_tenant)` | Per-customer listing + support for FK 3 |
| 4 | `ix_presupuestos_empleado` | `(id_empleado)` | Support for FK 4, **simple** (a composite index led by `id_tenant` would NOT cover a simple FK — stage 14's amendment trap) |
| 5 | `ux_presupuestos_numero` | `(id_tenant, id_punto_venta, numero)` **UNIQUE, PARTIAL** `WHERE numero IS NOT NULL` | Own series unique per PV; partial because a draft has no number (`ux_ordenes_compra_numero` shape) |
| 6 | *(implicit)* `ak_presupuestos_id_presupuesto_id_tenant` | `(id_presupuesto, id_tenant)` | The unique index Postgres creates for the AK |

**FK-coverage audit:** 4 FKs, 4 support indexes, **zero convention-added surprises**. No index is led
by `id_tenant` except 1 (whose FK *is* `id_tenant`) and 5 (unique, declared for its own reason) — the
exact pair stage 16 shipped and verified. **Total: 6 indexes + 1 PK.**

**No `estado` index**: a four-value discriminator that composes with indexes 2 and 3; adding it
speculatively is a migration for an unmeasured gain (stage-13 gate criterion). **No index on
`vencimiento`** for the same reason — the expiring-quotes listing is already bounded by index 2.

### D. New table — `items_presupuesto`

**Child scope: `id_tenant` only**, no own FK to `puntos_venta` (derived from the parent), the
`ItemComprobanteVentaConfiguration.cs:13-15` criterion. **`EntidadBase`: YES** (the replace-set
rewrites them).

```sql
items_presupuesto (
    id_item           integer     GENERATED BY DEFAULT AS IDENTITY,
    id_tenant         integer     NOT NULL,
    id_presupuesto    integer     NOT NULL,
    orden             integer     NOT NULL,
    id_articulo       integer     NOT NULL,      -- un presupuesto no tiene líneas de concepto libre
    descripcion       text        NOT NULL,      -- snapshot: el cliente lee un nombre
    cantidad          numeric(12,3) NOT NULL,
    precio_unitario   numeric(14,2) NOT NULL,
    descuento         numeric(14,2) NOT NULL DEFAULT 0,
    total             numeric(14,2) NOT NULL,
    id_lista_precio   integer     NOT NULL,      -- procedencia del precio ofrecido
    id_oferta         integer     NULL,
    id_alicuota_iva   integer     NOT NULL,
    porcentaje_iva    numeric(5,2) NOT NULL,
    created_at, updated_at, deleted_at,
    CONSTRAINT pk_items_presupuesto PRIMARY KEY (id_item)
);
```

**17 columns.** Deliberately **narrower than `items_comprobante_venta`**: no `id_area`, no
`codigo_barra` (both are artículo attributes the conversion re-reads from `articulos`, which it must
load anyway), no `costo_unitario`/`costo_es_estimado` (a quote never froze a cost — decision 4), no
`id_lote` (nothing is reserved — decision 5). `id_articulo` is **NOT NULL**: a free-concept line
cannot be converted into a stock-moving sale line.

| Element | Name | Definition |
|---|---|---|
| PK | `pk_items_presupuesto` | `(id_item)` |
| FK 5 | `fk_items_presupuesto_tenant` | `(id_tenant) → tenants` RESTRICT |
| FK 6 | `fk_items_presupuesto_presupuesto` | `(id_presupuesto, id_tenant) → presupuestos` RESTRICT, against §C's AK |
| FK 7 | `fk_items_presupuesto_articulo` | `(id_articulo, id_tenant) → articulos` RESTRICT |
| FK 8 | `fk_items_presupuesto_lista_precio` | `(id_lista_precio, id_tenant) → listas_precio` RESTRICT |
| FK 9 | `fk_items_presupuesto_oferta` | `(id_oferta, id_tenant) → ofertas` RESTRICT, nullable, MATCH SIMPLE |
| FK 10 | `fk_items_presupuesto_alicuota_iva` | `(id_alicuota_iva) → alicuotas_iva` RESTRICT — **simple**: `alicuotas_iva` is global (ADR-11), the `fk_items_comprobante_venta_alicuota_iva` precedent |
| CHECK 2 | `ck_items_presupuesto_cantidad_positiva` | `cantidad > 0` |
| RLS | `items_presupuesto_tenant` | `HabilitarRlsDeTenant("items_presupuesto")`. **Standard, no deviation** |

**Indexes:**

| # | Index | Columns | Role |
|---|---|---|---|
| 7 | `ix_items_presupuesto_tenant` | `(id_tenant)` | RLS + FK 5 |
| 8 | `ix_items_presupuesto_presupuesto` | `(id_presupuesto, id_tenant)` | FK 6 — **not** covered by index 12 (second column differs), the exact pair `items_comprobante_venta` already carries |
| 9 | `ix_items_presupuesto_articulo` | `(id_articulo, id_tenant)` | FK 7 |
| 10 | `ix_items_presupuesto_lista_precio` | `(id_lista_precio, id_tenant)` | FK 8 |
| 11 | `ix_items_presupuesto_oferta` | `(id_oferta, id_tenant)` | FK 9 |
| 12 | `ix_items_presupuesto_alicuota_iva` | `(id_alicuota_iva)` | FK 10, simple |
| 13 | `ux_items_presupuesto_orden` | `(id_presupuesto, orden)` **UNIQUE** | Mirrors `ux_items_comprobante_venta_orden` |

**FK-coverage audit:** 6 FKs, 6 support indexes, zero surprises. **Total: 7 indexes + 1 PK.**

### E. New table — `remitos`

**Scoping: operativa. `EntidadBase`: YES** (same reasoning as §C).

```sql
remitos (                     -- [operativa]
    id_remito           integer     GENERATED BY DEFAULT AS IDENTITY,
    id_tenant           integer     NOT NULL,
    id_punto_venta      integer     NOT NULL,
    id_cliente          integer     NOT NULL,
    id_empleado         integer     NOT NULL,
    numero              bigint      NULL,       -- serie 'REM'; se asigna al EMITIR
    fecha_emision       timestamptz NOT NULL,   -- creación del borrador (IRelojDelSistema)
    fecha_salida        timestamptz NULL,       -- par de `numero`: cuándo salió la mercadería
    direccion_entrega   text        NULL,
    observaciones       text        NULL,
    subtotal            numeric(14,2) NOT NULL,
    descuento_total     numeric(14,2) NOT NULL,
    total               numeric(14,2) NOT NULL,
    estado              estado_remito NOT NULL,
    id_comprobante_venta integer    NULL,       -- la factura consolidada (N remitos → 1)
    created_at, updated_at, deleted_at,
    CONSTRAINT pk_remitos PRIMARY KEY (id_remito)
);
```

**18 columns** (15 + 3).

| Element | Name | Definition |
|---|---|---|
| PK | `pk_remitos` | `(id_remito)` |
| AK | `ak_remitos_id_remito_id_tenant` | `UNIQUE (id_remito, id_tenant)` — required by `items_remito` and by `movimientos_stock.id_remito` (§H) |
| FK 11 | `fk_remitos_tenant` | `(id_tenant) → tenants` RESTRICT |
| FK 12 | `fk_remitos_punto_venta` | `(id_punto_venta, id_tenant) → puntos_venta` RESTRICT |
| FK 13 | `fk_remitos_cliente` | `(id_cliente, id_tenant) → clientes` RESTRICT |
| FK 14 | `fk_remitos_empleado` | `(id_empleado) → usuarios(id_usuario)` RESTRICT, simple (same deviation as FK 4) |
| FK 15 | `fk_remitos_comprobante_venta` | `(id_comprobante_venta, id_tenant) → comprobantes_venta` RESTRICT, nullable, MATCH SIMPLE — against the **already existing** `ak_comprobantes_venta_id_comprobante_venta_id_tenant` (decision 8, verified) |
| CHECK 3 | `ck_remitos_salida_completa` | `((numero IS NULL) = (fecha_salida IS NULL)) AND (estado IN ('borrador','anulado') OR numero IS NOT NULL)` — the number and the physical exit are the same fact |
| CHECK 4 | `ck_remitos_facturacion` | `((id_comprobante_venta IS NULL) = (estado <> 'facturado'))` — `facturado` and its link are the same fact, in both directions (decision 9's un-link clears both together) |
| RLS | `remitos_tenant` | `HabilitarRlsDeTenant("remitos")`. **Standard, no deviation** |

**Indexes:**

| # | Index | Columns | Role |
|---|---|---|---|
| 14 | `ix_remitos_tenant` | `(id_tenant)` | RLS + FK 11 |
| 15 | `ix_remitos_punto_venta_fecha` | `(id_punto_venta, id_tenant, fecha_emision)` | PV listing + FK 12 by prefix |
| 16 | `ix_remitos_cliente` | `(id_cliente, id_tenant)` | Per-customer listing (the consolidation's own query) + FK 13 |
| 17 | `ix_remitos_empleado` | `(id_empleado)` | FK 14, simple |
| 18 | `ix_remitos_comprobante_venta` | `(id_comprobante_venta, id_tenant)` | FK 15 + the *"which remitos does this invoice cover"* read |
| 19 | `ux_remitos_numero` | `(id_tenant, id_punto_venta, numero)` **UNIQUE, PARTIAL** `WHERE numero IS NOT NULL` | Own series per PV |
| 20 | *(implicit)* `ak_remitos_id_remito_id_tenant` | `(id_remito, id_tenant)` | The AK's unique index |

**FK-coverage audit:** 5 FKs, 5 support indexes, zero surprises. **Total: 7 indexes + 1 PK.**

### F. New table — `items_remito`

**Child scope: `id_tenant` only. `EntidadBase`: YES.**

```sql
items_remito (
    id_item           integer     GENERATED BY DEFAULT AS IDENTITY,
    id_tenant         integer     NOT NULL,
    id_remito         integer     NOT NULL,
    orden             integer     NOT NULL,
    id_articulo       integer     NOT NULL,      -- un remito entrega mercadería, nunca un servicio
    descripcion       text        NOT NULL,
    cantidad          numeric(12,3) NOT NULL,
    precio_unitario   numeric(14,2) NOT NULL,
    descuento         numeric(14,2) NOT NULL DEFAULT 0,
    total             numeric(14,2) NOT NULL,
    id_lista_precio   integer     NOT NULL,
    id_oferta         integer     NULL,
    id_alicuota_iva   integer     NOT NULL,
    porcentaje_iva    numeric(5,2) NOT NULL,
    costo_unitario    numeric(14,2) NULL,        -- congelado al SALIR la mercadería (etapa 9)
    costo_es_estimado boolean     NOT NULL DEFAULT false,
    id_lote           integer     NULL,          -- FEFO resuelto y congelado, como el ítem de venta
    created_at, updated_at, deleted_at,
    CONSTRAINT pk_items_remito PRIMARY KEY (id_item)
);
```

**20 columns.** The cost columns ship even though no report consumes them yet: **a cost is
unrecoverable once the goods have left**, while a report can be computed any day (the stage-9
argument, applied). `id_lote` is populated for lot-effective artículos and NULL otherwise — the
cross-table conditional the `stock` spec already states, asserted by an integration test.

| Element | Name | Definition |
|---|---|---|
| PK | `pk_items_remito` | `(id_item)` |
| FK 16 | `fk_items_remito_tenant` | `(id_tenant) → tenants` RESTRICT |
| FK 17 | `fk_items_remito_remito` | `(id_remito, id_tenant) → remitos` RESTRICT |
| FK 18 | `fk_items_remito_articulo` | `(id_articulo, id_tenant) → articulos` RESTRICT |
| FK 19 | `fk_items_remito_lista_precio` | `(id_lista_precio, id_tenant) → listas_precio` RESTRICT |
| FK 20 | `fk_items_remito_oferta` | `(id_oferta, id_tenant) → ofertas` RESTRICT, nullable |
| FK 21 | `fk_items_remito_alicuota_iva` | `(id_alicuota_iva) → alicuotas_iva` RESTRICT, simple (global) |
| FK 22 | `fk_items_remito_lote` | `(id_lote, id_articulo, id_tenant) → lotes` RESTRICT, MATCH SIMPLE — Postgres guarantees the line's lot belongs to the line's artículo, the `fk_items_comprobante_venta_lote` precedent |
| CHECK 5 | `ck_items_remito_cantidad_positiva` | `cantidad > 0` |
| CHECK 6 | `ck_items_remito_costo_no_negativo` | `costo_unitario IS NULL OR costo_unitario >= 0` |
| CHECK 7 | `ck_items_remito_estimado_con_costo` | `NOT costo_es_estimado OR costo_unitario IS NOT NULL` |
| RLS | `items_remito_tenant` | `HabilitarRlsDeTenant("items_remito")`. **Standard, no deviation** |

**Indexes:**

| # | Index | Columns | Role |
|---|---|---|---|
| 21 | `ix_items_remito_tenant` | `(id_tenant)` | RLS + FK 16 |
| 22 | `ix_items_remito_remito` | `(id_remito, id_tenant)` | FK 17 |
| 23 | `ix_items_remito_articulo` | `(id_articulo, id_tenant)` | FK 18 |
| 24 | `ix_items_remito_lista_precio` | `(id_lista_precio, id_tenant)` | FK 19 |
| 25 | `ix_items_remito_oferta` | `(id_oferta, id_tenant)` | FK 20 |
| 26 | `ix_items_remito_alicuota_iva` | `(id_alicuota_iva)` | FK 21, simple |
| 27 | `ix_items_remito_lote` | `(id_lote, id_articulo, id_tenant)` | FK 22 |
| 28 | `ux_items_remito_orden` | `(id_remito, orden)` **UNIQUE** | Mirrors `ux_items_comprobante_venta_orden` |

**FK-coverage audit:** 7 FKs, 7 support indexes, zero surprises. **Total: 8 indexes + 1 PK.**

### G. ALTER on `comprobantes_venta` — the conversion link

```sql
ALTER TABLE comprobantes_venta ADD COLUMN id_presupuesto_origen integer NULL;

ALTER TABLE comprobantes_venta
    ADD CONSTRAINT fk_comprobantes_venta_presupuesto_origen
    FOREIGN KEY (id_presupuesto_origen, id_tenant)
    REFERENCES presupuestos (id_presupuesto, id_tenant);      -- RESTRICT, MATCH SIMPLE

CREATE UNIQUE INDEX ux_comprobantes_venta_presupuesto_origen
    ON comprobantes_venta (id_presupuesto_origen, id_tenant)
    WHERE id_presupuesto_origen IS NOT NULL;
```

| Element | Name | Definition |
|---|---|---|
| FK 23 | `fk_comprobantes_venta_presupuesto_origen` | Composite, nullable, MATCH SIMPLE — with `id_presupuesto_origen` NULL the constraint is not checked (100% of today's rows, permanently legitimate) |
| Index 29 | `ux_comprobantes_venta_presupuesto_origen` | **UNIQUE, PARTIAL** — the 1:1 of decision 8 **and** the FK support index, declared explicitly with doc-10 naming instead of letting EF autogenerate a PascalCase one (the `NumeracionComprobanteConfiguration.cs:44-49` trap). Column order matches the FK exactly |

Adding a **nullable** column with no default is metadata-only in PG 11+: **no table rewrite**, no lock
beyond the brief `ACCESS EXCLUSIVE` every `ALTER` takes. **Binding verification** (success criteria):
`pg_indexes` shows this index and **no** EF-generated sibling — the one place where a filtered index
serving as FK support must be proven rather than assumed.

**No CHECK ties `id_presupuesto_origen` to anything.** The agreement between quote and sale (same
tenant, same customer, not expired, still `enviado`) is a **cross-table** rule the schema cannot
express; it is enforced in the service by the state-guarded `UPDATE` of decision 4.

### H. ALTER on `movimientos_stock` — the fourth writer's document link

```sql
ALTER TABLE movimientos_stock ADD COLUMN id_remito integer NULL;

ALTER TABLE movimientos_stock
    ADD CONSTRAINT fk_movimientos_stock_remito
    FOREIGN KEY (id_remito, id_tenant)
    REFERENCES remitos (id_remito, id_tenant);                -- RESTRICT, MATCH SIMPLE
```

| Element | Name | Definition |
|---|---|---|
| FK 24 | `fk_movimientos_stock_remito` | Composite, nullable — the exact shape of `fk_movimientos_stock_comprobante_venta` / `..._comprobante_compra` (`MovimientoStockConfiguration.cs:116-128`) |
| Index 30 | `ix_movimientos_stock_remito` | `(id_remito, id_tenant)` — FK support + *"the movements of this remito"* read, mirroring `ix_movimientos_stock_comprobante_venta` |

**Why the ledger must change.** Every other motivo carries its document (`id_comprobante_venta`,
`id_comprobante_compra`, `id_punto_venta_destino`, `id_lote`). A `remito` movement with no reference
would be the only unattributable row in an append-only ledger whose purpose is reconstruction.
Populated only for `motivo = remito` and for the `motivo = anulacion` rows that reverse them — the
`id_comprobante_compra` rule verbatim, and the `stock` spec delta states it.

**No CHECK ties `motivo` to the document columns** — none exists today for the other three either
(`MovimientoStockConfiguration.cs:22-25`, the only CHECK is `cantidad <> 0`), and inventing one now
would retro-constrain 100% of existing rows.

### I. Data statements — **THREE**, plus one seed change

```sql
-- 1. PresupuestosEtapa17 — net 1 de la decisión 2. Idempotente.
UPDATE tipos_comprobante SET activo = false WHERE codigo = 'PRE';

-- 2. RemitosEtapa17 — TXR para bases YA migradas (mismo guard que RC/C-*).
INSERT INTO tipos_comprobante (clase, codigo, nombre, letra, signo, discrimina_iva, es_fiscal, afecta_stock, activo, created_at, updated_at)
SELECT 'venta', 'TXR', 'Ticket X por remitos', 'X', 1, false, false, false, true, now(), now()
WHERE EXISTS (SELECT 1 FROM tipos_comprobante)
  AND NOT EXISTS (SELECT 1 FROM tipos_comprobante WHERE codigo = 'TXR');

-- 3. RemitosEtapa17 (Down) — desactiva en vez de borrar, para que un TXR ya emitido siga
--    siendo legible después de un rollback (criterio de CuentaCorrienteEtapa7/Etapa8).
UPDATE tipos_comprobante SET activo = false WHERE codigo = 'TXR';
```

**Seed change (mandatory, decision 2).** `InicializadorDeBaseDeDatos.TiposComprobanteBase` gains an
explicit `Activo` field: `false` for `PRE`, `true` for every other row, plus the new `TXR` tuple. The
proof that this is not optional is the execution order — migrations run first, the seeder runs only
against an **empty** database (`:432`), so on a fresh install statement 1 matches zero rows and the
seeder would then insert `PRE` active. `ux_tipos_comprobante_codigo` is UNIQUE over `codigo` alone,
so `'TXR'` is checked against **every** class; it collides with nothing.

### J. Error backstops (`db-error-backstops` APPLIES)

| New constraint | Client-input reachable? | Backstop |
|---|---|---|
| `ux_presupuestos_numero` (19… index 5) | **No** under normal operation — the only writer is `AsignadorDeNumeroComprobante` | **`23505` mapping REQUIRED anyway, and it carries the ordering trap for the FOURTH time**: the name contains `_numero`, so `ClasificarUnicidad`'s generic `_numero` branch (the `ux_clientes_numero` family) would classify it wrong. It MUST resolve by **exact name, above** that call — the identical treatment `ux_comprobantes_venta_numero` (`ManejadorDeErrores.cs:127-129`), `ux_comprobantes_compra_numero_externo` (`:136-138`) and `ux_ordenes_compra_numero` (`:159-161`) already document. Code: `numero_de_presupuesto_duplicado`, 409. Tests: a raw out-of-band insert asserting `23505` **and** the translated code, plus two concurrent `enviar` on one PV proving two distinct numbers and no 409 |
| `ux_remitos_numero` (index 19) | No — same writer | **FIFTH occurrence of the `_numero` ordering trap.** Exact-name branch, code `numero_de_remito_duplicado`, 409, same test pair |
| `ux_comprobantes_venta_presupuesto_origen` (index 29) | **Yes** — `idPresupuestoOrigen` comes from the request | Service refuses a non-`enviado` quote first (409), the state-guarded `UPDATE` serializes the race, and this index is the schema backstop. **Exact-name `23505` branch above `ClasificarUnicidad`** (its name must not be left to substring classification), code `presupuesto_ya_convertido`, 409. **Race test required**: two concurrent conversions of the same quote → exactly one 201 and one 409 |
| `ux_items_presupuesto_orden` (index 13) | No — `orden` is server-assigned inside the replace-set | Exact-name `23505` → `orden_de_item_duplicado`, 409, mirroring `ux_items_comprobante_venta_orden`. **Race-test exemption documented**, same family and reason |
| `ux_items_remito_orden` (index 28) | No — same | Same treatment, exemption documented |
| `ak_presupuestos_…`, `ak_remitos_…` | **No** — structurally unviolable (identity columns) | **No `23505` mapping. Exemption documented** per the skill's gate table and the `ak_*` precedent (no `ak_*` in this repo has one) |
| FK 3 / FK 13 `…_cliente` | **Yes** — `idCliente` from the body | Service pre-check 404 first (`ResolverClienteAsync`'s ADR-8 rule: an apocryphal id 404s, never 409) + generic `23503` → `400 referencia_invalida` (`:224`). One integration test per FK asserting the **translated** code |
| FK 7 / FK 18 `…_articulo`, FK 8/19 `…_lista_precio`, FK 9/20 `…_oferta`, FK 10/21 `…_alicuota_iva` | **Yes** — item lines | Same shape the venta draft validates today + generic mapping; one test per family |
| FK 23 `…_presupuesto_origen` | **Yes** — `idPresupuestoOrigen` | Service pre-check under the state-guarded `UPDATE` returning 404/409 before any write + generic mapping as backstop |
| FK 15 `…_comprobante_venta`, FK 24 `…_remito` | No — server-derived within the same transaction | Generic mapping. **Exemption documented** |
| FK 2/12 `…_punto_venta` | **Yes** | `ResolverPuntoVentaAsync` 404 first + generic mapping |
| FK 1/5/11/16 `…_tenant` | No — session-derived | Generic mapping. **Exemption documented**; SQLSTATE-asserting test required anyway |
| FK 4/14 `…_empleado` | No — always `contexto.UsuarioId`; `usuarios` is soft-deleted | Generic mapping. **Exemption documented** (the `fk_comprobantes_venta_empleado` precedent) |
| FK 6/17 `…_presupuesto`/`…_remito` (items) | No — the parent id of the same transaction | Generic mapping. **Exemption documented** |
| CHECK 1, 3, 4 | No — every column is server-derived and the service validates the transition first | Exact-name `23514` mappings following the `ck_comprobantes_compra_*` family, each proven by a raw-insert `23514` test so the constraint exists rather than being assumed |
| CHECK 2, 5 (cantidad), 6, 7 (costo) | **Yes** for quantities (request input), No for costs (server-derived) | Service validation first (400 with a domain code), exact-name `23514` mapping as the out-of-band backstop, one test each |

**`ManejadorDeErrores.cs` IS MODIFIED**: **3 new exact-name `23505` branches above
`ClasificarUnicidad`** (two of them the `_numero` ordering trap, occurrences 4 and 5), **2 more
exact-name `23505` branches** for the `orden` families, and **7 exact-name `23514` branches**.

### K. Deliberate non-decisions (gate-relevant)

- **No `vencido` enum value and no scheduler** (decision 3) — expiry is derived, so nothing can drift.
- **No reservation column, no `motivo_stock` value for a hold** (decision 5).
- **No `importe`-style CHECK** on `subtotal`/`descuento_total`/`total` on any of the four tables — the
  `importe` CHECK micro-gate is an **open carryover the owner reserved** (carried since stage 12,
  listed untouched by stages 14, 15 and 16). This stage does not pre-empt it.
- **No CHECK comparing `vencimiento` with `fecha_emision`** (decision 3 — it is only meaningful inside
  a timezone).
- **No CHECK tying `movimientos_stock.motivo` to its document columns** (§H).
- **No `estado`, `vencimiento` or `fecha_salida` index** — unmeasured gain (stage-13 criterion).
- **No `id_presupuesto_origen` on `remitos`** — quote → remito is out of scope.
- **No second `ALTER TYPE`**: `estado_comprobante`, `estado_compra`, `estado_orden_compra`,
  `tipo_movimiento_cc` and `categoria_gasto` are untouched. **`motivo_stock` is the only irreversible
  change of this stage.**
- **No change to `comprobantes_venta`'s existing columns, to `items_comprobante_venta`, to `stock`,
  `stock_lotes`, `lotes`, `numeraciones_comprobante`, `turnos_caja` or any proveedor-side table.**
- **No partitioning, retention or TTL.** One row per document.
- **No database-level immutability.** The same honest residue stages 14-16 recorded: theatre while
  `ways_owner` is a superuser.

**Ordering inside `PresupuestosEtapa17`**: `CREATE TYPE estado_presupuesto` → `CREATE TABLE
presupuestos` (+AK, FKs, CHECK, indexes) → `CREATE TABLE items_presupuesto` → `ALTER TABLE
comprobantes_venta` (column, FK, partial unique index) → data statement 1 → `HabilitarRlsDeTenant` on
both new tables, **last**.
**Ordering inside `RemitosEtapa17`**: `ALTER TYPE motivo_stock ADD VALUE 'remito'` (named by nothing
else in this migration) → `CREATE TYPE estado_remito` → `CREATE TABLE remitos` → `CREATE TABLE
items_remito` → `ALTER TABLE movimientos_stock` → data statement 2 → RLS on both new tables, last.

### Model summary for the gate

| Object | Change |
|---|---|
| `estado_presupuesto` | **NEW TYPE** — enum, 4 values, each with a writer |
| `estado_remito` | **NEW TYPE** — enum, 4 values, each with a writer |
| `motivo_stock` | **ALTER TYPE … ADD VALUE `'remito'`** — the ninth value. **IRREVERSIBLE, accepted, registered** |
| `presupuestos` | **NEW TABLE** — 17 columns, 1 PK, 1 AK, **4 FKs**, **1 CHECK**, **6 indexes** (incl. the AK's implicit one and 1 partial UNIQUE), RLS estándar, `EntidadBase` **YES** |
| `items_presupuesto` | **NEW TABLE** — 17 columns, 1 PK, **6 FKs**, **1 CHECK**, **7 indexes** (1 UNIQUE), RLS estándar, `EntidadBase` **YES** |
| `remitos` | **NEW TABLE** — 18 columns, 1 PK, 1 AK, **5 FKs**, **2 CHECKs**, **7 indexes** (incl. the AK's implicit one and 1 partial UNIQUE), RLS estándar, `EntidadBase` **YES** |
| `items_remito` | **NEW TABLE** — 20 columns, 1 PK, **7 FKs**, **3 CHECKs**, **8 indexes** (1 UNIQUE), RLS estándar, `EntidadBase` **YES** |
| `comprobantes_venta` | **ALTER** — `+ id_presupuesto_origen integer NULL` + composite FK + 1 partial UNIQUE index (metadata-only, no rewrite) |
| `movimientos_stock` | **ALTER** — `+ id_remito integer NULL` + composite FK + 1 support index (metadata-only, no rewrite) |
| Data statements | **THREE** (1 `UPDATE` PRE, 1 guarded `INSERT` TXR, 1 `UPDATE` TXR in `Down`) **+ the `TiposComprobanteBase` seed change** |
| `numeraciones_comprobante` | **UNCHANGED** — `'PRES'`, `'REM'` and `'TXR'` need no schema and no seed (verified) |
| `ManejadorDeErrores.cs` | **MODIFIED** — 5 exact-name `23505` branches (2 of them the `_numero` ordering trap, 4th and 5th occurrences) + 7 exact-name `23514` branches |
| `Politicas.cs` | **UNCHANGED** (decision 10) |
| Migrations | **TWO** (`PresupuestosEtapa17`, `RemitosEtapa17`) |
| **New indexes, total** | **30** — 6 + 7 on the presupuesto side, 7 + 8 on the remito side, 1 on `comprobantes_venta`, 1 on `movimientos_stock`; **excluding** the 4 new PK indexes |

## Affected Areas

| Area | Impact | Description |
|---|---|---|
| `src/Ways.Domain/Ventas/` | New | `EstadoPresupuesto`, `Presupuesto`, `ItemPresupuesto`, `EstadoRemito`, `Remito`, `ItemRemito`, and `ReglaDePresupuestos` — the expiry/convertibility predicate as a pure function, unit-testable without a DB (the `PoliticaDeRoles`/`ReglaDeLotes` pattern) |
| `src/Ways.Domain/Stock/MotivoStock.cs` | Modified | `Remito` — the ninth value, with its irreversibility comment |
| `src/Ways.Infrastructure/Migrations/` | New | `PresupuestosEtapa17` + `RemitosEtapa17` — the only DDL of this stage |
| `src/Ways.Infrastructure/Persistencia/Configuraciones/` | New + Modified | Four new configurations; `ComprobanteVentaConfiguration` (+`IdPresupuestoOrigen`, FK 23, index 29), `MovimientoStockConfiguration` (+`IdRemito`, FK 24, index 30), `DbSet`s, `MapEnum` in **both** option builders |
| `src/Ways.Infrastructure/Persistencia/InicializadorDeBaseDeDatos.cs` | Modified | `TiposComprobanteBase` gains `Activo` (PRE `false`) and the `TXR` row (§I) |
| `src/Ways.Application/Ventas/ServicioDePresupuestos.cs` | New | Draft CRUD (replace-set under `FOR UPDATE`), `enviar` with `'PRES'` numbering, `anular`, the read model with the derived `vencido`, and `/para-venta` |
| `src/Ways.Application/Ventas/EscriturasDePresupuesto.cs` | New | The single `UPDATE … WHERE estado = 'enviado' RETURNING` that marks `convertido` — the only transition authority, called from the sale transaction |
| `src/Ways.Application/Ventas/ServicioDeVentas.cs` | Modified | **One clause** in `ResolverTipoComprobanteAsync` (decision 2); the presupuesto branch of the **decide** phase (decision 4); **one guarded call** inside the transaction and **one guarded call** in `AnularAsync` gated by the `id_tipo_comprobante` added to `MarcarAnuladoAsync`'s existing `RETURNING`. **The pinned statement order and the stock/CC loops are byte-identical**, and an ordinary sale emits zero extra statements |
| `src/Ways.Application/Ventas/ServicioDeRemitos.cs` | New | Draft CRUD, `emitir` (numbering `'REM'`, FEFO, **the fourth stock write site** with its lock order), `anular` (inverse movements), and the read model |
| `src/Ways.Application/Ventas/ServicioDeFacturacionDeRemitos.cs` | New | The consolidation of decision 7: ascending remito locks, the itemless `TXR` comprobante, pagos + cuenta corriente through the existing writers, and the state-guarded N-row link |
| `src/Ways.Api/Endpoints/PresupuestosEndpoints.cs`, `RemitosEndpoints.cs` | New | Both groups under `OperacionDePos`, nothing stacked (decision 10) |
| `src/Ways.Api/Endpoints/VentasEndpoints.cs` | Modified | The checkout request gains an optional `idPresupuestoOrigen`; no route, no policy and no response shape changes (`dto-contract-honesty`) |
| `src/Ways.Api/Seguridad/ManejadorDeErrores.cs` | Modified | Gate §J |
| `src/Ways.Web/src/paginas/Presupuestos.tsx` + `Presupuesto.tsx` | New | List + detail/draft with the expiry state and the *"convertir"* action into the POS; `react-async-state` compliant, `web-descriptor-tests` covered |
| `src/Ways.Web/src/paginas/Remitos.tsx` + `Remito.tsx` + `FacturarRemitos.tsx` | New | List + detail/draft/emit/annul + the consolidation screen |
| `src/Ways.Web/src/paginas/Pos.tsx` | Modified | Pre-load from a quote (read-only display of frozen prices) and the *"esta venta viene del presupuesto N"* banner |
| `docs/10-modelo-de-datos.md` | Modified | The four tables, both new columns, the `PRE`/`TXR` notes in §1, and the "Estado (Etapa 17)" annotations |
| `docs/11-programa-post-paridad.md` | Modified | Etapa 17 status block with its four open decisions resolved (orchestrator, outside the phase) |

## Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| **The `PRE` hole is "closed" only on migrated databases** and reopens on every fresh install | **High if unmanaged** | Decision 2 makes the seed change mandatory and names the execution order that proves it; a test asserts a freshly seeded database has `PRE` inactive |
| **Silent repricing at conversion** — the checkout re-resolves prices and the stage's central promise dies quietly | **High if unmanaged** | Decision 4 makes the quote the price authority; the binding test converts a quote after the list price changed and asserts the **quoted** total |
| **Double decrement or phantom restock** on the consolidation path | **High if unmanaged** | Decision 7 makes the comprobante **itemless**, so both are unreachable rather than avoided; two tests assert zero `movimientos_stock` rows on emission **and** on annulment of a `TXR` |
| **A fourth stock write site introduces a deadlock** with the checkout | Med-High | The lock order is re-stated and implemented independently (decision 6), with a rendezvous test of a remito and a checkout on the same artículo and lot — the exact form `stock/spec.md:197-204` already demands of the other pairs |
| **Regressing the checkout** while adding three touch points | Med-High | Each is guarded and free for an unlinked sale; *"zero extra statements for an ordinary sale"* is asserted as a test, and the whole existing ventas suite must pass **unchanged** |
| **Expiry evaluated in the wrong timezone** — a quote dying at 21:00 for an Argentine customer | Med | Decision 3 pins `date` + `hoy(zona del PV)` with the `lotes-y-vencimientos:318-320` criterion cited; a test at a non-zero offset, since `RelojFijo` in `Z` cannot see this class of bug (stage-14 verify W2) |
| **The `_numero` ordering trap struck a fourth and fifth time** | Med | Gate §J requires exact-name branches above `ClasificarUnicidad` plus tests asserting the **translated** code, not the SQLSTATE alone |
| **A filtered index not recognised as FK support**, leaving an EF-generated PascalCase sibling on `comprobantes_venta` | Med | Made a **binding success criterion** verified against `pg_indexes`, not an assumption |
| **`ALTER TYPE … ADD VALUE` referenced in its own migration** | Low-Med | Decision 11 isolates it and the ordering in §K forbids naming it there; the stage-12 failure is the documented precedent |
| **Scope creep into reservations, repricing or fiscal consolidation** | Med | All three refused in writing with reopen conditions (decisions 4, 5, 7 / Out of Scope) |
| **Reviewer overload** (two schemas, a fourth write site, a touched checkout, three screens) | **High** | Eight stacked-to-main slices with four pre-authorized split points, `judgment-day` before every PR |
| **Raw-ADO `DateTimeOffset` written without UTC normalization** (a real 500 in PR #129) | Low-Med | The remito's ledger writer copies the existing `ParametrosDeComando`/`AgregarParametro` usage; a test at a non-zero offset |

## Rollback Plan

**Reversible except for one artifact, which is named.**

**Per slice.** Slices 2, 3, 5, 6, 7 and 8 are additive code over an unchanged schema: reverting one
removes a surface or a write path and leaves the tables intact and consistent.

**Slice 1 (`PresupuestosEtapa17`).** `DROP INDEX ux_comprobantes_venta_presupuesto_origen` → `ALTER
TABLE comprobantes_venta DROP CONSTRAINT fk_comprobantes_venta_presupuesto_origen` → `DROP COLUMN
id_presupuesto_origen` → `DROP TABLE items_presupuesto` → `DROP TABLE presupuestos` → `DROP TYPE
estado_presupuesto` → `UPDATE tipos_comprobante SET activo = true WHERE codigo = 'PRE'` **only if the
resolver guard is also reverted** — otherwise leaving `PRE` inactive is the safer residue and the
`Down` should say so.

**Slice 4 (`RemitosEtapa17`).** `ALTER TABLE movimientos_stock DROP CONSTRAINT
fk_movimientos_stock_remito` → `DROP COLUMN id_remito` → `DROP TABLE items_remito` → `DROP TABLE
remitos` → `DROP TYPE estado_remito` → `UPDATE tipos_comprobante SET activo = false WHERE codigo =
'TXR'` (deactivate, never delete — a `TXR` already emitted must stay readable).

**The one thing that does not roll back:** `motivo_stock` keeps the value `'remito'` forever. No row
will reference it after a rollback and nothing breaks; the value simply stays in the type. Accepted
and registered (§B).

**Why nothing else is destroyed.** No existing row is rewritten and no existing column changes
meaning: `id_presupuesto_origen` and `id_remito` start NULL everywhere and stay NULL for every
document that did not come from this stage. Stock, cuenta corriente and the ledger are byte-identical
whether this stage ships or not, because the engine that writes them is unchanged.

## Dependencies

- **Etapa 5** (archived) — `comprobantes_venta`, `items_comprobante_venta`, `ServicioDeVentas` with
  its decide-then-commit shape and pinned statement order, `AsignadorDeNumeroComprobante`,
  `numeraciones_comprobante`, `ServicioDeOfertas.ResolverAsync`. The engine this stage feeds and must
  not disturb.
- **Etapa 12** (archived) — `ReglaDeLotes`, `ServicioDeLotes.LeerSaldosAsync`, the FEFO decide-phase
  rule and the `stock_lotes` cache, consumed unchanged by the fourth write site.
- **Etapa 9** (archived) — the frozen-cost discipline (`costo_unitario` / `costo_es_estimado` and the
  honest NULL), replicated on `items_remito`.
- **Etapa 7** (archived) — `ServicioDeCuentaCorriente` and `EscriturasDeCuentaCorriente`, reused by
  the consolidation; and the itemless-comprobante precedent (`RC`) decision 7 leans on.
- **Etapa 16** (archived) — the document-with-lifecycle shape, the `UPDATE … RETURNING` transition
  authority, the guarded-call coupling pattern, the counted-index gate discipline and the `_numero`
  ordering trap.
- **Etapa 10/13** — `ParametroConocido.ZonaHoraria` and the `ResolverZonaAsync` pattern, consumed
  read-only for the expiry rule.
- `IRelojDelSistema`, `IContextoDeUsuario`, `EstrategiaSinReintento`, `ManejadorDeErrores`,
  `HabilitarRlsDeTenant`, `ValidadorDePagos` — all existing, **no new wiring**.
- Skills: `db-error-backstops` (per constraint), `react-async-state` + `web-descriptor-tests` (web
  slices), `dto-contract-honesty`, `mutation-proof-tests`, `work-unit-commits`, `judgment-day` before
  every PR.
- No new NuGet package, no new web dependency, **no scheduler, no queue**.

## Success Criteria

- [ ] Exactly **two** migrations ship (`PresupuestosEtapa17`, `RemitosEtapa17`); the only DDL is the
      gate section's and the only data statements are §I's;
      `dotnet ef migrations has-pending-model-changes` is clean afterwards.
- [ ] The migrations create **exactly 30 new indexes** (6+7+7+8+1+1) and **no** unnamed EF-generated
      FK support index — verified against `pg_indexes`, including that
      `ux_comprobantes_venta_presupuesto_origen` (partial) is the **only** index covering FK 23.
- [ ] RLS proven on all four new tables: a tenant reading with another tenant's GUC sees **zero**
      rows; an INSERT with a foreign `id_tenant` is refused (`42501`), asserted by SQLSTATE.
- [ ] **The `PRE` hole is closed twice, provably**: a freshly seeded database has `PRE` inactive; a
      `POST /api/ventas` with `"PRE"` returns 400; and an **out-of-band active** venta-class type with
      `afecta_stock = false` **also** returns 400 — the second test still failing if the resolver
      clause is removed.
- [ ] A draft presupuesto has `numero IS NULL` and `vencimiento IS NULL`; `enviar` assigns the next
      number **for that punto de venta** and requires an expiry; two concurrent `enviar` produce **two
      distinct numbers with no 409**.
- [ ] `ux_presupuestos_numero` and `ux_remitos_numero` resolve to their own domain codes (**not** the
      `ux_clientes_numero` family) — the ordering trap proven by assertion, not by reading.
- [ ] Converting a quote after the list price changed emits the sale at the **quoted** price, with the
      quoted discount, offer reference and IVA — and a `costo_unitario` frozen from **today's**
      `costo_nominal`.
- [ ] A conversion of an **expired** quote is refused (`409 presupuesto_vencido`), with expiry
      computed in the punto de venta's `zona_horaria` — proven by a test at a non-zero offset where
      UTC and local disagree on the day.
- [ ] Two concurrent conversions of the same quote yield exactly **one** 201 and **one** 409, and the
      partial unique index is proven by a raw out-of-band insert (`23505` + translated code).
- [ ] A sale **without** `idPresupuestoOrigen` emits **zero** extra statements, keeps the pinned
      statement order, and leaves the whole existing ventas suite green and unchanged.
- [ ] Emitting a remito inserts one `movimientos_stock` row per line with `motivo = remito` and
      `id_remito` set, updates `stock` and (for lot-effective lines) `stock_lotes` in the same
      transaction, and freezes `id_lote` on the item; a remito line for a non-product artículo is
      refused (400).
- [ ] A remito and a checkout competing for the same artículo and lot **both complete with no
      deadlock** — the fourth write site's own concurrency test.
- [ ] Annulling a remito inserts the exact inverse movements with `motivo = anulacion` and the same
      `id_remito`; a `facturado` remito cannot be annulled (409).
- [ ] `stock.cantidad` equals `SUM(movimientos_stock.cantidad)` across a sequence that includes
      `remito` and its `anulacion` — the nine-motivo restatement, asserted.
- [ ] The consolidation emits **one itemless** `TXR` comprobante whose total equals the sum of its
      remitos' frozen lines, writes **zero** `movimientos_stock` rows, links all requested remitos in
      one state-guarded statement, and refuses a mixed-customer or already-invoiced set (409).
- [ ] Annulling a `TXR` returns its remitos to `emitido` with `id_comprobante_venta` cleared, reverses
      cuenta corriente, and creates **zero** stock movements.
- [ ] Authorization matrix: a Vendedor can create, send, convert, deliver and invoice (200/201) — no
      403 anywhere on these routes; `Politicas.cs` is unchanged.
- [ ] doc 10 carries the four tables, both new columns, the `PRE`/`TXR` notes and the "Estado
      (Etapa 17)" annotations; the `stock` spec says **four** write sites and nine motivos; the
      `comprobantes-venta` RC sentence no longer claims RC flows through `ServicioDeVentas`.
- [ ] Domain / Application / Integration / vitest suites green; descriptor tests for every new screen.

## Plan de slices (tentative — `sdd-tasks` owns the final breakdown)

Stacked-to-main, one `judgment-day` round per slice (`protocolo-pr-solo-dev`).

| # | Branch | Content | ~lines |
|---|---|---|---|
| 1 | `feat/stage17-slice1-schema-presupuestos` | `PresupuestosEtapa17` (type, 2 tables, 10 FKs, 2 CHECKs, 13 indexes, the `comprobantes_venta` ALTER + FK 23 + index 29, data statement 1, RLS last) + entities + EF configs + `MapEnum` in both builders + the **seed change** + RLS/SQLSTATE/CHECK tests + doc 10 | ~500 |
| 2 | `feat/stage17-slice2-presupuestos-abm` | `ServicioDePresupuestos`: draft replace-set under `FOR UPDATE`, `POST/PUT/GET/list`, `enviar` with `'PRES'` numbering, `anular`, the derived `vencido` in the PV's zona horaria, the `ux_presupuestos_numero` backstop + concurrency test | ~480 |
| 3 | `feat/stage17-slice3-guard-y-conversion` | The resolver guard + its two binding mutation tests; `/para-venta`; `idPresupuestoOrigen` in the decide phase with the frozen-price authority; the guarded `MarcarConvertidoAsync`; the double-conversion race; the zero-extra-statements proof | ~500 |
| 4 | `feat/stage17-slice4-schema-remitos` | `RemitosEtapa17` (`ALTER TYPE`, type, 2 tables, 12 FKs, 5 CHECKs, 15 indexes, the `movimientos_stock` ALTER + FK 24 + index 30, data statement 2, RLS last) + entities + configs + `MotivoStock.Remito` + backstops + doc 10 | ~500 |
| 5 | `feat/stage17-slice5-remito-write-site` | `ServicioDeRemitos`: draft, `emitir` (numbering, FEFO, **the fourth write site** with its independent lock order), `anular`, the rendezvous test against a checkout, the nine-motivo consistency test | ~520 |
| 6 | `feat/stage17-slice6-consolidacion` | `POST /api/remitos/facturacion`: ascending remito locks, the itemless `TXR` comprobante, pagos + CC reuse, the state-guarded N-row link, the `RETURNING`-gated un-link in `AnularAsync`, the zero-stock assertions and the races | ~480 |
| 7 | `feat/stage17-slice7-web-presupuestos` | `Presupuestos.tsx` + `Presupuesto.tsx` (list, draft, send, annul, expiry state) + the POS conversion entry point + descriptor tests | ~450 |
| 8 | `feat/stage17-slice8-web-remitos` | `Remitos.tsx` + `Remito.tsx` + `FacturarRemitos.tsx` (list, draft, emit, annul, consolidate) + descriptor tests | ~450 |

Merge order `1 → 2 → 3 → 4 → 5 → 6 → 7 → 8`. Slice 1 blocks 2-3 and 7; slice 4 blocks 5-6 and 8;
3 depends on 2 for the entity surface; 6 depends on 5 for the remito lifecycle. **Slices 1-3 and 4-6
are independent tracks** and could interleave if the chain allows it.

**Pre-approved degradation** (the stage-12 decision-11 / stage-14 / stage-15 / stage-16 pattern), in
priority order:

1. **If slice 1 overflows** — split at the table boundary: `1a` (type + both presupuesto tables + the
   seed change + data statement 1) and `1b` (the `comprobantes_venta` ALTER + index 29 + doc 10). The
   split keeps **one migration per document**, which is the invariant that must not be degraded.
2. **If slice 4 overflows** — same split: `4a` (`ALTER TYPE` + type + both remito tables) and `4b`
   (the `movimientos_stock` ALTER + the `TXR` data statement + doc 10).
3. **If slice 6 overflows** — split at the write-path boundary: `6a` (the consolidation itself) and
   `6b` (the annulment un-link + its races).
4. **If slices 7/8 overflow** — ship list + detail + draft and drop the POS banner / the consolidation
   screen's bulk selection. A documented reduction, never silent.
5. **Never degraded**: the two `PRE` nets and their mutation tests, the frozen-price assertion, the
   fourth write site's lock order and concurrency test, and the zero-stock assertions of the
   consolidation. A phantom sale, a silent reprice or a double decrement is worse than no stage at
   all, so those are split, never trimmed.

**Review Workload Forecast (preliminary — `sdd-tasks` produces the binding one)**

- Estimated total: **~3 880 lines** across 8 slices. **Calibrated against the programme's own
  record**: stages 13-16 consistently came in **1.5-3x** their naive production-code estimate because
  test depth (races, SQLSTATE assertions, fault points, rendezvous tests, descriptor tests) is what
  inflates a slice. Every slice here carries at least one of those; slices 3, 5 and 6 carry three
  each, and slice 5 is the most test-heavy work unit of the whole programme so far (a new stock write
  site always is).
- `Decision needed before apply: No` — `auto-chain` + `stacked-to-main` already resolved in
  `state.yaml`.
- `Chained PRs recommended: Yes` — `chain_strategy: stacked-to-main`.
- `400-line budget risk: High` — **all eight** slices sit above the cap on the estimate alone, so the
  calibration above says they *will* exceed it. Four split points are pre-authorized and a **10-12 PR
  outturn is the expected case**, not the exception. This is the largest stage of the programme
  before Etapa 19.
- `size:exception` anticipated: **No** — the splits absorb it.

## Refutaciones y refinamientos a las Orchestrator Decisions

All seven are ratified in substance. Two carry a corrected mechanism, and three claims inherited from
the explore's tentative model **are refuted with evidence**.

| # | Orchestrator Decision | Verdict |
|---|---|---|
| 1 | Presupuesto = own table, structural mirror of the OC | **Ratified** (decision 1). Verified against the code: the stock and cuenta-corriente loops of `EjecutarTransaccionAsync` are unconditional over the plan, so option B's *"don't move anything"* has nowhere to live but inside them |
| 2 | The `PRE` finding closed with two nets: `activo = false` + a surgical `AfectaStock` guard **with product lines** in the resolver | **Ratified in substance, corrected in mechanism, and extended.** The guard is **unconditional on `afecta_stock`**, not conditional on lines — refutation 1 below. And the decision was **incomplete**: without the `TiposComprobanteBase` seed change, every fresh install reopens the hole (§I proves it from the execution order) |
| 3 | The presupuesto reserves no stock | **Ratified** (decision 5), with the honest residue stated: a converted quote may drive stock negative, which the counter already allows |
| 4 | Price frozen; expiry governs; no repricing | **Ratified, and made implementable.** The explore did not name the trap: the checkout re-resolves prices as its single authority, so pre-loading the cart would have repriced silently. Decision 4 makes the quote's snapshot the price authority and freezes the lines |
| 5 | Remito = own table with `ServicioDeRemitos` as the fourth formal write site; the stock guarantee amended; motivo `remito` via `ALTER TYPE` | **Ratified, with two additions the decision did not name**: the ledger needs `movimientos_stock.id_remito` (§H — otherwise the only unattributable rows in an append-only ledger), and a remito line MUST be a product, which is what makes *"every remito line moves stock"* total |
| 6 | Conversion by FK, no bridge tables; `id_presupuesto_origen` and `remitos.id_comprobante_venta` | **Ratified, refined and verified.** `comprobantes_venta` **already has** the alternate key the N:1 needs (`ComprobanteVentaConfiguration.cs:40-41`) — checked, not assumed. The 1:1 gains a **partial unique index** so it is a database guarantee, not a service promise |
| 7 | `para-venta` pre-loads the POS; the sale carries `id_presupuesto_origen` and marks `convertido` in the same transaction | **Ratified in shape, corrected in substance.** *"Cero cambio al checkout"* is not achievable **and not the right criterion**: the honest criterion, inherited from stage 16, is *"zero extra statements for an ordinary sale and an unchanged statement order"*, which this design meets. `/para-venta` becomes a **read for display**; the price authority moves server-side (decision 4) |

**Refuted (explore's tentative model / open questions — not Orchestrator Decisions):**

1. **The line-conditional guard (`!AfectaStock` + product lines) does not ship.** The resolver runs 40
   lines before `MaterializarItems` and before `articulos` is loaded, so the conditional form needs a
   signature change or a second query to buy a **weaker** rule that would still admit an itemless
   `PRE` sale. Verified that the unconditional form is safe: the only other `afecta_stock = false`
   type, `RC`, is resolved inside `ServicioDeCuentaCorriente.ResolverTipoRcAsync` (`:358-363`) and
   never reaches this resolver.
2. **`vencido` as a stored enum value does not ship** (`explore.md:51`). It has **no writer** — making
   a quote expire by itself needs a scheduler this repo does not have and stage 16 explicitly
   recorded as absent. It is a derived predicate, the `ReglaDeLotes.EstaVencido` precedent, evaluated
   in the punto de venta's zona horaria per a **binding** criterion already written into
   `lotes-y-vencimientos/spec.md:318-320`.
3. **The explore's integration sketch is incomplete in two load-bearing places.** It says the
   conversion costs *"cero cambio al checkout"* (refutation above), and it treats
   `remitos.id_comprobante_venta` as the whole of the consolidation. It is not: an ordinary sale over
   the remitos' lines would **decrement stock twice**, and a consolidation comprobante carrying items
   would be **reversed into phantom stock** by `AnularAsync` (`comprobantes-venta/spec.md:130-133`).
   Both are closed by making the comprobante **itemless**, which required a new `tipos_comprobante`
   row (`TXR`) the explore never contemplated (decision 7).

**New decisions the explore did not raise at all:** the ledger column for the fourth writer (§H), the
consolidation's shape and its two traps (decision 7), the terminal-vs-reversible asymmetry between
`convertido` and `facturado` (decision 9), the authorization gate (decision 10), the two-migration
split with the `ALTER TYPE` isolated (decision 11), and the turno asymmetry (decision 13).

## Proposal question round

Execution mode is `automatic-autonomous`, so these were resolved rather than asked. Each records the
assumption taken so a correction is cheap. **None blocks spec/design.**

1. **Does a quote expire because the price expires, or because the offer expires?** Assumed **the
   price** (decision 3/4): the vencimiento is what makes a frozen price safe to honour. If quotes are
   really open-ended commercial offers, the expiry becomes advisory and the conversion stops refusing
   expired ones — a service rule, not a migration.
2. **May a converted quote be edited on the way to the sale?** Assumed **no** (decision 4). If
   operators routinely adjust a line at the counter, the alternative is per-line provenance and a
   quoted-vs-sold reconciliation — the most expensive machinery of Etapa 7, rebuilt.
3. **Does a delivery leave without anyone opening a till?** Assumed **yes** (decision 13) — a remito
   is a warehouse act. If deliveries always happen at the counter, adding the turno requirement is one
   call plus a 409.
4. **Is an invoice over remitos ever partial** (invoice 3 of the 5 delivered lines)? Assumed **no**:
   consolidation is per remito, not per line. Partial invoicing would need a per-line invoiced
   quantity — the derived-quantity machinery of stage 16, which this stage deliberately does not build.
5. **Who may deliver goods without invoicing them?** Assumed **whoever may sell them** (decision 10).
   If a delivery should require a supervisor, that is one policy stacked on two routes.
6. **Should an annulled consolidation free its remitos to be invoiced again?** Assumed **yes**
   (decision 9) — the alternative strands delivered goods as permanently unbillable.
7. **Is `TXR` acceptable as the interim consolidation type, or should the circuit wait for Etapa 19?**
   Assumed **acceptable** (decision 7): the system emits no fiscal comprobante at all today, so a
   non-fiscal consolidation is exactly as fiscal as every other sale currently is. If the owner would
   rather not create the type, the alternative is shipping remitos that cannot be invoiced until
   Etapa 19 — and that is the single assumption that would most change this stage's scope.
