# Proposal: Stage 12 — Lotes y vencimientos (FEFO)

## Intent

The business sells food. Expired merchandise is a direct, silent loss, and today the system
has **no way to anticipate it**: `stock` is a single `numeric(12,3)` per
`(id_articulo, id_punto_venta)` with no notion of *which* units those are, and
`movimientos_stock` is a complete ledger with **zero lot dimension**
(`explore.md` §1, doc-10:505-559). Nothing anywhere in `src` stores an expiry date.

Stage 12 gives merchandise an **identity with a date**: a lot, a per-lot balance per punto
de venta, a FEFO default at the counter, a pull-model "próximos a vencer / vencidos"
surface, and a first-class `decomiso` circuit so that what is thrown away is *measured*
instead of disappearing into a generic `ajuste` (doc-11:142-163).

The stage's hardest requirement is not the lot table — it is the **off switch**. Doc-11:146-148
is literal: *"el POS no puede pagar el costo de una dimensión extra que no usa"*. So the
governing constraint of every decision below is: **with the module off, the checkout hot
path must not gain a single round-trip, a single mandatory column, or a single branch that
reads state it does not already hold.** This proposal meets that by branching the write
paths on `articulos.controla_lote` — a column that arrives inside a query the sale
*already* performs — and by **folding the empresa-level module flag into the existing
`parametros` read**, which takes the sale from two parametro queries to one. The hot path
gets *cheaper*, not more expensive, and it is verifiable by counting queries.

## Scope

### In Scope

- **Lot identity** (`lotes`): articulo + código + `fecha_vencimiento`, tenant-wide, created
  at reception or by an admin, immutable in its expiry once created.
- **Per-lot balance** (`stock_lotes`): the same `INSERT ... ON CONFLICT ... RETURNING`
  row-lock-as-serialization shape `stock` uses, keyed
  `(id_articulo, id_punto_venta, id_lote)`. `stock` itself is **unchanged**.
- **Ledger lot dimension**: `movimientos_stock.id_lote`, nullable, additive, append-only.
- **Effective lot control** = `articulos.controla_lote` (tenant-wide flag) **AND**
  `lotes_habilitado` (empresa parametro). Both must be true for a punto de venta to run the
  lot path.
- **Reception** (`ServicioDeCompras`): lot código + expiry per draft line, get-or-create at
  `Confirmar`, per-lot movement and balance, per-lot anulación reversal.
- **Sale** (`ServicioDeVentas`): server-side **FEFO default** computed in the
  decide-then-commit read phase; the lot snapshotted on `items_comprobante_venta`; per-lot
  movement inside the pinned lock order; exact per-lot anulación; NCX returns with an
  explicit lot.
- **Transfers** (`ServicioDeStock`): the lot travels with the merchandise; per-lot
  sufficiency refusal; the ascending lock order extended to the lot dimension.
- **Ajuste, conteo and the new `decomiso` circuit**: per-lot for lot-effective articulos,
  `motivo = decomiso` as a distinct, reportable reason.
- **Activation reconciliation**: the "sin identificar" lot and the net-zero
  `motivo = reclasificacion` movement pair that gives pre-existing stock a lot without
  moving a single unit of the aggregate.
- **Expiry surface (pull)**: `GET /api/reportes/stock/vencimientos` + its `/export` sibling
  (stage-11 pattern) + a Tablero tile.
- **Web**: lot picker at the POS (pre-selected FEFO, zero keystrokes on the happy path),
  lot input at reception, lot column in transfers/conteo, the `controla_lote` flag on the
  articulo editor, the `lotes_habilitado` toggle on parametros, the vencimientos screen.

### Out of Scope

- **Push alerts of any kind** — no email sender, no background scheduler, no notification
  table, no job runner. Decision 10; `explore.md` §8 confirms **no such infrastructure
  exists anywhere in `src`**.
- **Serial numbers / unit-level traceability** (a lot is a batch, not a unit).
- **Multi-lot lines**: one line, one lot (decision 4). Two lots = two lines.
- **Lot-aware pricing, costing or margin** — the lot does not change `costo_unitario`,
  `costo_nominal`, or any stage-9/10 figure. Cost stays per articulo.
- **Per-articulo alert horizons** — one empresa-level `dias_alerta_vencimiento`.
- **Lot on `gastos`, on tesorería, on cuenta corriente** — untouched.
- **Full-count freeze/variance workflow** — the `conteo-de-inventario` spec's standing
  exclusion is **not** reopened; only the count's unit becomes the lot.
- **Making `id_comprobante_asociado` mandatory on NCX** — decision 8 deliberately refuses
  to change an existing spec'd behaviour of `comprobantes-venta` for a lot-side reason.
- **Trazabilidad hacia el cliente** (which client got which lot, recall lists) — the data
  becomes derivable as a by-product of the item snapshot, but no recall surface is built.
- The owner's reserved items: comisiones formula, Supervisor margin, `OperacionDePos` read
  model, cierre de caja por rol, export branding. Untouched.

## Capabilities

### New Capabilities

- **`lotes-y-vencimientos`** — owns the lot end to end: lot identity and its immutability,
  the `stock_lotes` balance and its two invariants, effective lot control, the FEFO default
  contract, the sin-identificar lot and the `reclasificacion` reconciliation, the
  `decomiso` circuit, the vencimientos report and its export sibling, and the binding
  **"module off ⇒ zero added cost"** requirement with its query-count criterion.

Following the stage-11 precedent (listing exports owned by one capability rather than
smeared across four specs), the vencimientos report lives here end to end —
`reportes-de-gestion` is **not** modified.

### Modified Capabilities

- **`stock`** — the ledger gains `id_lote`; `motivo_stock` gains `decomiso` and
  `reclasificacion`; the sum-invariant requirement is restated over eight motivos; the
  ajuste path becomes lot-aware.
- **`transferencias-de-stock`** — lot travels; the `2N`-key ascending order becomes an
  order over `(id_articulo, id_punto_venta, id_lote)`; per-lot sufficiency refusal.
- **`comprobantes-venta`** — `items_comprobante_venta.id_lote` as a new **snapshot** field
  (explicitly legal under "Snapshot Immutability of Items"); NCX lot rules.
- **`comprobantes-compra`** — draft lot input, get-or-create at confirm, per-lot anulación
  refusal.
- **`conteo-de-inventario`** — for a lot-effective articulo the counted total is **per
  lot**; the "never a delta" rule is unchanged and extends per lot.
- **`parametros-operativos`** — two new `ParametroConocido` keys, no migration
  (stage-10 precedent).

## Approach

**Two tables, one nullable column, two enum values — and not one byte of change to the
shape of `stock`.**

The aggregate cache stays exactly what it is; the lot is a *second, parallel* cache over
the *same* ledger rows. `stock.cantidad = SUM(all movimientos)` keeps holding untouched,
and a second invariant appears next to it: `stock_lotes.cantidad = SUM(movimientos with
that id_lote)`. Because every lot-bearing movement is also an aggregate movement, the two
caches can never disagree by construction — the residue between them is exactly the
quantity that has no lot yet, which is precisely what the reconciliation converts.

Every decision below is written to be **reversible**: turning the module off is a parametro
flip, un-flagging an articulo is a boolean, and rolling the code back leaves nullable
columns and unused tables that harm nothing. The single genuinely irreversible artifact is
a Postgres enum value (which cannot be dropped) — a two-word residue in a type, and that is
stated as such rather than hidden.

## Autonomous decisions

The owner delegates technical decisions with recorded rationale. Each decision below states
its context, the options weighed, the call, and **what it costs to revert**.

---

### 1 — Lot control is a per-articulo boolean, mandatory where it is on. Not three-way, not optional.

**Context.** doc-11:159 asks: obligatorio, opcional, or configurable por artículo? A tenant
sells yogurt *and* light bulbs; a global "obligatorio" would put an expiry prompt on light
bulbs, and a global "opcional" would make every lot balance a half-truth.

**Options.**

| Option | Cost |
|---|---|
| Global obligatorio | Absurd on non-perishables; blocks the counter for no reason |
| Global opcional (lot as a free-text nicety) | Lot balances diverge from the aggregate silently; FEFO becomes a guess; the report lies |
| **Per-articulo boolean, mandatory where on** | One column, one branch, `EsProducto`'s exact precedent |
| Per-articulo three-way enum (no aplica / opcional / obligatorio) | New domain semantics with no precedent in the codebase; "opcional" is the option that has no honest invariant |

**Decision.** `articulos.controla_lote boolean NOT NULL DEFAULT false`. Where it is `true`
(and the empresa has the module on — decision 2), **every** stock movement of that articulo
carries a lot: sale, purchase, transfer, ajuste, conteo, decomiso, anulación. Where it is
`false`, the code path is byte-identical to today. There is **no "opcional"**: a lot
dimension that is sometimes filled is not a dimension, it is noise that makes the
`SUM(stock_lotes) = stock.cantidad` invariant unassertable — and an invariant that cannot
be asserted is exactly how a stock system rots.

This is `EsProducto`'s shape, deliberately: a boolean on `articulos` that decides whether an
entire code path applies, already loaded by every caller that needs it.

**Reversibility.** Flip the boolean to `false` and the articulo goes back to aggregate-only
behaviour immediately; the historical lot rows stay (append-only), and re-enabling re-runs
the reconciliation. **Cost of reverting: zero.** Widening to a three-way enum later is a
column type change with a mechanical backfill (`false → no_aplica`,
`true → obligatorio`) — additive, no data loss.

---

### 2 — The module switch is an empresa `parametro`, reconciled with the tenant-wide flag by ANDing them — and it rides free inside a query the sale already makes.

**Context.** `articulos` is **tenant-wide** (no `id_empresa`); `parametros` is
**empresa/PV-scoped**. Doc-11:146 demands activation *por empresa*. These are two different
axes and `explore.md` §7 flags the mismatch as needing an explicit resolution: what does a
lot-controlled articulo mean in an empresa where the module is off?

Worse, `ServicioDeVentas.EmitirAsync` is pinned to a documented **≤16 round-trip budget**
and already spends **two** of them on parametros (one query per key, lines 112-113). Naively
adding a third for `lotes_habilitado` would make the module cost the POS something *even
when off* — the one thing doc-11:148 forbids.

**Decision (three parts).**

1. **`lotes_habilitado`** (`bool`, default `false`) is a new `ParametroConocido`, resolved
   empresa-level exactly like `zona_horaria`/`comision_porcentaje` — **no migration, no data
   statement** (the stage-10 pattern, `parametros-operativos` spec).
2. **Effective lot control = `articulos.controla_lote` AND `lotes_habilitado`.** Both must
   be true. This closes the scoping mismatch *at the write path*, not just in the UI: an
   empresa with the module off runs today's code even for a flagged articulo, and turning
   the module on later is reconciled by decision 3's idempotent operation, which is the same
   operation that handles pre-existing stock. One mechanism, two problems.
3. **The parametro read is batched.** `ServicioDeVentas`'s private `ResolverParametroAsync`
   becomes a single `WHERE clave IN (...)` query resolving `tolerancia_pago`,
   `vuelto_maximo` **and** `lotes_habilitado` together. Net effect on the hot path:
   **2 queries → 1**. The module flag is not merely free, it **pays for itself**.

**Where the round-trips actually land** (the budget claim, verifiable by query count):

| Module state | Δ round-trips in `EmitirAsync` |
|---|---|
| Off (`lotes_habilitado = false`) | **−1** (parametros batched) |
| On, cart has no lot-controlled articulo | **−1** (the FEFO query is skipped: `controla_lote` is already in the loaded `articuloPorId` map) |
| On, cart has ≥1 lot-controlled articulo | **0** (−1 batched, +1 lot-balance read for the FEFO plan) |

Note the second row: the decision to sell lot-wise is taken from data **already in hand**.
The articulos are loaded before the transaction for pricing; `controla_lote` arrives with
them. No probing query exists.

**Honest residue.** A tenant with two empresas that shares its catalog (`DisponibleParaTodas
= true`) and enables the module in only one still flags the articulo *tenant-wide* — but
because effective control ANDs the empresa parametro, the second empresa's behaviour is
**unchanged**. The residue is narrower than the mismatch: the flag is *visible* everywhere,
*effective* only where enabled.

**Reversibility.** The flag is a parametro row (or its absence). Flipping it off returns the
empresa to aggregate behaviour with all lot history preserved. **Cost of reverting: one
`parametros` row.** If a real tenant ever needs per-empresa lot control of the *same*
articulo, the additive fix is a `controla_lote` override column on the existing
`articulos_empresas` junction — no new table, no migration of existing data.

---

### 3 — Pre-existing stock gets a "sin identificar" lot through a **net-zero `reclasificacion` movement pair**, not a migration backfill.

**Context.** doc-11:160 asks what happens to existing stock on activation. `explore.md` §9
offers `CostoCongeladoEnVentaEtapa9` as the backfill precedent, but flags the mismatch
correctly: that migration ran **once at deploy for all tenants**, while activation here is
**per-empresa, per-articulo, on demand, possibly years after deploy**. A migration cannot
express that.

There is also an arithmetic trap. The aggregate says 40 units. We need lot rows summing 40
**without changing `stock.cantidad`** — but `stock.cantidad = SUM(movimientos)`, so *any*
ledger row moves it.

**Decision.** A new motivo, **`reclasificacion`**, written **only in net-zero pairs** over
the same `(id_articulo, id_punto_venta)`: one row `id_lote = NULL, cantidad = −X` and one
row `id_lote = L, cantidad = +X`. The aggregate is provably untouched (the pair sums to
zero); the lot balance becomes exactly `X`; the ledger stays append-only; the whole thing is
auditable. **This is the mirrored-pair shape transfers already use**, applied across the lot
axis instead of the punto-de-venta axis.

The receiving lot is the **sin identificar** lot: one per articulo
(`es_sin_identificar = true`, `fecha_vencimiento NULL`, enforced by a partial unique index),
created lazily on first need.

**When it runs.** Synchronously and transactionally at the moment lot control becomes
effective — flipping `articulos.controla_lote` on reconciles that articulo across the
empresa's puntos de venta; flipping `lotes_habilitado` on reconciles the (initially empty)
set of already-flagged articulos. It is **idempotent** (a second run finds a zero residue
and writes nothing, exactly as `ContarAsync` writes nothing on a zero difference, dodging
`ck_movimientos_stock_cantidad_no_cero` instead of colliding with it) and re-runnable from
an explicit admin endpoint. Bounded by construction: the natural activation order (enable
module → flag articulos one at a time) touches a handful of rows per flip.

**Why `reclasificacion` and not `ajuste`.** The `conteo-de-inventario` spec already
establishes this codebase's principle: `inventario` is distinct from `ajuste` *for
traceability*, and the two write paths must never produce each other's motivo. An
`ajustes` report polluted with zero-net activation pairs would be a lie by inclusion. And
the name is chosen to be **general on purpose**: a net-zero lot-to-lot move of the same
articulo at the same PV is precisely what an operator needs when they mislabel a lot — one
primitive, two real uses, no second concept later.

**Reversibility.** The pairs are ordinary ledger rows; they can be neutralised by an
inverse pair without deleting anything. Un-flagging the articulo makes them inert history.
**Cost of reverting: nothing to unwind** — the aggregate was never touched. The one
irreversible residue is the enum value itself (see decision 9).

---

### 4 — FEFO is a **server-computed default**, never an imposition. One lot per line. The counter is never blocked.

**Context.** doc-11:161 asks: suggestion or imposition? `explore.md` §"FEFO" is blunt: the
current design has **zero enforcement mechanism** — `UpsertStockAsync` is a pure aggregate
delta with no unit identity. Imposition would need per-lot row locks selected in the
planning phase (which this proposal builds anyway), so the question is genuinely open on
the merits, not on feasibility.

**Decision.**

- The sale request **may** carry `idLote` per line. If it is **omitted**, the server picks
  the FEFO lot itself, in the **decide-then-commit read phase**, before the transaction
  opens (constraint 5). A client that knows nothing about lots therefore still transacts
  correctly — the lot dimension cannot break an old client or a fallback path.
- If it is **supplied**, the server validates it (exists, belongs to that articulo, not
  soft-deleted) and **honours it**. FEFO is not imposed, because the operator is holding a
  physical package: forcing them to record a different lot than the one in their hand would
  make the system's own data a lie, which is worse than an out-of-order sale.
- **FEFO ordering**: `ORDER BY es_sin_identificar DESC, fecha_vencimiento ASC, id_lote ASC`.
  The sin-identificar lot goes **first**, not last: at activation it *is* the oldest physical
  stock on the shelf, and parking it behind every dated lot would make it immortal — a
  permanent residue that never drains, never appears in the expiry report (it has no date),
  and quietly poisons every balance forever. `id_lote` is the deterministic tiebreak.
- **One lot per line.** If the FEFO lot does not cover the quantity, the sale still goes
  through against that lot (which may go negative, exactly as `stock.cantidad` may go
  negative today — legacy parity, `stock` spec). The POS *suggests splitting the line*; it
  never blocks. Two lots = two lines, which is honest: they are physically two different
  products with two different expiry dates, and the comprobante snapshot needs one lot per
  item row for anulación to be exact.
- **Selling an expired lot is allowed at the counter, with a warning** (decision 12).

**Reversibility.** Imposing FEFO later is a validation rule in the planning phase over data
this stage already produces — no schema change, no contract change (the field is already
there, only its acceptance narrows). Relaxing it back is the same edit. **Cost of reverting
in either direction: one rule in one file.** Multi-lot lines later are a child table of the
item, purely additive.

---

### 5 — The ledger gains a **nullable `id_lote`**; the lot balance lives in a **new** `stock_lotes` cache. `stock` does not change.

**Context.** doc-11:162 asks what relation the lot has with `movimientos_stock`, "hoy un
ledger completo sin dimensión de lote". Constraints 1, 2 and 4 of `explore.md` pin the
answer's boundaries: append-only, sum-invariant, and row-lock-as-serialization.

**Options.**

| Option | Verdict |
|---|---|
| Re-key `stock` to include `id_lote` | **Rejected.** Breaks the PK of the hottest table, breaks every existing query and the spec-asserted invariant, and forces a lot on non-lot articulos |
| Derive lot balances from the ledger on read (no cache) | **Rejected.** FEFO would scan history on every checkout; there is no row to lock, so the concurrency primitive disappears |
| **Nullable `id_lote` on the ledger + a parallel `stock_lotes` cache** | **Chosen** |

**Decision.** `movimientos_stock.id_lote integer NULL`, additive, with a composite FK that
enforces *at the database level* that the movement's lot belongs to the movement's articulo
(FK on `(id_lote, id_articulo, id_tenant)` against an alternate key on `lotes` — the same
composite-with-tenant convention used throughout). `stock` keeps its shape, its PK and its
invariant **verbatim**.

`stock_lotes (id_articulo, id_punto_venta, id_lote) → cantidad` is a PK-only cache with the
identical `INSERT ... ON CONFLICT DO UPDATE ... RETURNING` upsert, so it inherits the same
row-lock-as-serialization guarantee (constraint 4) with no new concurrency primitive.

**The two invariants, side by side:**

1. *(unchanged, restated over eight motivos)* `stock.cantidad` per
   `(id_articulo, id_punto_venta)` = `SUM(movimientos_stock.cantidad)` for that pair.
   `reclasificacion` pairs sum to zero and therefore cannot perturb it.
2. *(new)* `stock_lotes.cantidad` per `(id_articulo, id_punto_venta, id_lote)` =
   `SUM(movimientos_stock.cantidad)` for that triple. And for a **lot-effective**
   `(articulo, PV)` after reconciliation, `SUM over lots = stock.cantidad`, because the
   unlotted residue is zero by construction.

**Why "NOT NULL when the articulo is lot-controlled" is not a database CHECK.** It is a
cross-table conditional; Postgres cannot express it in a row CHECK. It is an **application
invariant with a dedicated integration test** (no movement of a lot-effective articulo
lacks a lot). The textbook alternative — a redundant `controla_lote` column on
`movimientos_stock` FK'd to a `(id_articulo, controla_lote)` unique key plus a CHECK — is
**deliberately rejected for v1**: it adds a column to the hottest table and forces all three
independent writers to carry it, for an invariant a test already proves. It is recorded here
as the escape hatch if that invariant is ever observed violated in production.

**Reversibility.** Every schema element is additive and nullable. Reverting the code leaves
a nullable column and two unused tables. **Cost of reverting: two orphan tables and one
orphan column, harming nothing.** No existing row is rewritten by this stage.

---

### 6 — The lock order extends to a single lexicographic order over `(id_articulo, id_punto_venta, id_lote)`, with the aggregate row **first**. Applied identically at all three write sites.

**Context.** Constraint 3 is the highest-risk mechanical item in the stage: ascending
`id_articulo` in checkout, ascending `id_articulo` in purchase confirm, and the ascending
`2N`-tuple order in transfers are **deadlock-prevention devices, not style**, and
`explore.md` §1 confirms the three writers hold **their own private copies** of
`InsertarMovimientoStockAsync`/`UpsertStockAsync` with no shared helper — by explicit,
documented design (`ServicioDeCompras`'s own doc-comment calls the duplication a "Slice 2
non-negotiable").

**Decision.** Every transaction that touches stock builds **one** total order over the keys
it will lock, in this exact form:

```
ORDER BY id_articulo, id_punto_venta, id_lote NULLS FIRST
```

where the aggregate `stock` row is the element with `id_lote = NULL`. Concretely: for each
`(articulo, PV)` in ascending order, upsert `stock` first, then upsert its `stock_lotes`
rows in ascending `id_lote`. Two tables, one order, no cycle — because the order is a
property of the *key tuple*, not of the table.

Transfers keep their existing rule (one order over all `2N` keys, never "all origin then
all destination"); the tuple simply gets a third component, making it a `≥2N`-key order.

**The duplication is not refactored away.** Three writers, three implementations of the same
rule, three independent test suites — the codebase's stated position, and this stage is
emphatically not the moment to unify the most concurrency-sensitive code in the system.
**Mitigation instead of unification**: the order is stated once in the spec as a single
requirement, and each write site gets its own concurrency test asserting it.

**Reversibility.** The order is a `ORDER BY` in three files. **Cost of reverting: three
edits** — but note this is the one decision whose *incorrect implementation* is expensive
(a production deadlock under concurrent checkout + transfer), which is why it gets its own
per-site test rather than a shared assertion.

---

### 7 — In a transfer, **the lot travels**. Sufficiency is refused per lot.

**Context.** doc-11:161 asks how the lot interacts with transfers. `explore.md` §4 pins the
shape: no header entity, no state machine, **no in-transit state** — "the transfer completes
or it does not happen at all".

**Options.** (a) The lot travels — the same `id_lote` is decremented at origin and
incremented at destination. (b) The transfer moves an aggregate quantity and the destination
re-buckets into its own sin-identificar lot.

**Decision.** **(a) The lot travels.** A lot is an identity of the *merchandise*, not of the
*location* — that is exactly why `lotes` carries no `id_punto_venta` and the balance lives
in `stock_lotes`. Option (b) would destroy expiry information at every internal move, which
is the one thing the stage exists to preserve: merchandise transferred from the depot would
arrive with an unknown expiry, and the receiving PV's expiry report would be blind on
exactly the stock it just received.

Per line: one lot, FEFO-defaulted server-side if omitted (decision 4's rule, same code).
The **back-office tightening extends to the lot**: a transfer that would leave the
*origin lot* negative is refused (`409 stock_insuficiente_para_transferencia`), even if the
aggregate is sufficient. This is stricter than today, deliberately and consistently with the
existing principle: a depot move that would invent units costs nothing to refuse; a cashier
mid-sale must never be stopped.

**Reversibility.** Option (b) remains implementable later as a per-transfer flag without
schema change. **Cost of reverting: a branch in one service.**

---

### 8 — Anulación is exact by snapshot. NCX carries an **explicit lot**, suggested from the association when there is one — and `id_comprobante_asociado` stays **optional**.

**Context.** This is `explore.md`'s sharpest gap. Anulación iterates the original
comprobante's own items, so a lot on the item makes reversal trivially exact. **NCX
devoluciones are a different animal**: they flow through the *same* `EmitirAsync` path as an
ordinary comprobante of `tipo NCX` with negative quantities, and
`id_comprobante_asociado` is **optional** — so today there is *no mechanism whatsoever* to
know which physical lot a returned unit came from.

**Options.**

| Option | Verdict |
|---|---|
| Make `id_comprobante_asociado` mandatory for lot-controlled lines | **Rejected.** It changes an existing, spec'd behaviour of `comprobantes-venta` for a reason tangential to it, and it would make goods sold *before* the module was on un-returnable |
| Client picks the lot on the NCX line | **Chosen** |
| Return into a dedicated "devoluciones" lot | **Rejected.** Invents a lot whose expiry is unknown, right after the customer handed us a package with the date printed on it |

**Decision.**

1. **`items_comprobante_venta.id_lote`** — a new **snapshot** field, frozen at emission,
   never re-derived. This is explicitly legal under the "Snapshot Immutability of Items"
   requirement (it is *added to* the snapshot, and no edit endpoint exists), and it is what
   makes anulación exact: the reversal reads the item's own lot, with no lookup and no
   ambiguity.
2. **NCX**: for a lot-effective articulo the line **must** carry `idLote`. The POS *suggests*
   it — from the associated comprobante's item snapshot when `id_comprobante_asociado` is
   present, otherwise from the articulo's existing lots (**the returned package has the date
   printed on it**; the operator reads it, which is the only source of truth that actually
   exists). If the operator genuinely cannot identify it, the **sin identificar lot is the
   escape hatch** — that is exactly the residue role it was created for, and it keeps the
   counter unblocked.
3. Returning into an expired lot is permitted. It is honest: those units *are* expired, they
   will appear in the expiry report, and the `decomiso` circuit is right there.

**Reversibility.** Making the association mandatory later is a validation rule (additive,
no schema change); deriving the lot automatically when the association exists is likewise a
rule. **Cost of reverting: one validation.** The snapshot column itself is nullable and
inert for non-lot articulos.

---

### 9 — `decomiso` is a **first-class motivo**, not a flavour of `ajuste`. It refuses to go negative.

**Context.** doc-11:145-146 asks for a "circuito de ajuste/decomiso por vencimiento con su
movimiento de stock correspondiente". `explore.md` §5 frames the fork: a new `MotivoStock`
value, or a sub-reason on top of `ajuste`.

**Decision.** A new enum value **`decomiso`**, plus a dedicated write path
`POST /api/stock/decomiso` (Admin-only, `GestionDeCatalogo` over `OperacionDePos`, like
ajuste/conteo/transferencia), requiring `idLote` for lot-effective articulos and a non-empty
`observaciones`. The client sends a **positive** quantity; the server negates it — the same
"no client-supplied signed delta" discipline `ContarAsync` enforces. **Back-office
tightening applies**: a decomiso that would leave the lot negative is refused
(`409 stock_insuficiente_para_decomiso`) — you cannot throw away units you do not have.

**Why a distinct motivo.** *Merma por vencimiento is a P&L number the owner wants to see.*
Buried inside `ajuste` it is unreportable without parsing free-text `observaciones`, which
is precisely the `tipo=95` disease doc-10 principle 4 exists to prevent. The
`conteo-de-inventario` spec already set the precedent: `inventario` was split from `ajuste`
"for traceability" on a weaker argument than this one.

`decomiso` is deliberately **not** restricted to expired lots — breakage and loss are real
and belong in the same bucket.

**Reversibility.** This is the stage's **one genuinely irreversible artifact**: Postgres
cannot `DROP` an enum value. Reverting leaves `decomiso` and `reclasificacion` as unused
members of `motivo_stock` — a residue in a type, no data harm, no behaviour. **Cost of
reverting: two dead enum values.** Recorded honestly rather than glossed.

**Migration note (PostgreSQL 17, binding on apply).** `ALTER TYPE ... ADD VALUE` is
permitted inside a transaction from PG12 on, but the new value **cannot be used in the same
transaction**. The migration that adds the two values must therefore **not** reference them
in any `Sql()`/seed statement of that same migration. Runtime use (a later request) is
unaffected.

---

### 10 — Alerts are **pull**: a report, an export sibling and a Tablero tile. No push infrastructure. **This is the alert channel stage 13 inherits.**

**Context.** doc-11:145 says "alertas de mercadería próxima a vencer", and doc-11:180-183
says stage 13 depends on stage 12 "para el canal de alertas compartido". But `explore.md`
§8 is categorical: grepping `notificacion|alerta|Notification|Alert|SendGrid|SmtpClient|
IEmailSender` across `src` returns **zero matches**. There is no email sender, no scheduler,
no background service, no notification table.

**Decision.** The alert channel for this program is **pull**, and stage 12 establishes it:

- `GET /api/reportes/stock/vencimientos?idPuntoVenta&dias=` — lot rows at a punto de venta
  with a positive balance, classified `vencido` / `por_vencer` / `vigente`, ordered by
  expiry. `dias` defaults to the new `dias_alerta_vencimiento` parametro (30).
- Its **`/export?formato=xlsx` sibling**, by co-location, per the stage-11 house standard —
  this *is* the "planilla de vencimientos / control de góndola" of doc-11:155, at the cost
  of one mapping and one route line.
- A **Tablero tile**: counts of vencidos and por vencer for the punto de venta, linking to
  the report.

**"Hoy" is resolved in the punto de venta's own `zona_horaria`**, never in server/UTC time —
this is the exact production bug class stage 11's slice-9 judgment-day round caught and
which needed a second post-archive hardening commit (`08e7707`). A store west of UTC must
not see tomorrow's expiries today. **Binding**: this is a verify criterion, not a nicety.

Building push here would mean an email/SMS dependency, a scheduler, delivery state,
retry/bounce handling, per-user preferences and an unsubscribe path — an entire stage of
infrastructure, decided blind, for zero current users, in a stage already sized "grande".

**Reversibility.** Adding push later consumes this same query as its data source and changes
nothing that ships here. **Cost of reverting: zero** (nothing to undo). **Explicit handoff
to stage 13**: the shared channel it inherits is *pull*; if push is wanted, stage 13 owns
that decision with a second real use case in hand, which is the right moment to decide it.

---

### 11 — Conteo of a lot-effective articulo counts **per lot**. The "never a delta" rule is unchanged.

**Context.** `conteo-de-inventario` is strictly per-articulo today and its spec explicitly
scopes out any full-count workflow. But leaving it aggregate-only is **not neutral**: an
aggregate conteo on a lot-controlled articulo would move `stock.cantidad` without touching
any lot balance, breaking invariant 2 silently. Doing nothing is the one option that is
actually unsafe.

**Options.** (a) Refuse conteo for lot-effective articulos. (b) Count per lot. (c) Dump the
difference into the sin-identificar lot.

**Decision.** **(b)**, minimally: for a lot-effective articulo the request carries a counted
total **per lot**, the server derives each lot's delta under that lot's row lock (the exact
`ContarAsync` shape, one level down), and the aggregate delta is the sum of the per-lot
deltas — one movement per lot with `motivo = inventario`, zero-difference lots writing
nothing, as today. This is also how the physical count actually happens: you count
perishables by the date on the shelf.

**(c) is rejected**: it would fabricate an unidentified-lot balance out of a counting error,
turning a measurement into contamination. **(a) is recorded as the pre-approved
degradation** if the slice overflows its budget — a clean `409` is honest, and a stage that
ships a refusal is strictly better than one that ships a silent divergence.

**Reversibility.** Both directions are one request-shape change over the same movements.
**Cost of reverting: one contract and one service method.**

---

### 12 — An expired lot **blocks the back office and warns the counter**.

**Context.** May you sell from a lot that expired yesterday? The codebase already has a
governing principle for exactly this asymmetry, stated in the `transferencias-de-stock`
spec: *counter operations never block on stock (a cashier must never be stopped mid-sale);
back-office stock-reducing operations do.*

**Decision.** Apply it verbatim to expiry:

- **Sale / NCX**: allowed. The response carries a warning flag and the POS shows it
  prominently, pre-selecting a non-expired lot whenever one exists. A hard block at the
  counter would mean a queue, a manager call, and — realistically — the sale happening
  anyway with the wrong lot recorded, which is worse than a recorded truth.
- **Transfer**: refused (`409 transferencia_lote_vencido`). Moving expired goods between
  stores is never the right operation; `decomiso` is.
- **Reception**: a purchase line with `fecha_vencimiento` in the past is refused
  (`409 lote_vencido_en_recepcion`) — receiving already-expired merchandise is a supplier
  problem to solve at the door, not a data entry to accept.

**Reversibility.** Each is one validation in one place. **Cost of reverting: three edits**,
and each can be flipped independently.

---

## Modelo de datos propuesto

> **DB CHANGE GATE — this section is the contract.** It states the complete model at table
> level. Anything `sdd-apply` writes that is not here is a scope violation that reopens the
> gate. On implementation, **doc 10 §6 is updated** with the new tables and the two new
> `motivo_stock` values, following the "Estado (Etapa N)" annotation convention already used
> there.

**Migration**: one, named `LotesYVencimientosEtapa12`. PostgreSQL 17.

### A. New table — `lotes`

**Scoping category (doc 09): Tenant-wide** (`id_tenant`, **sin** `id_empresa`) — doc 09's
exact category for `articulos`/`codigos_barra`/`precios`. `lotes` follows the articulo the
same way `precios` does ("sigue al artículo"), and is NOT in the "Catálogo" category (which
carries an `id_empresa NULL` column — `lotes` has none). It has identity and a lifecycle, so
it inherits `EntidadTenant` (full `created_at`/`updated_at`/`deleted_at` audit), unlike the
PK-only caches. *(Gate amendment 1: category name corrected to doc-09 terms; model unchanged.)*

```sql
lotes (                              -- [catálogo — tenant-wide, sin id_empresa, como articulos]
    id_lote            integer  GENERATED BY DEFAULT AS IDENTITY,
    id_tenant          integer  NOT NULL,
    id_articulo        integer  NOT NULL,
    codigo             text     NOT NULL,   -- server-derived from the ISO expiry when omitted
    fecha_vencimiento  date     NULL,       -- NULL if and only if es_sin_identificar
    es_sin_identificar boolean  NOT NULL DEFAULT false,
    created_at         timestamptz NOT NULL,
    updated_at         timestamptz NOT NULL,
    deleted_at         timestamptz NULL,
    CONSTRAINT pk_lotes PRIMARY KEY (id_lote)
);
```

| Element | Name | Definition |
|---|---|---|
| PK | `pk_lotes` | `(id_lote)` |
| Alternate key | `ux_lotes_id_articulo_tenant` | UNIQUE `(id_lote, id_articulo, id_tenant)` — **principal key for the composite FKs of `stock_lotes` and `movimientos_stock`**, so the DB itself enforces "the lot belongs to that articulo" |
| FK | `fk_lotes_tenant` | `(id_tenant) → tenants(id_tenant)` `ON DELETE RESTRICT` |
| FK | `fk_lotes_articulo` | `(id_articulo, id_tenant) → articulos(id_articulo, id_tenant)` `ON DELETE RESTRICT` |
| CHECK | `ck_lotes_vencimiento_segun_tipo` | `(es_sin_identificar AND fecha_vencimiento IS NULL) OR (NOT es_sin_identificar AND fecha_vencimiento IS NOT NULL)` |
| CHECK | `ck_lotes_codigo_no_vacio` | `length(btrim(codigo)) > 0` |
| Unique idx | `ux_lotes_articulo_codigo` | UNIQUE `(id_tenant, id_articulo, codigo)` `WHERE deleted_at IS NULL` — the natural key; get-or-create resolves against it |
| Unique idx | `ux_lotes_sin_identificar` | UNIQUE `(id_tenant, id_articulo)` `WHERE es_sin_identificar AND deleted_at IS NULL` — at most one sin-identificar lot per articulo |
| Index | `ix_lotes_tenant` | `(id_tenant)` |
| Index | `ix_lotes_articulo` | `(id_articulo, id_tenant)` — FK support (explicit name; EF's PascalCase default is always overridden) |
| Index | `ix_lotes_vencimiento` | `(id_tenant, fecha_vencimiento)` `WHERE deleted_at IS NULL` — the expiry report's filter |
| RLS | `lotes_tenant` | `migrationBuilder.HabilitarRlsDeTenant("lotes")` → `ENABLE` + `FORCE ROW LEVEL SECURITY` + `USING/WITH CHECK (app_es_plataforma() OR id_tenant = app_tenant_actual())` |

**Immutability rule (application-level):** a lot's `fecha_vencimiento` is frozen at
creation. A second reception with the same `(articulo, codigo)` and a different expiry is
refused `409 lote_vencimiento_incompatible` rather than silently overwriting — an expiry
that changes retroactively would rewrite the meaning of every movement already posted
against it.

### B. New table — `stock_lotes`

**Scoping category (doc 09): operativa** (`id_tenant` + `id_punto_venta`) — identical to
`stock`. PK-only cache, **no audit columns**, following `Stock`'s documented precedent, which
also means it needs the same hand-rolled tenant filter treatment as
`WaysDbContext.AplicarFiltroDeTenantEnStock`.

```sql
stock_lotes (                        -- [operativa]
    id_articulo     integer NOT NULL,
    id_punto_venta  integer NOT NULL,
    id_lote         integer NOT NULL,
    id_tenant       integer NOT NULL,
    cantidad        numeric(12,3) NOT NULL DEFAULT 0,
    CONSTRAINT pk_stock_lotes PRIMARY KEY (id_articulo, id_punto_venta, id_lote)
);
```

| Element | Name | Definition |
|---|---|---|
| PK | `pk_stock_lotes` | `(id_articulo, id_punto_venta, id_lote)` — **also the `ON CONFLICT` target** of the upsert whose row lock is the concurrency primitive (constraint 4) |
| FK | `fk_stock_lotes_tenant` | `(id_tenant) → tenants(id_tenant)` RESTRICT |
| FK | `fk_stock_lotes_lote` | `(id_lote, id_articulo, id_tenant) → lotes(id_lote, id_articulo, id_tenant)` RESTRICT — enforces lot/articulo coherence at the DB |
| FK | `fk_stock_lotes_punto_venta` | `(id_punto_venta, id_tenant) → puntos_venta(id_punto_venta, id_tenant)` RESTRICT |
| Index | `ix_stock_lotes_tenant` | `(id_tenant)` |
| Index | `ix_stock_lotes_punto_venta` | `(id_punto_venta, id_tenant)` — the vencimientos report's driving access path (same shape as `ix_stock_punto_venta`) |
| Index | `ix_stock_lotes_lote` | `(id_lote, id_articulo, id_tenant)` — FK support |
| CHECK | **none on `cantidad`** | **Deliberate**, matching `stock`: a lot balance may go negative at the counter (legacy parity). Negativity is refused only on back-office paths (transfer, decomiso), in the application |
| RLS | `stock_lotes_tenant` | `HabilitarRlsDeTenant("stock_lotes")` — same policy as above |

**No `minimo`/`reposicion` per lot** — reorder points are per articulo and belong to stage 13.

### C. Modified table — `movimientos_stock`

```sql
ALTER TABLE movimientos_stock ADD COLUMN id_lote integer NULL;
```

| Element | Name | Definition |
|---|---|---|
| Column | `id_lote` | `integer NULL`. Populated for movements of lot-effective articulos and for the `id_lote IS NULL` half of a `reclasificacion` pair (which stays NULL by design) |
| FK | `fk_movimientos_stock_lote` | `(id_lote, id_articulo, id_tenant) → lotes(id_lote, id_articulo, id_tenant)` `ON DELETE RESTRICT` |
| Index | `ix_movimientos_stock_lote` | `(id_lote, id_articulo, id_tenant)` — FK support and per-lot ledger reconstruction |
| Unchanged | `ck_movimientos_stock_cantidad_no_cero`, every existing column, index and FK, and the append-only rule | No existing row is rewritten by this stage |

**Not enforced in the DB:** "`id_lote` NOT NULL when the articulo is lot-effective" is a
cross-table conditional (decision 5) — an application invariant with a dedicated integration
test.

### D. Modified enum — `motivo_stock`

```sql
ALTER TYPE motivo_stock ADD VALUE 'decomiso';
ALTER TYPE motivo_stock ADD VALUE 'reclasificacion';
```

Emitted by EF/Npgsql as an `AlterDatabase` annotation change
(`ajuste,anulacion,compra,decomiso,inventario,reclasificacion,transferencia,venta`).
**Binding**: the new values MUST NOT be referenced by any statement in the same migration
(PG allows `ADD VALUE` in a transaction from v12, but forbids *using* the value in that
same transaction). Six motivos become eight; the sum-invariant requirement is restated over
all eight.

### E. Modified table — `articulos`

```sql
ALTER TABLE articulos ADD COLUMN controla_lote boolean NOT NULL DEFAULT false;
```

| Element | Name | Definition |
|---|---|---|
| Column | `controla_lote` | `boolean NOT NULL DEFAULT false` — same shape and role as `es_producto` |
| Index | `ix_articulos_controla_lote` | `(id_tenant)` `WHERE controla_lote AND deleted_at IS NULL` — a small partial index serving the reconciliation set and the lot-articulo listing |

**No backfill**: the default is `false`, which is byte-identical to today's behaviour for
every existing row. **This stage writes no data statement over existing rows** — the only
data movement is the runtime, per-tenant reconciliation of decision 3, which is application
code, not a migration.

### F. Modified table — `items_comprobante_venta`

```sql
ALTER TABLE items_comprobante_venta ADD COLUMN id_lote integer NULL;
```

| Element | Name | Definition |
|---|---|---|
| Column | `id_lote` | `integer NULL` — a **snapshot** field, frozen at emission, never re-derived (doc-10 principle 6) |
| FK | `fk_items_comprobante_venta_lote` | `(id_lote, id_articulo, id_tenant) → lotes(id_lote, id_articulo, id_tenant)` RESTRICT — targets the existing alternate key `ux_lotes_id_articulo_tenant`, enforcing lot/articulo coherence at the DB and avoiding a second alternate key on `lotes`. `MATCH SIMPLE` semantics: `id_articulo` is nullable here (free-concept lines, doc 10 §4), but a lot-bearing line necessarily references an articulo — the application validates that pairing; the FK enforces it whenever all three columns are non-null. *(Gate amendment 2.)* |
| Index | `ix_items_comprobante_venta_lote` | `(id_lote, id_articulo, id_tenant)` — FK support |

### G. Modified table — `items_comprobante_compra`

```sql
ALTER TABLE items_comprobante_compra
    ADD COLUMN codigo_lote       text    NULL,
    ADD COLUMN fecha_vencimiento date    NULL,
    ADD COLUMN id_lote           integer NULL;
```

| Element | Name | Definition |
|---|---|---|
| Columns | `codigo_lote`, `fecha_vencimiento` | Draft-time **input**, captured while the compra is `Borrador`. Kept as inputs (not resolved to a lot) because draft lines are physically replaced on every edit (`DELETE`+`INSERT`) — resolving early would litter `lotes` with rows for drafts that never confirm |
| Column | `id_lote` | Resolved at `Confirmar` by get-or-create against `ux_lotes_articulo_codigo`; NULL while the compra is a draft and for non-lot articulos. Snapshot thereafter — this is what makes anulación exact |
| FK | `fk_items_comprobante_compra_lote` | `(id_lote, id_articulo, id_tenant) → lotes(id_lote, id_articulo, id_tenant)` RESTRICT — same alternate-key target as venta's; here `id_articulo` is `NOT NULL`, so the coherence FK is fully enforced whenever `id_lote` is set. *(Gate amendment 2.)* |
| Index | `ix_items_comprobante_compra_lote` | `(id_lote, id_articulo, id_tenant)` — FK support |
| CHECK | `ck_items_comprobante_compra_lote_input` | `(codigo_lote IS NULL AND fecha_vencimiento IS NULL) OR fecha_vencimiento IS NOT NULL` — a lot code without an expiry can never resolve to a valid `lotes` row, so it is refused at the door |

### H. No schema change — `parametros`

Two new `ParametroConocido` entries only. **No migration, no data statement**, exactly as
stage 10 did for `zona_horaria`/`comision_porcentaje`:

| Key | CLR type | Default | Role |
|---|---|---|---|
| `lotes_habilitado` | `bool` | `false` | Empresa-level module switch (decision 2) |
| `dias_alerta_vencimiento` | `int` | `30` | "Próximo a vencer" horizon (decision 10) |

### Model summary for the gate

| Object | Change | Scoping | RLS |
|---|---|---|---|
| `lotes` | **NEW table** | catálogo tenant-wide (like `articulos`) | `lotes_tenant`, FORCE |
| `stock_lotes` | **NEW table** | operativa (like `stock`) | `stock_lotes_tenant`, FORCE |
| `movimientos_stock` | +1 nullable column, +1 FK, +1 index | unchanged | unchanged |
| `motivo_stock` | +2 enum values | — | — |
| `articulos` | +1 NOT NULL DEFAULT false column, +1 partial index | unchanged | unchanged |
| `items_comprobante_venta` | +1 nullable column, +1 FK, +1 index | unchanged | unchanged |
| `items_comprobante_compra` | +3 nullable columns, +1 FK, +1 index, +1 CHECK | unchanged | unchanged |
| `stock` | **NONE** | — | — |
| `parametros` | **NONE** (2 registry entries) | — | — |

**Zero rows of existing data are rewritten. No backfill migration. No view, no materialized
view, no trigger, no function.**

## Affected Areas

| Area | Impact | Description |
|---|---|---|
| `src/Ways.Domain/Stock/` | New/Modified | `Lote`, `StockLote`; `MotivoStock` +2; `MovimientoStock.IdLote` |
| `src/Ways.Domain/Articulos/Articulo.cs` | Modified | `ControlaLote` |
| `src/Ways.Domain/Ventas/ItemComprobanteVenta.cs` | Modified | `IdLote` (snapshot) |
| `src/Ways.Domain/Compras/ItemComprobanteCompra.cs` | Modified | `CodigoLote`, `FechaVencimiento`, `IdLote` |
| `src/Ways.Domain/Catalogos/ParametroConocido.cs` | Modified | 2 keys |
| `src/Ways.Domain/Stock/` (new rules) | New | FEFO ordering rule + effective-lot-control rule, pure and unit-testable without a DB (the `PoliticaDeRoles` pattern) |
| `src/Ways.Infrastructure/Persistencia/Configuraciones/` | New/Modified | `LoteConfiguration`, `StockLoteConfiguration`, + 5 modified |
| `src/Ways.Infrastructure/Persistencia/WaysDbContext.cs` | Modified | Hand-rolled tenant filter for `stock_lotes` (the `Stock` precedent) |
| `src/Ways.Infrastructure/Persistencia/Migraciones/` | New | `LotesYVencimientosEtapa12` |
| `src/Ways.Application/Stock/ServicioDeStock.cs` | Modified | Lot-aware transfer/ajuste/conteo, `decomiso`, reconciliation |
| `src/Ways.Application/Stock/ServicioDeLotes.cs` | New | Get-or-create, sin-identificar, `reclasificacion`, FEFO query |
| `src/Ways.Application/Ventas/ServicioDeVentas.cs` | Modified | Batched parametros (2→1), FEFO planning, per-lot writes, per-lot anulación |
| `src/Ways.Application/Compras/ServicioDeCompras.cs` | Modified | Lot at draft/confirm/anulación |
| `src/Ways.Application/Reportes/ServicioDeReportesDeStock.cs` | Modified | Vencimientos report (+ `TablaExportable` mapping) |
| `src/Ways.Api/Endpoints/{Stock,Reportes}Endpoints.cs` | Modified | `/stock/decomiso`, `/stock/lotes*`, `/reportes/stock/vencimientos` + `/export` |
| `src/Ways.Web/src/paginas/` | Modified/New | `Pos`, `CompraEditor`, `Transferencias`, `ConteoDeInventario`, `Articulos`, `Parametros`, `Tablero`; new `Vencimientos` |
| Database | **See *Modelo de datos propuesto*** | 2 new tables, 6 additive columns, 2 enum values |

## Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| **A deadlock between a checkout and a transfer once the lock unit gains a third component** — the stage's worst failure: intermittent, production-only, and the three write sites do not share a helper | Med | One order stated once in the spec (`id_articulo, id_punto_venta, id_lote NULLS FIRST`, aggregate row first); a concurrency test **per write site**, not one shared assertion |
| **The module costs the POS something while off**, violating doc-11:148 | Med | Branch on `controla_lote` (already loaded, no probe query); parametros batched 2→1; a **query-count test** asserting the off path issues no more round-trips than today |
| `SUM(stock_lotes) ≠ stock.cantidad` drifting silently for a lot-effective articulo | Med | Both invariants asserted as spec scenarios with tests; `reclasificacion` pairs proven net-zero; conteo made per-lot (decision 11) rather than left aggregate |
| **Three independent writers implementing the lot rule differently** (doc-11's own "tamaño: grande" risk) | High | Slice boundaries follow the writers (4, 5, 7, 8); each ships its own tests; the shared rule lives in a pure Domain helper even though the raw-SQL bodies stay duplicated by design |
| NCX with no identifiable lot at the counter | Med | Sin-identificar lot as the explicit escape hatch; the counter is never blocked (decision 8) |
| A lot picker slowing the checkout | Med | FEFO pre-selected server-side; the picker is opt-in; `idLote` is optional in the request and defaulted by the server |
| Expiry dates computed in UTC instead of the PV's `zona_horaria` (stage-11's slice-9 bug class, which needed a post-archive hardening commit) | Med | Verify criterion with an explicit non-UTC test, called out in the spec |
| Scope creep into push notifications or unit-level traceability | Med | Explicit out-of-scope list; decision 10 names pull as *the* channel stage 13 inherits |
| A slice overflowing the 400-line budget (11 slices, several genuinely large) | High | Pre-identified split points per slice below; conteo-per-lot has a pre-approved degradation (decision 11) |
| `ALTER TYPE ADD VALUE` used in the same transaction as its migration | Low | Stated as a binding migration note (decision 9) |

## Rollback Plan

**Per slice**: every schema element is additive and nullable (or `NOT NULL DEFAULT false`),
so reverting a slice's code leaves inert columns. No existing row is rewritten anywhere in
this stage.

**Runtime**: setting `lotes_habilitado = false` for an empresa returns it to today's exact
behaviour **without touching a single stock row** — the lot history stays as inert audit,
and re-enabling re-runs the idempotent reconciliation.

**Whole stage**: dropping `lotes`/`stock_lotes` and the six columns returns the schema to
stage 11 with two orphan `motivo_stock` values (Postgres cannot drop enum values) — a
residue in a type, no data harm. Stated as the one irreversible artifact of the stage.

## Dependencies

- **Stage 11** (archived): the `TablaExportable` / `IExportadorDeTabla` / `/export`-sibling
  house standard, consumed verbatim by the planilla de vencimientos — "one mapping and one
  route line" (stage-11 archive §8 Handoff). Also `ix_stock_punto_venta`'s access shape,
  which `ix_stock_lotes_punto_venta` mirrors.
- **Stage 8**: `ServicioDeCompras`, transfers and conteo — the three write sites this stage
  extends, and their lock-order discipline.
- **Stage 10**: the Tablero the expiry tile lands on; `zona_horaria` for the "hoy"
  resolution.
- **Stage 5**: `stock` / `movimientos_stock` and the sum-invariant this stage must preserve.
- No new NuGet package. No new web dependency.

## Success Criteria

- [ ] With `lotes_habilitado = false`, a checkout issues **no more round-trips than before
      this stage** — asserted by a query-count test, not by inspection.
- [ ] With the module on and no lot-controlled articulo in the cart, likewise.
- [ ] `stock.cantidad = SUM(movimientos_stock.cantidad)` still holds across **eight**
      motivos, including a sequence containing `decomiso` and a `reclasificacion` pair.
- [ ] `stock_lotes.cantidad = SUM(movimientos with that lot)` holds after a mixed sequence of
      compra, venta, transferencia, NCX, anulación, conteo and decomiso.
- [ ] For a lot-effective `(articulo, PV)` after reconciliation,
      `SUM(stock_lotes) = stock.cantidad`.
- [ ] Reconciliation is idempotent: a second run writes zero rows.
- [ ] A checkout omitting `idLote` for a lot-controlled articulo succeeds and picks the FEFO
      lot; a checkout supplying a valid one is honoured.
- [ ] Anulación of a lot-bearing sale reverses the **exact** lot, proven by test.
- [ ] A concurrent checkout and a reverse transfer of the same articulo+lots do not deadlock
      — one test per write site.
- [ ] A transfer that would leave the origin **lot** negative is refused, even with a
      sufficient aggregate.
- [ ] The vencimientos report resolves "hoy" in the PV's `zona_horaria`, proven with a
      non-UTC zone.
- [ ] The vencimientos export exists and its figures equal the JSON endpoint's (stage-11's
      binding invariant).
- [ ] Domain / Application / Integration / vitest suites all green; descriptor tests for
      every new or modified screen (`web-descriptor-tests`).

## Plan de slices (tentative — `sdd-tasks` owns the final breakdown)

Stacked-to-main, one judgment-day round per slice (`protocolo-pr-solo-dev`).

| # | Branch | Content | ~lines |
|---|---|---|---|
| 1 | `feat/stage12-slice1-esquema` | The whole migration: `lotes`, `stock_lotes`, 6 columns, 2 enum values, RLS, EF configs, tenant filter, migration/RLS tests. **No writer.** | ~400 |
| 2 | `feat/stage12-slice2-activacion` | 2 parametros; effective-lot-control Domain rule; **batched parametro read in `ServicioDeVentas` (2→1)** + its query-count test | ~250 |
| 3 | `feat/stage12-slice3-lotes-reconciliacion` | `ServicioDeLotes` (get-or-create, sin-identificar), `reclasificacion` pair, activation hook + admin re-run endpoint | ~380 |
| 4 | `feat/stage12-slice4-recepcion` | Compra: draft lot input, get-or-create at confirm, per-lot movement + balance, per-lot anulación refusal | ~400 |
| 5 | `feat/stage12-slice5-venta-fefo` | FEFO planning in the decide phase, per-line `idLote`, item snapshot, per-lot writes in the pinned order, exact anulación, concurrency test | ~400 |
| 6 | `feat/stage12-slice6-ncx` | NCX lot rules + expired-lot warning contract (may fold into 5 if it comes in small) | ~220 |
| 7 | `feat/stage12-slice7-transferencias` | Lot travels; extended lock order; per-lot sufficiency refusal; expired-lot refusal; concurrency test | ~350 |
| 8 | `feat/stage12-slice8-ajuste-decomiso-conteo` | Lot-aware ajuste, `POST /stock/decomiso`, per-lot conteo (degradation pre-approved) | ~400 |
| 9 | `feat/stage12-slice9-vencimientos` | Report + `/export` sibling + Tablero tile, `zona_horaria`-correct "hoy" | ~300 |
| 10 | `feat/stage12-slice10-web-operacion` | POS lot picker (FEFO pre-selected) + reception lot input + descriptor tests | ~400 |
| 11 | `feat/stage12-slice11-web-backoffice` | Vencimientos screen, `controla_lote` on the articulo editor, `lotes_habilitado` toggle, lot column in transfers/conteo | ~400 |

Merge order: `1 → 2 → 3 → {4, 5→6, 7, 8 sequential on the stock surface} → 9 → {10 needs 4+5, 11 needs 8+9}`.

**Review Workload Forecast (preliminary — `sdd-tasks` produces the binding one)**

- Estimated total: **~3 900 lines** across 11 slices.
- **Chained PRs recommended: Yes.** `chain_strategy: stacked-to-main`.
- **400-line budget risk: High.** Slices 1, 4, 5, 8, 10 and 11 sit at the cap.
  Pre-identified split points: slice 1 at the tables/columns boundary; slice 4 at the
  draft-input/confirm boundary; slice 5 at the FEFO-planning/transaction-write boundary;
  slice 8 at the decomiso/conteo boundary; slices 10 and 11 per screen.
- **Decision needed before apply: Yes — already resolved**: `auto-chain`,
  `stacked-to-main`. No `size:exception` anticipated; as in every prior stage, overflow is
  expected to come from **test depth**, not scope.

## Deferred / adjacent (recorded, not in scope)

- **Push alerts** (email/notification, scheduler, delivery state) — stage 13 owns the
  decision with a second real use case in hand (decision 10).
- **Per-articulo `dias_alerta_vencimiento` override** — additive column, no migration of
  existing data.
- **Per-empresa lot control of the same articulo** — an additive `controla_lote` override on
  `articulos_empresas` (decision 2).
- **Multi-lot sale lines** — a child table of the item, additive (decision 4).
- **`lotes.id_proveedor` / `fecha_elaboracion`** — additive nullable columns when a real
  recall or supplier-quality use case appears.
- **Recall / trazabilidad hacia el cliente** — derivable from the item snapshot this stage
  creates; no surface built.
- **Lot-aware costing** — cost stays per articulo (stage 9's model, untouched).
- **`articulos_empresas` replace-set concurrency gap** and the **importe CHECK micro-gate** —
  carried from stage 8, still open, untouched here.
- **`ways_owner` as a testcontainer superuser** — repo-wide migration-test weakness, and
  **relevant to this stage** (unlike stage 11): this is the first stage since 9 to ship a
  real migration with RLS on new tables, so the weakness affects how strongly the new RLS
  policies can be tested. Recorded for `sdd-design`.
- **Containment/import-boundary lint rule** — stage-10/11 carryover, unaffected here.

## Proposal question round

Each records the assumption taken, so a correction is cheap. **None of these blocks
spec/design**; all are recorded for the owner.

1. **May a cashier sell from an expired lot?** Assumed **yes, with a prominent warning**
   (decision 12) — the codebase's own principle is that the counter never blocks. The
   alternative (hard refusal) is one validation away. *This is the most product-weight call
   of the stage.*
2. **Is FEFO a suggestion or an imposition?** Assumed **suggestion** — server-computed
   default, operator-overridable, because the operator is holding a physical package and
   forcing a different lot would make the data a lie (decision 4).
3. **Should the "sin identificar" lot be offered first or last by FEFO?** Assumed **first**
   — it is the oldest physical stock at activation, and parking it last would make it
   immortal (decision 4).
4. **Should `conteo` count per lot, or be refused for lot-controlled articulos?** Assumed
   **per lot** (decision 11), with the refusal pre-approved as the degradation if slice 8
   overflows.
5. **"Alertas" as a pull report + tile, with no push channel?** Assumed **yes** (decision
   10). No notification infrastructure exists anywhere in `src`; building it here would be
   deciding it blind. **Handoff**: stage 13 inherits *pull* as the shared alert channel.
6. **Is one lot per sale line acceptable, forcing a split when a lot runs short?** Assumed
   **yes** (decision 4) — it is honest, and it is what makes anulación exact.
