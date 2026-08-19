# Proposal: Stage 15 — Cuenta corriente de proveedores (ledger)

## Intent

doc-11:231-249 asks for the other side of the counter: promote the **derived** proveedor saldo
(`Σ compras confirmadas − Σ gastos ligados`) into **a ledger of its own** with immutable
movements, partial payments, imputación a comprobantes and a queryable history — the design of
Etapa 7 applied to money going **out** instead of coming in.

`explore.md` proves how thin the current state is, and the spec says so in its own words:

| Today | Evidence | Verdict |
|---|---|---|
| No table, no cache, no ledger | `openspec/specs/saldo-de-proveedor/spec.md:13-19` — *"Saldo Is A Derived Read, Never Persisted"* | The read is honest but reconstructs nothing |
| Two aggregate EF queries per read | `ServicioDeSaldoDeProveedor.cs:30-43` | No lock, no writer, no history |
| Payment = a `gasto` with `categoria = proveedor` | `ServicioDeGastos.cs:134-174` | Correct money-wise; invisible as an account |
| Debt = a confirmed compra | `ServicioDeCompras.cs:312-352` | Never touches `proveedores` |
| The saldo is **declared an approximation** | `spec.md:57-63`, doc-10:832-834 | An approximation is not an invariant |
| `proveedores` has **no** `saldo` | doc-10:175-184 | doc-10 principle 7 (*nada de saldos sin libro*) is satisfied only because there is no saldo at all |

Two consequences the business already feels. First, **nothing can be reconstructed**: "why do I
owe this supplier 47.300?" has no answer beyond re-running two SUMs over rows that may have been
edited for unrelated reasons. Second, **the approximation lies in one specific, reachable way**:
an unlinked proveedor gasto reduces the saldo without settling any compra (`spec.md:65-70`), so
the total and the per-compra statuses can disagree with each other and nobody can tell which one
is wrong.

This stage is not legacy parity. `alsina/` E8 Proveedores is a bare ABM
(doc-01:336-339) — **no supplier cuenta corriente ever existed in the legacy**. Etapa 15 mirrors
Etapa 7's *design pattern*, and it is the first stage of the post-parity programme that is **not
purely additive**: `proveedores.saldo` starts with real history behind it and therefore requires
a backfill.

## Scope

### In Scope

- **One append-only ledger, `movimientos_cuenta_corriente_proveedor`** — operativa scoping,
  no `EntidadBase`, `saldo_resultante` snapshotted per row, four movement types
  (`apertura | compra | pago | ajuste`). One migration (gate section).
- **`proveedores.saldo` as a maintained cache** with exactly ONE write authority, mirroring
  stage 7: a single `EscriturasDeCuentaCorrienteProveedor` holding the one raw
  `UPDATE proveedores SET saldo = saldo + $1 ... RETURNING` and the one raw ledger `INSERT`
  (decision 6).
- **Opening asiento per proveedor** (decision 1): the migration writes one `apertura` movement
  computed with the **exact formula of the current `saldo-de-proveedor` spec**, so the ledger
  starts provably equal to the read it replaces. No synthetic replay.
- **Debt on confirm**: `ServicioDeCompras.ConfirmarAsync` writes one positive `compra` movement
  in its existing transaction.
- **Contramovimiento on anulación** (decision 5): `AnularAsync` writes one negative `ajuste`
  that reverses the **debt** of the annulled compra. *"Sin motor de reversión de gastos"*
  (doc-10:465-466) stays true — payments are not reversed.
- **Payment stays `gastos`** (decision 2): a gasto with `categoria = proveedor` **and** an
  `id_proveedor` writes one negative `pago` movement in the same transaction — inheriting the
  open-turno guard and the arqueo egress term **with no new derivation**.
- **Imputación a comprobantes, explicit and per movement** (decision 7): the `pago` movement
  carries the compra it settles, so the per-compra payment status becomes one indexed
  aggregation over the ledger instead of a `gastos` GROUP BY. No FIFO.
- **Ajuste manual** with a required `detalle`, under a **new** Supervisor + Admin policy
  (decision 8).
- **Estado de cuenta API + screen**: `GET /api/proveedores/{id}/cuenta-corriente` (movement list
  with running balance, desde/hasta, histórico) and the mirror screen; the existing
  `GET /api/proveedores/{id}/saldo` is **re-sourced from the ledger with its response shape
  unchanged**.
- **Pinned total lock order** (decision 9), verified against both existing write paths.
- **doc 10 gains the table** (a §8-adjacent subsection), the `proveedores.saldo` column in §2 and
  the "Estado (Etapa 15)" annotation, written from inside slice 1 (stage-12 task-1.17
  discipline).

### Out of Scope

- **Reliquidación a precio del día** (decision 3). No economic symmetry: a client's unpaid fiado
  loses value to inflation *for us*; our payable to a supplier does not. doc-11:246-249 does not
  ask for it, and `proveedores` has no `id_lista_precio` to re-price against.
- **Retenciones y notas de crédito de proveedor** (decision 4) — deferred **with the reopen
  condition named**, and with no speculative enum value reserved for them.
- **FIFO / automatic allocation** of a payment across several compras (decision 7). The operator
  imputa a payment to a compra explicitly, exactly as the existing gasto link already does.
- **A dedicated payment comprobante** (a `proveedores` analogue of `RC`). `gastos` is already
  turno-scoped and already counted by `CalculadorDeArqueo`; a new comprobante would re-derive
  what already works (decision 2).
- **`limite_credito` / `credito_ilimitado` for proveedores.** They do not exist on the table and
  nothing in the business asks us to cap what a supplier lends us.
- **A partial-payment state on `estado_compra`.** Payment status stays a **derived** read, now
  sourced from the ledger (decision 7). No new enum value on `estado_compra`.
- **The `/export` sibling** of the estado de cuenta. Stage 7 shipped without one (the clientes
  export arrived with stage 11); doc-11 asks for "historial consultable", not for a download.
  Recorded as the cheapest first extension.
- **An audit action for the proveedor ajuste manual.** Stage 14 decision 5 excluded *ajuste
  manual de cuenta corriente and gastos* on the ground that they are already actor-stamped
  ledgers; this ledger is actor-stamped by the same criterion. Registered, not forgotten.
- **`saldo` in the proveedor ABM DTO.** The saldo endpoint exists for that; the ABM stays
  byte-identical and the saldo stays uneditable by hand (the same refusal stage 7 made for
  `clientes.saldo`).
- **Blocking the soft delete of a proveedor with a non-zero saldo.** Today's ABM allows it and
  the ledger is append-only, so nothing corrupts. Recorded as an open product question, not
  silently changed.
- **The owner's reserved items** (comisiones formula, Supervisor margin, `OperacionDePos` read
  model, cierre de caja por rol, export branding) and every carryover: `articulos_empresas`
  replace-set gap, the **importe CHECK micro-gate** (deliberately untouched — see gate §F),
  `ways_owner` superuser. Untouched.

## Capabilities

### New Capabilities

- **`cuenta-corriente-de-proveedores`** — owns the stage end to end: the ledger row's meaning and
  immutability, the `saldo = Σ importes` invariant and its single write authority, the four
  movement types with their per-tipo shape, the four write paths (apertura, confirm, gasto,
  anulación) and the manual ajuste, the pinned lock order, the imputación rule, the estado de
  cuenta read model (running balance, date filter, empty state), and the authorization split.

Following the stage-11/12/13/14 precedent, one capability owns its surface rather than smearing
one sentence across five specs. Unlike stage 7 (which split into four capabilities) there is no
reliquidación engine and no new comprobante tipo here, so the surface does not justify a split.

### Modified Capabilities

- **`saldo-de-proveedor`** — three of its four requirements change, and this is the stage's
  headline spec edit:
  - **REMOVED: *"Saldo Is A Derived Read, Never Persisted"*.** Directly superseded. Retired
    explicitly, the way stage 7 removed `consumo-cuenta-corriente`'s *"No Reliquidación, No CC
    Management, No Pagos De Cuenta"* — never left to rot as a contradicted requirement.
  - **REMOVED: *"Saldo Is An Approximation, Not An Invariant"*.** The saldo becomes an invariant;
    the invariant itself is stated by the new capability, not restated here.
  - **MODIFIED: *"Per-Compra Payment Status From Linked Gastos Only"*** → derived from the ledger
    movements imputed to that compra. The observable outcomes of its existing scenarios are
    preserved (a fully paid compra is `pagada`; an unimputed payment reduces the total without
    settling any compra), which is what makes the migration provable.
  - **UNCHANGED: *"Authorization And Scoping"*** — the read stays under `OperacionDePos`, still
    404 across tenants.
- **`gastos`** — ADDED: a gasto with `categoria = proveedor` **and** a non-null `id_proveedor`
  writes exactly one `pago` movement in the same transaction, and its `id_comprobante_compra`
  link becomes the movement's imputación. A gasto that fails either condition writes **no**
  movement — the same predicate the retired formula used, stated so the ledger cannot silently
  diverge from it.
- **`comprobantes-compra`** — ADDED: confirmar writes exactly one `compra` movement; anular
  writes exactly one reversing `ajuste`; the informational `gastosLigados` count and the
  no-reversal-of-gastos rule are unchanged.
- **`operacion-de-pos`** — the estado de cuenta and saldo reads live under `OperacionDePos`; the
  proveedor ajuste manual lives under the new Supervisor + Admin policy (mirroring how stage 7
  recorded its split here).

**Not modified**: `proveedores` (the ABM does not change), `arqueo-de-cierre` (a gasto's egress
term is untouched — no new term, no new formula), `auditoria-de-operaciones` (see Out of Scope),
`consumo-cuenta-corriente` / `pagos-a-cuenta` / `estado-de-cuenta` /
`reliquidacion-a-precio-del-dia` / `ajustes-de-cuenta-corriente` (the client-side ledger is not
touched: this stage adds a parallel table, it does not generalize the existing one),
`turnos-de-caja`, `stock`, `lotes-y-vencimientos`.

## Approach

**One ledger, one write authority, four write paths, one screen.**

1. **The saldo is a cache of the book, never a number someone typed.** `proveedores.saldo` moves
   only through the one raw `UPDATE ... RETURNING`, and every movement is inserted by the one raw
   `INSERT ... RETURNING`, both living in a single `EscriturasDeCuentaCorrienteProveedor` —
   verbatim structural copy of `EscriturasDeCuentaCorriente.cs`, including its two hard-won
   details: never a tracked `proveedor.Saldo +=` (a retry would double-count) and
   **`DateTimeOffset` normalized to UTC before any raw-ADO parameter** (the PR #129 lesson, whose
   fix had to reach five call sites).
2. **The migration proves itself against the spec it retires.** The opening asiento is computed
   with the retired formula, so slice 1 can assert *"for every proveedor, the new
   `proveedores.saldo` equals what `ServicioDeSaldoDeProveedor` returned before the migration"*
   over a fixture that mixes borrador, confirmada, anulada, linked, unlinked and
   soft-deleted rows. Backfill correctness is a test, not a claim.
3. **Reuse the payment path instead of inventing one.** The ledger write joins
   `ServicioDeGastos.InsertarGastoAsync` inside its existing transaction, **after** the turno
   `FOR SHARE` and the compra `FOR SHARE` it already takes. The cash keeps landing in the arqueo
   through the existing `gastos` egress term.
4. **One movement per business act**, with the same decide-then-commit posture,
   `EstrategiaSinReintento`, `ManejadorDeErrores` mapping and RLS + manual tenant filter as
   stages 5-8-14.
5. **Locks are pinned, not discovered** (decision 9) — and the rule is stated as an invariant
   (`proveedores` is the LAST row lock before the ledger INSERT) so it survives a future write
   path that nobody has written yet.
6. **DB CHANGE GATE (CLAUDE.md), exercised in autonomous mode.** This is the stage's centre of
   gravity: one new table, one new enum type, **two ALTERs over existing tables** and **one data
   statement over existing rows** — the first non-additive migration of the programme. The
   contract is the `Modelo de datos propuesto` section below.

## Autonomous decisions

Under delegated technical authority, conservative and reversible bias. Decisions 1-5 formalize
the five `Orchestrator Decisions` recorded at the foot of `explore.md`; 6-9 are the ones the
proposal had to resolve to make the model complete. Each records context, options with
tradeoffs, the decision, and **what it costs to reverse it**. Refinements and refutations are
collected again in the closing section.

---

### 1 — Migration = **one `apertura` movement per proveedor**, computed with the retired formula. Not a synthetic replay. And the opening movement needs a shape of its own.

**Context.** doc-11:246-247 leaves it open: *"asiento de apertura por proveedor versus
reconstrucción desde el historial de compras"*. This is the first stage of the programme with
existing history to reconcile — stage 7 shipped with **zero** backfill.

**Options.**

| Option | Pro | Contra |
|---|---|---|
| **Opening `apertura` movement per proveedor** | Cheap and O(#proveedores); provable against the spec being retired; the row is *honest about what it is* — a derivation, with the derivation in its `detalle` | Per-movement provenance before the cutover is lost (it never existed) |
| Full synthetic replay as `compra`/`pago` rows | Looks like a real history | **Fabricates provenance that never existed**: a synthetic `pago` would need an `id_gasto`, a PV, an actor and a date it can only invent, and the resulting rows would be indistinguishable from real ones. Migration size grows with the tenant's entire history |
| No ledger row, just `UPDATE proveedores SET saldo = <derived>` | Smallest diff | Violates doc-10 principle 7 (*nada de saldos sin libro*) **on day one**: a saldo with no book behind it is exactly what this stage exists to remove |

**Decision.** Opening `apertura` movement, with three sub-decisions the explore left implicit:

- **A proveedor whose derived saldo is `0` gets NO row.** Zero-delta no-op, the same rule stage
  7 applied to a no-op reliquidación and stage 8 to a conteo without difference. Most tenants'
  proveedores are quiet; the migration should not write a table full of zeroes.
- **The `apertura` movement has NO `id_punto_venta` and NO `id_empleado`** (both nullable, both
  NULL for this tipo only, CHECK-enforced — gate §B). The derived saldo aggregates across every
  PV of the tenant and no human performed this act. Picking "the first PV" and "some admin"
  would fabricate provenance at a smaller scale — the same sin this decision just refused, in a
  repo that shipped an audit stage last week.
- **`apertura` is its own enum value**, not an `ajuste`. With `ajuste` the CHECK could not tell
  an actor-less opening row from a manual ajuste (which MUST have an actor), so the NOT NULL
  guarantee for every human-written movement would be lost at the database level. It also makes
  *"why is my saldo 47.300 with no history?"* answerable in one query.

**Cost of reversing.** The opening rows are ordinary ledger rows: a correction is a new `ajuste`,
never an edit. If the whole stage is rolled back, dropping the column loses nothing —
`comprobantes_compra` and `gastos` are untouched, so the retired formula still computes the same
number from the same rows. Going the other way (discovering later that a replay was needed) is
impossible to do honestly at any time, so it is not a direction we are giving up.

---

### 2 — **The payment stays a `gasto`.** The ledger writer joins `ServicioDeGastos`. And the composite FK needs an alternate key on `gastos` that **does not exist yet**.

**Context.** doc-11:247-248: *"si el gasto ligado sigue siendo el mecanismo de pago o se
reemplaza por un movimiento de pago propio"*.

**Options.**

| Option | Pro | Contra |
|---|---|---|
| **Keep `gastos`** | Already turno-scoped (`ExigirTurnoAbiertoBajoLockAsync`), already an egress term of `CalculadorDeArqueo`, already has the compra-link TOCTOU guard (`ExigirCompraLigableAsync`, `FOR SHARE`), already has a `medio_pago`. Zero new derivation | The ledger writer lands inside `ServicioDeGastos` rather than in a clean parallel service |
| A dedicated payment write path | A tidy parallel service | Must re-derive the turno guard, the arqueo term and the medio de pago, or the cash becomes invisible to the cierre — **the exact gap stage 7 created the `RC` comprobante to avoid**. Two ways to pay a supplier is a support problem, not a feature |
| A `proveedores` analogue of the `RC` comprobante | Symmetry with stage 7 | `RC`'s value was *acquiring* turno/arqueo/numeración. `gastos` already has them; a compra-side comprobante would add a numeración series nobody asked for |

**Decision.** `gastos` stays. The `pago` movement is written inside
`InsertarGastoAsync`'s existing transaction, and the ledger row carries `id_gasto` so any
movement can be traced back to the physical payment.

**Verified correction to Orchestrator Decision 2.** It says *"verificar/agregar AK `(Id,
IdTenant)` en `gastos` como parte del gate"*. **Verified: the alternate key does NOT exist.**
`GastoConfiguration.cs` has no `HasAlternateKey` (unlike `ProveedorConfiguration.cs:151`,
`ComprobanteCompraConfiguration.cs:49`, `TurnoCajaConfiguration.cs:38` and fifteen others). So
the composite FK **requires an `ALTER TABLE gastos ADD CONSTRAINT ... UNIQUE`**, which is a
second ALTER over an existing table and is therefore in the gate contract (§D) rather than
assumed. The alternative — a **simple** FK to `gastos(id_gasto)` — was considered and rejected:
it is available (the PK covers it) but it drops the cross-tenant guard every other composite FK
in this schema carries, and it would make the proveedor ledger the only operativa table whose
FKs are inconsistent with each other.

**Cost of reversing.** Moving the payment to its own write path later leaves the ledger shape
untouched (`id_gasto` becomes nullable-and-unused for new rows, exactly as
`motivo_stock`'s reserved values were harmless). The alternate key on `gastos` is
structurally unviolable (`id_gasto` is already unique) and can be dropped with no data change.

---

### 3 — **Reliquidación is OUT of scope**, and the enum reserves nothing for it.

**Context.** `explore.md` §2 asks for this to be confirmed rather than assumed, since Etapa 7's
centrepiece was the re-pricer.

**Decision.** Out of scope, on three independent grounds:

- **No economic symmetry.** Reliquidación exists because unpaid fiado *owed to us* loses value to
  inflation (doc-01:401-402). A payable *we owe* does not lose value for us — re-pricing it
  upward would mean voluntarily increasing our own debt.
- **doc-11 does not ask for it.** Etapa 15's alcance names pagos parciales, imputación and
  historial; its open decisions name retenciones and notas de crédito. Reliquidación appears
  nowhere.
- **The machinery has nothing to key off.** Stage 7 re-prices against the client's
  `id_lista_precio`; `proveedores` has no lista and no `precios` relationship
  (doc-10:175-184).

Consequently the enum reserves **no** `actualizacion_precios` value, and `tipo_movimiento_cc`
is **not** reused (its four values include one that would be permanently dead here).

**Cost of reversing.** If a supplier-side re-pricing case ever appears, it needs a new enum value
(`ALTER TYPE ... ADD VALUE`, one migration) plus an engine. Reserving the value now would cost
the same migration later **and** carry a value with no writer forever — the direction with no
upside.

---

### 4 — **Native enum `tipo_movimiento_cc_proveedor`**, four values, no speculative ones. Not `text` + CHECK.

**Context.** Orchestrator Decision 4 chooses the enum and asks the proposal to argue it against
stage 14's opposite call (`auditoria.accion` is `text` + CHECK because `ALTER TYPE ... ADD VALUE`
is irreversible — proven in stage 12).

**Both precedents are right, because the two columns are different animals.**

| | `auditoria.accion` (stage 14 → `text`) | `tipo_movimiento_cc_proveedor` (this stage → enum) |
|---|---|---|
| Cardinality | **Open**, grows with **every future stage** | **Closed**: 4 values, one writer each |
| Who owns the value set | A catalog in the application | The arithmetic itself — the tipo decides the sign and the required shape |
| Joined / matched on | Never | Yes: the per-compra imputación aggregation and the per-tipo shape validator |
| Growth cost | One irreversible migration **per stage** | One migration, once, if retenciones ever arrive |

The decisive argument is the stage's own framing: **this table is read side by side with
`movimientos_cuenta_corriente`**, whose `tipo` is a native enum
(`MovimientoCuentaCorrienteConfiguration.cs:35-38`). Making the mirror table's discriminator a
`text` column would be a gratuitous asymmetry in the one place the repo most wants symmetry, and
doc-10 principle 4's actual prohibition is against **padrones editable by the user** becoming
enums — this is neither editable nor a padrón. Every other ledger discriminator in the schema
(`motivo_stock`, `tipo_movimiento_caja`, `tipo_movimiento_tesoreria`, `categoria_gasto`) is a
native enum.

**Decision.** `CREATE TYPE tipo_movimiento_cc_proveedor AS ENUM
('apertura', 'compra', 'pago', 'ajuste')` — declared in lifecycle order, because the type is
generated from the C# member order by `npgsql.MapEnum<T>()` in
`WaysDbContextFactory.cs:33-47` **and** `DependencyInjection.cs:97-111` (both, never also via
`HasPostgresEnum` — `WaysDbContext.cs:183-186`).

**Refinement of Orchestrator Decision 4.** It fixes the launch set at `compra | pago | ajuste`
and forbids speculative values. The set ships as **four** values because `apertura` is **not**
speculative: it has a writer **in this stage** (the migration of decision 1), and without it the
opening asiento would have to masquerade as an `ajuste` and destroy the actor NOT NULL guarantee
(decision 1). The no-speculation rule is respected exactly: every value has a writer on day one.

**Retenciones y notas de crédito: DEFERRED**, with the reopen condition named — the first real
retention or supplier credit note a customer needs. They cost one `ALTER TYPE ... ADD VALUE` in
its own migration (it cannot be referenced in the transaction that adds it, proven in stage 12)
plus a write path. Shipping the values now would put two permanently unwritten labels into an
irreversible type.

**Cost of reversing.** Adding a value later: one migration, irreversible but bounded. Removing
one: impossible — hence zero speculative values. Going from enum to `text` later is a trivial
cast; the opposite is a validating rewrite. The expensive direction is the one nobody needs.

---

### 5 — **Contramovimiento on anulación de compra: IN scope.** The debt is reversed; the payments are not.

**Context.** `ServicioDeCompras.EjecutarAnulacionAsync` reverses stock and **counts** linked
gastos purely informationally (`ServicioDeCompras.cs:593-594`, *"NUNCA bloquea"*);
doc-10:465-466 records *"sin motor de reversión de gastos"*.

**Decision.** Anulación writes exactly one negative `ajuste` movement (`importe` = −the
`compra` movement's importe, `id_comprobante_compra` = the annulled compra) inside the existing
anulación transaction — the exact mirror of `ServicioDeVentas.EjecutarAnulacionAsync`
(`ServicioDeVentas.cs:657-662`), which writes an `Ajuste` of `-movimiento.Importe` for the same
reason. Not optional: **a ledger that diverges from the truth on the first anulación is born
broken**, and the divergence would be silent and permanent.

Two consequences stated rather than discovered:

- ***"Sin motor de reversión de gastos" survives verbatim.*** The gasto stays, its `pago`
  movement stays. What is reversed is the **debt**, not the payment.
- **A fully-paid compra that is then annulled leaves a credit** (`+1000` compra, `−1000` pago,
  `−1000` ajuste ⇒ `−1000`): a negative saldo meaning *the supplier owes us*. That is the honest
  arithmetic — we paid for goods we returned — and it is the same "saldo a favor" state stage 7
  chose for an overpayment (decision 9 there). It is surfaced in the UI as such, never clamped
  to zero.

**Cost of reversing.** Removing the contramovimiento later means explaining a permanent gap
between the ledger and reality. Adding it later would mean a second backfill over annulled
compras. This is the only direction that is cheap at any point in time.

---

### 6 — **One write authority**: `EscriturasDeCuentaCorrienteProveedor`, a structural copy of the stage-7 class. Not a method per service.

**Context.** Four write paths (migration, confirm, gasto, anulación, plus the manual ajuste)
mutate the same cached saldo. Stage 7's answer is a single static class holding **exactly one**
`UPDATE ... RETURNING` and **exactly one** `INSERT ... RETURNING` for the whole codebase
(`EscriturasDeCuentaCorriente.cs:6-20`).

**Decision.** The same shape, for the same reason its doc-comment gives: *"la extracción es lo
que compra seguridad"*. Three properties are copied deliberately, not incidentally:

- **Raw `UPDATE proveedores SET saldo = saldo + $1 ... RETURNING saldo`**, never a tracked
  `proveedor.Saldo +=` — a `CreateExecutionStrategy` retry would double-count the increment.
- **`id_tenant` in the `WHERE` in addition to the id** — RLS already isolates; this is the cheap
  second layer the whole repo applies.
- **UTC normalization of every `DateTimeOffset` raw-ADO parameter** — not a style choice: the
  missing normalization was a real 500 against `timestamptz` and PR #129 had to fix a fifth call
  site after four were already patched.

Plus one property this ledger adds: a **per-tipo shape validator** in the writer (the
`ValidarFormaPorTipo` pattern, `EscriturasDeCuentaCorriente.cs:102-122`) pinning that `compra`
carries a comprobante and no gasto, `pago` carries a gasto, `apertura` carries neither and has no
actor/PV, and `ajuste` is structurally dual (contramovimiento or manual).

**Cost of reversing.** None worth naming: this is a containment decision. Un-extracting it later
would be a refactor nobody would propose.

---

### 7 — **Imputación is explicit and per movement**, in ONE `id_comprobante_compra` column with per-tipo meaning. No FIFO, no allocation table.

**Context.** doc-11:235-236 explicitly asks for *"pagos parciales, imputación a comprobantes"* —
which is where Etapa 15's alcance is **wider** than stage 7's (stage 7's decision 2 refused
invoice-level imputación outright). The mechanism already exists in embryo:
`gastos.id_comprobante_compra` is exactly "which compra this payment settles".

**Options.**

| Option | Verdict |
|---|---|
| **One `id_comprobante_compra` column, meaning per tipo** (origin for `compra`, imputación target for `pago`/`ajuste`) | **Chosen.** The question a human asks — *"which compra does this movement affect?"* — has ONE answer column, so the per-compra status is one indexed `SUM(importe) GROUP BY id_comprobante_compra`. Precedent: `movimientos_cuenta_corriente.id_comprobante_venta` already carries per-tipo meaning (the consumo that originated it, and the `RC` for a pago) |
| Two columns (`..._origen` + `..._imputado`) | Rejected: mutually exclusive by construction (a `compra` movement never imputes, a `pago` never originates), so the split buys a name and costs a second FK, a second support index and a two-column aggregation |
| FIFO / automatic allocation | Rejected: a different product (partial-payment, over-payment and re-allocation rules), and the operator already chooses the compra today |
| An `imputaciones` join table (N:M) | Rejected: it would allow one payment across several compras, which nothing in doc-11 or the current UI asks for, and it makes the running balance a two-table read |

**Decision.** One column. **Per-compra payment status becomes a derived read over the ledger**:
for a confirmed compra, `SUM(importe) WHERE id_comprobante_compra = X` is the amount still owed —
`= total` ⇒ `impaga`, `<= 0` ⇒ `pagada`, otherwise `parcial`. The retired formula's observable
outcomes are preserved, including the one that made the old read an approximation: **an
unimputed payment reduces the total saldo without settling any compra** — now a stated,
tested property instead of a documented weakness.

**Cost of reversing.** Adding an allocation table later is additive: the column becomes the
default single imputación and existing rows migrate mechanically (one row per non-null value).
Removing an allocation table after payments were split across compras is a reconciliation
project.

---

### 8 — Ajuste manual gets a **NEW policy**, `SupervisionDeCuentaDeProveedor` (Supervisor + Admin). `SupervisionDeCuentaCorriente` is not reused.

**Context.** `explore.md` flags the reuse as a *"policy-semantics stretch"*. The existing
policy's own doc-comment (`Politicas.cs:50-57`) says it was named generically *"para que el
tightening diferido de cierre (open question de stage 6) pueda apilarse acá"* — reserved for
**cierre de caja**, not for the supplier side.

**Options.**

| Option | Verdict |
|---|---|
| Reuse `SupervisionDeCuentaCorriente` | **Rejected.** Its generic name is already promised to a different future tightening; every current caller is client-side. Reuse makes a later divergence a migration of intent instead of a claim edit |
| Stack it under `OperacionDePos` | Rejected: an ASP.NET AND-composition where Admin ∈ {Supervisor, Admin} would be a partial no-op suggesting a relationship that does not exist (the reasoning stage 14 decision 6 used verbatim) |
| Leave the ajuste under `OperacionDePos` | Rejected: a discretionary balance mutation is a different risk class from capturing a payment — the same judgement stage 7 made for the client side |
| **A new `SupervisionDeCuentaDeProveedor`, Supervisor + Admin** | **Chosen** |

**Decision.** `Politicas.SupervisionDeCuentaDeProveedor` (`supervision_cuenta_proveedor`), same
claim set as `SupervisionDeCuentaCorriente`, its own name. The identical-claims-own-name pattern
is already established: `LecturaDeAuditoria` has exactly `LecturaDeRentabilidad`'s claim set and
still earned its own name, *"un dato distinto"* (`Politicas.cs:73-79`). Reads (saldo, estado de
cuenta) stay under `OperacionDePos` — the cashier looks up what we owe all day; the payment
itself needs no new gate because it is a gasto and `gastos` already has one.

**Cost of reversing.** Collapsing the two policies later is deleting one registration and
updating its call sites. Splitting them after a real divergence in who may adjust which ledger
means a discussion about which callers were which.

---

### 9 — **Pinned total lock order**: `turnos_caja → comprobantes_compra → lotes → stock/stock_lotes → proveedores → ledger INSERT`. `proveedores` is ALWAYS the last row lock.

**Context.** `explore.md` risk 5 proposes `turno → compra header → proveedor → ledger` and asks
for it to be pinned in design. Neither existing path locks `proveedores` today.

**Verified against the real call sites** (this is a correction to the explore's candidate, which
omits the stock locks that already sit between the header and the end of the transaction):

| Path | Locks today, in order |
|---|---|
| `EjecutarConfirmarAsync` | `comprobantes_compra` header (`UPDATE ... RETURNING`, :331) → `lotes` (:401) → `stock` / `stock_lotes` |
| `EjecutarAnulacionAsync` | `comprobantes_compra` header (:513) → `movimientos_stock` + `stock` / `stock_lotes` (:549-591) |
| `InsertarGastoAsync` | `turnos_caja` `FOR SHARE` (:140) → `comprobantes_compra` header `FOR SHARE` (:145) |
| Checkout (the stage-7 mirror) | `turnos_caja` (:773) → `stock` (:874-878) → **`clientes`** (:898) → ledger (:910) |

**Decision.** The total order above, whose operative form is an **invariant rather than a
list**: *`proveedores` is the last row lock any transaction takes, and the ledger `INSERT`
follows it immediately.* That is precisely how stage 7 is deadlock-free — `clientes` is the
checkout's last lock too — and it is what keeps the order valid for a write path nobody has
written yet. The two hazardous interleavings both resolve: two payments to the same proveedor
serialize on the proveedor row; a confirm of compra A and a payment of compra B for the same
proveedor share only that row, and neither holds anything the other needs after taking it.

**Cost of reversing.** Changing the position of the proveedor lock later means re-proving
deadlock-freedom against every write path that exists at that moment — cheap now with three
paths, expensive later. Three rendezvous race tests (confirm × pago, pago × pago,
anulación × pago) make the order observable rather than asserted.

---

## Modelo de datos propuesto

> **DB CHANGE GATE — this section is the contract.** It states the complete model at table
> level. Anything `sdd-apply` writes that is not here is a **scope violation that reopens the
> gate**. On implementation, **doc 10 is updated** with the new table and the new column,
> following the "Estado (Etapa N)" annotation convention already used there.

**Gate verdict proposed: ONE migration**, named `CuentaCorrienteDeProveedoresEtapa15`.
PostgreSQL 17. **One new enum type. One new table. TWO ALTERs over existing tables. ONE data
statement over existing rows.** This is the programme's first non-additive migration, and the
non-additive parts are §C and §D.

### A. New enum type — `tipo_movimiento_cc_proveedor`

```sql
CREATE TYPE tipo_movimiento_cc_proveedor AS ENUM ('apertura', 'compra', 'pago', 'ajuste');
```

Declaration order = C# member order (the type is generated by `npgsql.MapEnum<T>()` in
`WaysDbContextFactory.cs` **and** `DependencyInjection.cs`; never also via `HasPostgresEnum`).
Four values, one writer each (decision 4): `apertura` ← the migration in §C; `compra` ←
`ServicioDeCompras.ConfirmarAsync`; `pago` ← `ServicioDeGastos.InsertarGastoAsync`; `ajuste` ←
`ServicioDeCompras.AnularAsync` (contramovimiento) and the manual ajuste endpoint. **No
speculative value.**

### B. New table — `movimientos_cuenta_corriente_proveedor`

**Scoping category (doc 09): operativa** (`id_tenant` + `id_punto_venta`) — the same category as
`movimientos_cuenta_corriente`, `gastos` and `comprobantes_compra`. **One justified deviation**:
`id_punto_venta` is **nullable**, NULL only for `apertura` (decision 1), exactly the deviation
`auditoria.id_punto_venta` established in stage 14 and `movimientos_tesoreria.id_turno_caja`
before it. It is **not** catálogo: it carries no `id_empresa`.

It inherits **no** `EntidadBase` columns: an immutable movement has no `updated_at` and no soft
delete — the criterion `movimientos_stock`, `movimientos_cuenta_corriente` and `auditoria`
already apply. Consequently it does **not** inherit `EntidadTenant` either; it gets its own
tenant query filter cloned from `MovimientoStock`, and `id_tenant` is written explicitly
(stage 14 design decision 7 — `EstamparTenant()` would overwrite it with the session tenant).

```sql
movimientos_cuenta_corriente_proveedor (   -- [operativa — id_punto_venta NULL en el asiento de apertura]
    id_movimiento         integer     GENERATED BY DEFAULT AS IDENTITY,
    id_tenant             integer     NOT NULL,
    id_proveedor          integer     NOT NULL,
    fecha                 timestamptz NOT NULL,   -- IRelojDelSistema, sin DEFAULT now()
    id_punto_venta        integer     NULL,       -- NULL solo en tipo = 'apertura'
    id_empleado           integer     NULL,       -- NULL solo en tipo = 'apertura'
    tipo                  tipo_movimiento_cc_proveedor NOT NULL,
    id_comprobante_compra integer     NULL,       -- 'compra': la que originó la deuda
                                                  -- 'pago'/'ajuste': la compra imputada
    id_gasto              integer     NULL,       -- el gasto que materializó el pago
    importe               numeric(14,2) NOT NULL, -- + aumenta deuda, − la reduce
    saldo_resultante      numeric(14,2) NOT NULL,
    detalle               text        NULL,       -- obligatorio en el ajuste manual (regla de servicio)
    CONSTRAINT pk_movimientos_cuenta_corriente_proveedor PRIMARY KEY (id_movimiento)
);
```

**12 columns.** `numeric(14,2)` for both amounts (doc-10 principle 5). `fecha` has **no
`DEFAULT now()`**: `IRelojDelSistema` is the repo's single time source and a DB default would
silently defeat `RelojFijo` in tests — the stage-14 criterion, applied verbatim.

**Constraints:**

| Element | Name | Definition |
|---|---|---|
| PK | `pk_movimientos_cuenta_corriente_proveedor` | `(id_movimiento)` — `integer`, `GENERATED BY DEFAULT AS IDENTITY` (repo convention, EF's `IdentityByDefaultColumn`; `integer` mirrors `movimientos_cuenta_corriente`, not `auditoria`'s `bigint`: a proveedor ledger writes one row per compra/pago, orders of magnitude below the audit log) |
| FK 1 | `fk_movimientos_cuenta_corriente_proveedor_tenant` | `(id_tenant) → tenants(id_tenant)` RESTRICT |
| FK 2 | `fk_movimientos_cuenta_corriente_proveedor_proveedor` | `(id_proveedor, id_tenant) → proveedores(id_proveedor, id_tenant)` RESTRICT. The alternate key **already exists** (`ProveedorConfiguration.cs:151`) — no hidden ALTER |
| FK 3 | `fk_movimientos_cuenta_corriente_proveedor_punto_venta` | `(id_punto_venta, id_tenant) → puntos_venta(...)` RESTRICT, **MATCH SIMPLE** (the default): with `id_punto_venta` NULL the constraint is not checked; tenant integrity comes from FK 1, which is why both exist (the `fk_auditoria_punto_venta` precedent) |
| FK 4 | `fk_movimientos_cuenta_corriente_proveedor_empleado` | `(id_empleado) → usuarios(id_usuario)` RESTRICT — **simple, not composite**, for the documented reason at doc-10:563-567: a composite alternate key would force `id_tenant NOT NULL` on `usuarios` and break the platform-staff NULL sentinel. Same criterion as `id_empleado` everywhere and as `fk_auditoria_actor` |
| FK 5 | `fk_movimientos_cuenta_corriente_proveedor_comprobante_compra` | `(id_comprobante_compra, id_tenant) → comprobantes_compra(...)` RESTRICT, nullable. Alternate key **already exists** (`ComprobanteCompraConfiguration.cs:49`) |
| FK 6 | `fk_movimientos_cuenta_corriente_proveedor_gasto` | `(id_gasto, id_tenant) → gastos(...)` RESTRICT, nullable. **Requires the alternate key of §D** |
| CHECK | `ck_movimientos_cuenta_corriente_proveedor_apertura` | `(tipo = 'apertura' AND id_punto_venta IS NULL AND id_empleado IS NULL) OR (tipo <> 'apertura' AND id_punto_venta IS NOT NULL AND id_empleado IS NOT NULL)` — the nullability of the two provenance columns is **exactly** the opening asiento, never a hole for a human-written movement (decision 1) |
| RLS | `movimientos_cuenta_corriente_proveedor_tenant` | `migrationBuilder.HabilitarRlsDeTenant("movimientos_cuenta_corriente_proveedor")` → `ENABLE` + `FORCE ROW LEVEL SECURITY` + `USING/WITH CHECK (app_es_plataforma() OR id_tenant = app_tenant_actual())`. **Standard policy, no deviation** |

**No alternate key on this table — and that is a decision, not an omission.**
`ak_movimientos_cuenta_corriente_id_movimiento_id_tenant` exists on the client ledger solely to
support its **self-FK** `fk_movimientos_cuenta_corriente_actualizacion`, the reliquidación
marker (`MovimientoCuentaCorrienteConfiguration.cs:48-49` + `:124-132`, doc-10:672-678). With
reliquidación out of scope (decision 3) there is no self-FK and **no table references this
ledger**, so an alternate key would be a unique constraint with no referrer. Adding one later is
one additive migration over data that already satisfies it (`id_movimiento` is unique by
identity).

**Indexes — counted from the start, including every ForeignKeyIndexConvention support index
(the lesson of stage 14's gate amendment 1):**

| # | Index | Columns | Role |
|---|---|---|---|
| 1 | `ix_movimientos_cuenta_corriente_proveedor_tenant` | `(id_tenant)` | RLS predicate + support for FK 1. Mirrors `ix_movimientos_cuenta_corriente_tenant` |
| 2 | `ix_movimientos_cuenta_corriente_proveedor_proveedor_fecha` | `(id_proveedor, id_tenant, fecha)` | The estado-de-cuenta listing **and** support for FK 2 by leading-column prefix — exactly how `ix_movimientos_cuenta_corriente_cliente_fecha` covers `fk_..._cliente` (its own doc-comment: *"columnas líderes en el mismo orden"*) |
| 3 | `ix_movimientos_cuenta_corriente_proveedor_comprobante_compra` | `(id_comprobante_compra, id_tenant)` | The per-compra imputación aggregation (decision 7) **and** support for FK 5 |
| 4 | `ix_movimientos_cuenta_corriente_proveedor_punto_venta` | `(id_punto_venta, id_tenant)` | Support for FK 3 — declared explicitly with doc-10 naming instead of letting EF autogenerate `IX_..._id_punto_venta_id_tenant` |
| 5 | `ix_movimientos_cuenta_corriente_proveedor_empleado` | `(id_empleado)` | Support for FK 4, **simple** (a composite index led by `id_tenant` would NOT cover a simple FK — the exact trap that produced stage 14's amendment) |
| 6 | `ix_movimientos_cuenta_corriente_proveedor_gasto` | `(id_gasto, id_tenant)` | Support for FK 6 |

**FK-coverage audit (the binding count):** 6 FKs, 6 support indexes, **zero indexes added by the
convention that this contract did not name**. FK 1 → index 1; FK 2 → index 2 (prefix); FK 3 →
index 4; FK 4 → index 5; FK 5 → index 3; FK 6 → index 6. No index is led by `id_tenant` except
index 1, whose FK is `id_tenant` itself. **Total on this table: 6 indexes + 1 PK.**

### C. ALTER on `proveedores` + the backfill data statement

```sql
ALTER TABLE proveedores ADD COLUMN saldo numeric(14,2) NOT NULL DEFAULT 0;
```

Mirrors `clientes.saldo` (doc-10:169-170) including its meaning: *cache; el libro es
movimientos_cuenta_corriente_proveedor*. No CHECK (a negative saldo is a legitimate credit —
decision 5; `clientes.saldo` has none either). PG 11+ makes a non-volatile default a
metadata-only change: **no table rewrite**.

Note the deliberate asymmetry, identical to stage 7's: the **cache** sits on a catálogo row
(`proveedores` is `[catálogo]`, shareable across empresas via `id_empresa NULL`) while the
**book** is operativa (per PV). One saldo per proveedor per tenant, movements stamped with the PV
they came from.

**Backfill — tentative SQL, idempotent, inside the same migration:**

```sql
-- 1. Asiento de apertura: la fórmula EXACTA del spec saldo-de-proveedor que esta etapa retira.
WITH derivado AS (
    SELECT p.id_tenant,
           p.id_proveedor,
           COALESCE(c.total, 0) - COALESCE(g.total, 0) AS saldo
    FROM proveedores p
    LEFT JOIN (SELECT id_tenant, id_proveedor, SUM(total) AS total
               FROM comprobantes_compra
               WHERE estado = 'confirmada' AND deleted_at IS NULL
               GROUP BY id_tenant, id_proveedor) c
           ON c.id_tenant = p.id_tenant AND c.id_proveedor = p.id_proveedor
    LEFT JOIN (SELECT id_tenant, id_proveedor, SUM(importe) AS total
               FROM gastos
               WHERE categoria = 'proveedor' AND id_proveedor IS NOT NULL AND deleted_at IS NULL
               GROUP BY id_tenant, id_proveedor) g
           ON g.id_tenant = p.id_tenant AND g.id_proveedor = p.id_proveedor
    WHERE p.deleted_at IS NULL
)
INSERT INTO movimientos_cuenta_corriente_proveedor
    (id_tenant, id_proveedor, fecha, id_punto_venta, id_empleado, tipo,
     id_comprobante_compra, id_gasto, importe, saldo_resultante, detalle)
SELECT d.id_tenant, d.id_proveedor, now(), NULL, NULL, 'apertura',
       NULL, NULL, d.saldo, d.saldo,
       'Asiento de apertura (etapa 15): saldo derivado de compras confirmadas menos gastos '
       || 'de categoria proveedor al momento de la migracion.'
FROM derivado d
WHERE d.saldo <> 0
  AND NOT EXISTS (SELECT 1
                  FROM movimientos_cuenta_corriente_proveedor m
                  WHERE m.id_tenant = d.id_tenant AND m.id_proveedor = d.id_proveedor);

-- 2. El cache, derivado del asiento que acaba de escribirse — nunca recalculado aparte.
UPDATE proveedores p
   SET saldo = m.saldo_resultante
  FROM movimientos_cuenta_corriente_proveedor m
 WHERE m.id_tenant = p.id_tenant
   AND m.id_proveedor = p.id_proveedor
   AND m.tipo = 'apertura'
   AND p.saldo <> m.saldo_resultante;
```

Five properties this SQL is pinned on, each a place where a naive backfill would diverge:

- **Idempotent** via `NOT EXISTS` on the guard, the stage-12 seed precedent
  (`INSERT ... WHERE NOT EXISTS`); re-running the migration writes nothing. Statement 2 derives
  the cache **from the row written by statement 1**, so the two can never disagree.
- **Soft deletes respected on both sides** (`deleted_at IS NULL` on `comprobantes_compra`,
  `gastos` and `proveedores`). The retired formula runs through EF with its `BajaLogica` query
  filter; raw SQL does **not** inherit it, and forgetting this is the single most likely way the
  opening asiento silently disagrees with the read it must reproduce.
- **The `gastos` predicate is `categoria = 'proveedor' AND id_proveedor IS NOT NULL`** — exactly
  `ServicioDeSaldoDeProveedor.cs:39-43`. A proveedor-category gasto with no `id_proveedor` never
  entered any proveedor's saldo and must not enter the ledger either.
- **`estado = 'confirmada'` only** — borradores and anuladas excluded (`spec.md:27-31`).
- **Zero-saldo proveedores get no row** (decision 1), so the migration is proportional to real
  activity, not to the padrón.

**Ordering inside the migration is part of the contract**: `CREATE TYPE` → `CREATE TABLE` →
indexes/FKs → **backfill** → `ALTER TABLE proveedores` + statement 2 → `HabilitarRlsDeTenant`.
RLS goes **last** on purpose: the policy is `FORCE`d, the migration connection has no
`app_tenant_actual()` GUC set, and depending on RLS bypass to write the backfill would make the
migration's correctness rest on `ways_owner` being a superuser — a known carryover weakness, not
a foundation.

### D. ALTER on `gastos` — the alternate key, **verified absent**

```sql
ALTER TABLE gastos
    ADD CONSTRAINT ak_gastos_id_gasto_id_tenant UNIQUE (id_gasto, id_tenant);
```

**Verification (not assumed, in either direction):** `GastoConfiguration.cs` declares no
`HasAlternateKey` — the file's index/FK block (`:66-131`) is complete and has none. It is
therefore **required** by FK 6 and is part of this contract. It creates **one implicit unique
index**, so the migration's total new index count is **7** (6 on the new table + 1 on `gastos`).

Semantics worth stating: the constraint is **structurally unviolable** — `id_gasto` is already
unique through `pk_gastos`, so `(id_gasto, id_tenant)` cannot collide. It adds no new failure
mode, only the composite reference target every other operativa FK in this schema uses.

### E. Error backstops (`db-error-backstops` APPLIES)

| New constraint | Client-input reachable? | Backstop |
|---|---|---|
| `fk_..._proveedor` (FK 2) | **Yes** — `id_proveedor` is a route value on the manual ajuste endpoint | Service pre-check (`ResolverProveedorAsync` → 404, ADR-8, reused from `ServicioDeSaldoDeProveedor.cs:79-88`) **plus** the existing generic `23503` → `400 referencia_invalida` prefix mapping (`ManejadorDeErrores.cs:224`, matches any `fk_`). Integration test asserting the translated domain code, not the exception type |
| `fk_..._comprobante_compra` (FK 5) | **Yes** — the imputación target comes from the request (the existing `gastos` field) | Already pre-checked under `FOR SHARE` by `ExigirCompraLigableAsync` (`ServicioDeGastos.cs:187-197`), which is the TOCTOU guard, not a UX nicety; generic `fk_`/`23503` mapping as backstop. Race test: imputar a payment to a compra being annulled concurrently |
| `fk_..._punto_venta` (FK 3) | **Yes** on the manual ajuste (PV as provenance) | `ResolverPuntoVentaAsync` 404 pre-check (the `ServicioDeGastos.cs:28-31` ordering rule: an apocryphal PV must 404, never 409) + generic mapping |
| `fk_..._tenant` (FK 1) | No — session-derived | Generic `fk_`/`23503` mapping. **Exemption documented**; SQLSTATE-asserting test required anyway |
| `fk_..._empleado` (FK 4) | No — always `contexto.UsuarioId`, server-derived (doc-10:566-567); `usuarios` is soft-deleted so the referenced row is never physically removed | Same generic mapping. **Exemption documented** (the `fk_auditoria_actor` precedent, whose fail-closed test proved the path rather than assuming it) |
| `fk_..._gasto` (FK 6) | No — the id of the gasto inserted by the same transaction | Same generic mapping. **Exemption documented** |
| `ck_..._apertura` | No — only the migration writes `apertura`; the API rejects the value at the DTO boundary and the writer's per-tipo validator (decision 6) refuses it before SQL | **Exemption documented**, guarded by a unit test on the writer's validator plus one integration test asserting the CHECK's SQLSTATE (`23514`) from a raw insert, so the constraint is proven to exist rather than assumed |
| `ak_gastos_id_gasto_id_tenant` (§D) | **No** — structurally unviolable (`id_gasto` unique via `pk_gastos`) | **No `23505` mapping needed; exemption documented per the skill's gate table.** Same criterion every other `ak_*` in this repo already follows (none has a mapping) |
| **New unique index reachable from a write path** | **none** | See §F |

**No new `23505` family is introduced by this stage**: the only new unique constraint is the
structurally-unviolable §D alternate key, so no duplicate-race test family is required. Stated
explicitly so the absence reads as a decision. `ManejadorDeErrores.cs` is **not modified**.

### F. Deliberate non-decisions (gate-relevant)

- **No CHECK on `importe`** (no `<> 0`, no sign rule). Two reasons: the mirror table
  `movimientos_cuenta_corriente` has none, and an `importe` CHECK is an **open carryover the
  owner reserved** (*"micro-gate del CHECK de importe"*, carried since stage 12 and listed
  untouched by stage 14). This stage does not pre-empt it. A zero-importe manual ajuste is
  refused by the service with a 400, the same shape `ServicioDeGastos.cs:97-107` uses.
- **No alternate key on the new ledger** (§B) — no referrer exists.
- **No unique constraint of any kind on the ledger.** Two identical payments to the same
  supplier on the same day are legitimate history.
- **No `estado_pago` column and no new `estado_compra` value.** Payment status stays derived
  (decision 7) — a cached status would be a second truth about the same money.
- **No `limite_credito` / `credito_ilimitado` / `id_lista_precio` on `proveedores`.** Not in
  doc-10, not asked for, and both stage-7 mechanisms that key off them are out of scope.
- **No partial index for eligibility** (the stage-7 `ix_..._consumos_pendientes` analogue) —
  there is no reliquidación scan to bound.
- **No partitioning, retention or TTL.** The ledger writes one row per compra and per payment:
  the same volume class `movimientos_stock` already lives in without a policy. `fecha` is
  indexed and the table is append-only, so a future conversion stays mechanical (the stage-14
  decision-3 pattern, without re-deriving its tripwires).
- **No index on `tipo`** — a four-value discriminator that composes with index 2; adding it
  speculatively is a migration for an unmeasured gain (the stage-13 gate criterion).
- **No `EntidadBase` columns and no `id_empresa`** — immutable movement, operativa table.
- **No changes to `comprobantes_compra`, `gastos` (beyond §D), `movimientos_stock`,
  `movimientos_cuenta_corriente`, `clientes`, `auditoria`, `tipo_movimiento_cc`, or any
  existing enum.** In particular **no `ALTER TYPE ... ADD VALUE` anywhere in this stage** —
  nothing irreversible ships.
- **No database-level immutability** (`REVOKE UPDATE, DELETE`) — same honest residue stage 14
  recorded: theatre while `ways_owner` is a superuser.

### Model summary for the gate

| Object | Change |
|---|---|
| `tipo_movimiento_cc_proveedor` | **NEW TYPE** — enum, 4 values (`apertura, compra, pago, ajuste`), each with a writer |
| `movimientos_cuenta_corriente_proveedor` | **NEW TABLE** — 12 columns, 1 PK, **6 FKs**, 1 CHECK, **6 indexes**, RLS estándar, no AK, no `EntidadBase` |
| `proveedores` | **ALTER** — `+ saldo numeric(14,2) NOT NULL DEFAULT 0` (metadata-only, no rewrite) |
| `gastos` | **ALTER** — `+ ak_gastos_id_gasto_id_tenant UNIQUE (id_gasto, id_tenant)` (verified absent; +1 implicit index) |
| Data statements | **TWO** — the idempotent `apertura` backfill and the cache `UPDATE` derived from it. No existing row's meaning is rewritten |
| Existing enums / types | **NONE** — no `ALTER TYPE`, nothing irreversible |
| `ManejadorDeErrores.cs` | **NOT MODIFIED** — the generic `fk_`/`23503` mapping covers this stage (§E) |
| Migrations | **ONE** (`CuentaCorrienteDeProveedoresEtapa15`) |
| **New indexes, total** | **7** (6 on the new table + 1 implicit from §D) |

## Affected Areas

| Area | Impact | Description |
|---|---|---|
| `src/Ways.Domain/CuentaCorriente/` (proveedor side) | New | `TipoMovimientoCcProveedor` enum, `MovimientoCuentaCorrienteProveedor` (immutable, no `EntidadBase`), `CalculadorDeEstadoDeCuentaDeProveedor` (running balance, pure) and the per-compra imputación rule — all unit-testable without a DB (`PoliticaDeRoles` pattern) |
| `src/Ways.Infrastructure/Migrations/` | New | `CuentaCorrienteDeProveedoresEtapa15` — the single migration of the gate section |
| `src/Ways.Infrastructure/Persistencia/` | Modified | `MovimientoCuentaCorrienteProveedorConfiguration`, `GastoConfiguration` (+ alternate key), `ProveedorConfiguration` (+ `saldo`), `DbSet`, the cloned tenant filter, `MapEnum` in **both** option builders (`WaysDbContextFactory.cs`, `DependencyInjection.cs`) |
| `src/Ways.Application/CuentaCorriente/EscriturasDeCuentaCorrienteProveedor.cs` | New | The ONE `UPDATE proveedores ... RETURNING` + the ONE ledger `INSERT ... RETURNING` + the per-tipo shape validator (decision 6) |
| `src/Ways.Application/CuentaCorriente/ServicioDeCuentaCorrienteDeProveedor.cs` | New | Manual ajuste + estado de cuenta read |
| `src/Ways.Application/Compras/ServicioDeCompras.cs` | Modified | `compra` movement in `EjecutarConfirmarAsync`; reversing `ajuste` in `EjecutarAnulacionAsync` (next to the stage-14 audit call at `:525-538`); proveedor lock as the last lock |
| `src/Ways.Application/Gastos/ServicioDeGastos.cs` | Modified | `pago` movement inside `InsertarGastoAsync`'s existing transaction (`:134-174`), after the turno and compra locks |
| `src/Ways.Application/Compras/ServicioDeSaldoDeProveedor.cs` | Modified | Re-sourced from the ledger (`proveedores.saldo` + per-compra imputación aggregation). **Response DTOs unchanged** (`dto-contract-honesty`) |
| `src/Ways.Api/Endpoints/ProveedoresEndpoints.cs` | Modified | `GET /{id}/saldo` stays top-level under `OperacionDePos` (the stage-8 decision to preserve) |
| `src/Ways.Api/Endpoints/CuentaCorrienteDeProveedorEndpoints.cs` | New | Group `/api/proveedores/{idProveedor:int}/cuenta-corriente`: `GET /` (estado de cuenta) under `OperacionDePos`, `POST /ajustes` under the new policy — mirroring `CuentaCorrienteEndpoints.cs:14-57` |
| `src/Ways.Api/Seguridad/Politicas.cs` | Modified | `SupervisionDeCuentaDeProveedor` (Supervisor + Admin, decision 8) |
| `src/Ways.Web/src/paginas/CuentaCorrienteDeProveedor.tsx` | New | Estado de cuenta screen + ajuste modal, `react-async-state` compliant, `web-descriptor-tests` covered; entry point from `Proveedores.tsx` and `Compras.tsx` |
| `src/Ways.Web/src/componentes/ResumenSaldoDeProveedor.tsx` | Modified | Same shape, plus the "saldo a favor" state (decision 5) and the link to the new screen |
| `docs/10-modelo-de-datos.md` | Modified | The new table (a §8-adjacent subsection), `proveedores.saldo` in §2, and the "Estado (Etapa 15)" annotations — including the retirement of the "saldo derivado, deliberadamente simple" note at doc-10:832-834 |
| `docs/11-programa-post-paridad.md` | Modified | Etapa 15 status block with the three resolved open decisions (orchestrator, outside the phase) |
| `src/Ways.Api/Seguridad/ManejadorDeErrores.cs` | **Unmodified** | Gate §E |

## Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| **The backfill disagrees with the read it replaces** (soft deletes, unlinked gastos, the `categoria` predicate) — a wrong opening saldo is a wrong saldo forever | **High if unmanaged** | The backfill mirrors `ServicioDeSaldoDeProveedor.cs:39-43` clause by clause (gate §C), and slice 1 asserts equality **per proveedor** over a fixture mixing borrador/confirmada/anulada, linked/unlinked, and soft-deleted rows on both sides |
| **Deadlock between confirm, anulación and payment on the same proveedor** | Med-High | Pinned total order with `proveedores` as the LAST row lock (decision 9), verified against all three existing paths + the checkout precedent; three rendezvous race tests |
| **The ledger diverging from the cache** (`saldo ≠ Σ importes`) | Med-High | ONE write authority (decision 6): both statements in the same class, both in the caller's transaction, `saldo_resultante` taken from the `RETURNING` of the update that just happened — never recomputed |
| **A `gasto` that should not move the account moving it** (categoria ≠ proveedor, or `id_proveedor` NULL) | Med | The writer applies the retired formula's exact predicate; a spec scenario per direction |
| **Anulación of a paid compra leaving a state nobody expects** (negative saldo) | Med | Decided and surfaced as "saldo a favor" (decision 5), with a scenario; never clamped to zero |
| **Reviewer overload** (schema + two guarded transactions + read model + screen) | Med-High | Six stacked-to-main slices, `judgment-day` before every PR, pre-authorized split points |
| **`ALTER TABLE gastos` colliding with a concurrent write** at deploy time | Low | `ADD CONSTRAINT UNIQUE` takes a brief ACCESS EXCLUSIVE lock; deploys already run migrations offline, same as every previous stage |
| **RLS blocking the backfill** (FORCE policy, no GUC on the migration connection) | Low | Ordering pinned in gate §C: the data statement runs **before** `HabilitarRlsDeTenant`, so correctness does not rest on the `ways_owner`-superuser carryover |
| **Scope creep into retenciones, notas de crédito or FIFO imputación** | Med | All three refused in writing with reopen conditions (decisions 4 and 7) |
| **Raw-ADO `DateTimeOffset` written without UTC normalization** (a real 500 in PR #129) | Low-Med | The writer copies `EscriturasDeCuentaCorriente`'s `AgregarParametro` verbatim; a test at a non-zero offset, since `RelojFijo` in `Z` cannot see this class of bug (stage-14 verify W2) |

## Rollback Plan

**The first non-additive stage of the programme is nevertheless fully reversible**, and that is
worth stating precisely because it is not obvious.

**Per slice.** Slices 2-6 are additive code over an unchanged schema: reverting one removes a
write path or a read surface and leaves the table intact and consistent (the ledger is
append-only, so nothing needs repair).

**Slice 1 (the schema).** Rollback is `DROP TABLE movimientos_cuenta_corriente_proveedor` →
`ALTER TABLE proveedores DROP COLUMN saldo` → `ALTER TABLE gastos DROP CONSTRAINT
ak_gastos_id_gasto_id_tenant` → `DROP TYPE tipo_movimiento_cc_proveedor`. In that order there is
**no dependent object**: nothing references the ledger (no alternate key, no self-FK, gate §B),
and no other column uses the type.

**Why the backfill destroys nothing.** The data statement writes **new** rows and one **new**
column; it does not rewrite a single existing row. `comprobantes_compra` and `gastos` are
untouched, so the retired formula still computes exactly the same number from exactly the same
data — the derived read can be restored bit-for-bit by reverting the code. This is the whole
reason decision 1 refused the synthetic replay: a replay would have been the version of this
migration that *could not* be rolled back cleanly.

**No irreversible database artifact of any kind**: no `ALTER TYPE ... ADD VALUE`, no dropped
column, no rewritten row, no destroyed history.

**Whole stage.** Revert the code, run the four statements above, restore the doc-10 wording.

## Dependencies

- **Etapa 7** (archived) — the design being mirrored: `EscriturasDeCuentaCorriente`, the
  `saldo`-as-cache discipline, the pinned lock order, the contramovimiento pattern, the estado de
  cuenta screen shape. No runtime dependency, a design one.
- **Etapa 8** (archived) — `comprobantes_compra` / `ServicioDeCompras` (confirmar/anular with
  their `UPDATE ... RETURNING` authority), `gastos` with `id_comprobante_compra` and
  `ExigirCompraLigableAsync`, `ServicioDeSaldoDeProveedor`, the `/saldo` endpoint.
- **Etapa 6** — `turnos_caja`, `ExigirTurnoAbiertoBajoLockAsync`, `CalculadorDeArqueo`'s egress
  term (consumed unchanged: no new arqueo term, no new formula).
- **Etapa 1** — `proveedores` with its alternate key (`ProveedorConfiguration.cs:151`),
  `PoliticaDeRoles`, the policy composition pattern.
- **Etapa 14** — the `id_punto_venta`-nullable-on-an-operativa-table precedent, the
  cloned-tenant-filter pattern for a non-`EntidadTenant` entity, and the gate discipline this
  proposal's model section follows.
- `IRelojDelSistema`, `IContextoDeUsuario`, `EstrategiaSinReintento`, `ManejadorDeErrores`,
  `HabilitarRlsDeTenant` — all existing, **no new wiring**.
- Skills: `db-error-backstops` (per constraint), `react-async-state` + `web-descriptor-tests`
  (the web slice), `dto-contract-honesty` (the re-sourced `/saldo`), `mutation-proof-tests`,
  `work-unit-commits`, `judgment-day` before every PR.
- No new NuGet package, no new web dependency, no scheduler, no queue.

## Success Criteria

- [ ] Exactly **one** migration ships, named `CuentaCorrienteDeProveedoresEtapa15`; the only DDL
      and the only data statements are the ones in the gate section;
      `dotnet ef migrations has-pending-model-changes` is clean afterwards.
- [ ] The migration creates **exactly 7 new indexes** (6 named on the new table + 1 implicit from
      the `gastos` alternate key) and **no** unnamed EF-generated FK support index.
- [ ] **For every proveedor, `proveedores.saldo` after the migration equals what
      `ServicioDeSaldoDeProveedor` returned before it** — proven over a fixture mixing borrador,
      confirmada, anulada, linked, unlinked, `id_proveedor IS NULL` and soft-deleted rows.
- [ ] Re-running the migration writes **no** additional `apertura` row and changes **no** saldo
      (idempotency proven, not assumed).
- [ ] A proveedor with no history gets **no** `apertura` row and keeps `saldo = 0`.
- [ ] RLS proven: a tenant reading with another tenant's GUC sees **zero** ledger rows; an
      INSERT with a foreign `id_tenant` is refused (`42501`), asserted by SQLSTATE.
- [ ] `proveedores.saldo` always equals the sum of that proveedor's movement importes — proven
      over a scenario mixing apertura, compra, pago, ajuste manual and anulación.
- [ ] Confirming a compra writes **exactly one** `compra` movement; anulándola writes **exactly
      one** reversing `ajuste`; the linked gastos are **not** reversed and the informational
      `gastosLigados` count is unchanged.
- [ ] A gasto with `categoria = proveedor` and an `id_proveedor` writes **exactly one** `pago`
      movement; a gasto without one (other categoria, or `id_proveedor` NULL) writes **none**.
- [ ] The cash of a proveedor payment still appears in the turno's arqueo **with no new
      derivation term**, and a payment with no open turno still returns `409 turno_no_abierto`.
- [ ] Per-compra payment status read from the ledger reproduces the retired spec's outcomes: a
      fully paid compra is `pagada`, a partially imputed one `parcial`, an unimputed payment
      reduces the total saldo without settling any compra.
- [ ] Anulando a fully-paid compra leaves a **negative** saldo surfaced as "saldo a favor".
- [ ] The three rendezvous races (confirm × pago, pago × pago, anulación × pago on the same
      proveedor) all commit consistently, with **no deadlock** and no lost saldo.
- [ ] A failed ledger write leaves saldo, ledger and the business operation untouched
      (fault-point test on each of the three write paths).
- [ ] Ajuste manual requires a `detalle`, returns **403 for a Vendedor**, and 200 for a
      Supervisor; the estado de cuenta read returns 200 for a Vendedor.
- [ ] `GET /api/proveedores/{id}/saldo` keeps its response shape byte-compatible while sourcing
      from the ledger; cross-tenant is still 404.
- [ ] The `apertura` CHECK is proven by SQLSTATE (`23514`) from a raw insert, and every FK by its
      translated `400 referencia_invalida`.
- [ ] No endpoint accepts a saldo, a `saldo_resultante` or a computed delta from the client.
- [ ] doc 10 carries the new table, `proveedores.saldo`, and the retirement of its
      "saldo derivado, deliberadamente simple" note.
- [ ] Domain / Application / Integration / vitest suites green; descriptor tests for the new
      screen.

## Plan de slices (tentative — `sdd-tasks` owns the final breakdown)

Stacked-to-main, one `judgment-day` round per slice (`protocolo-pr-solo-dev`).

| # | Branch | Content | ~lines |
|---|---|---|---|
| 1 | `feat/stage15-slice1-ledger-schema` | The migration (type, table, 6 FKs, 6 indexes, CHECK, both ALTERs, both data statements, RLS last) + entity + EF config + `MapEnum` in both builders + cloned tenant filter + RLS/SQLSTATE tests + **backfill fidelity & idempotency tests** + doc 10 | ~450 |
| 2 | `feat/stage15-slice2-escrituras-y-deuda` | `EscriturasDeCuentaCorrienteProveedor` (both statements + per-tipo validator + UTC normalization) + the `compra` movement in `ConfirmarAsync` + the reversing `ajuste` in `AnularAsync` + the pinned lock order + confirm/anulación race tests | ~400 |
| 3 | `feat/stage15-slice3-pago-por-gasto` | The `pago` movement inside `InsertarGastoAsync` + imputación + the predicate scenarios + pago × pago and anulación × pago races + the arqueo no-regression test | ~330 |
| 4 | `feat/stage15-slice4-estado-de-cuenta` | `GET /api/proveedores/{id}/cuenta-corriente` (running balance, desde/hasta, histórico, empty state) + `ServicioDeSaldoDeProveedor` re-sourced from the ledger with its DTOs unchanged + authorization tests | ~400 |
| 5 | `feat/stage15-slice5-ajuste-manual` | `SupervisionDeCuentaDeProveedor` policy + `POST /ajustes` (required detalle, signed importe) + 403/200 matrix | ~270 |
| 6 | `feat/stage15-slice6-web` | `CuentaCorrienteDeProveedor.tsx` (list, running balance, filters, ajuste modal) + `ResumenSaldoDeProveedor.tsx` saldo-a-favor state + entry points + descriptor tests | ~380 |

Merge order `1 → 2 → 3 → 4 → 5 → 6`. Slice 1 blocks everything (it owns the only migration);
3 depends on 2 only for the writer class; 4 and 5 depend on 1 and 2; 6 depends on 4 and 5.

**Pre-approved degradation** (stage-12 decision-11 / stage-14 pattern), in priority order:

1. **If slice 1 overflows** — split at the DDL/proof boundary: `1a` (migration + entity + EF
   config + RLS tests) and `1b` (backfill fidelity/idempotency tests + doc 10). The split keeps
   **one** migration, which is the invariant that must not be degraded.
2. **If slice 2 overflows** — split at the write-path boundary: `2a` (the writer class + the
   `compra` movement on confirm) and `2b` (the anulación contramovimiento + its races).
3. **If slice 6 overflows** — ship the list, the running balance and the filters, and drop the
   ajuste modal (the endpoint still serves the operation). A documented reduction, never silent.
4. **Never degraded**: the backfill fidelity proof, the single-write-authority containment, and
   the anulación contramovimiento. A ledger that starts wrong or diverges on the first anulación
   is worse than no ledger, so those are split, never trimmed.

**Review Workload Forecast (preliminary — `sdd-tasks` produces the binding one)**

- Estimated total: **~2 230 lines** across 6 slices. Calibrated against the stage-13 lesson that
  **test depth, not production code, is what inflates a slice**: every slice here carries either
  race tests, SQLSTATE assertions or a fidelity fixture, so the estimates are deliberately
  higher than a naive production-code count would suggest.
- `Decision needed before apply: No` — `auto-chain` + `stacked-to-main` already resolved in
  `state.yaml`.
- `Chained PRs recommended: Yes` — `chain_strategy: stacked-to-main`.
- `400-line budget risk: Medium` — slices 1, 2 and 4 sit at or above the cap; split points are
  pre-authorized above for 1 and 2, and slice 4 splits cleanly at the read/re-sourcing boundary
  (`4a` estado de cuenta, `4b` the `/saldo` re-sourcing) if the forecast demands it.
- `size:exception` anticipated: **No**.

## Refutaciones y refinamientos a las Orchestrator Decisions

None of the five is refuted outright — the code supports all five. Three need a correction or a
refinement the orchestrator must arbitrate, and two model claims inherited from the explore's
tentative table **are** refuted with evidence.

| # | Orchestrator Decision | Verdict |
|---|---|---|
| 1 | Opening `ajuste` per proveedor, exact spec formula, no replay | **Ratified, refined.** The opening movement cannot be a plain `ajuste`: it has **no actor and no punto de venta**, and forcing NOT NULL values there would fabricate provenance — the very sin this decision refuses. It ships as its own `apertura` tipo with both columns NULL under a CHECK. Also refined: a proveedor with a zero derived saldo gets **no row** |
| 2 | Payment stays `gastos`; verify/add the `(Id, IdTenant)` alternate key on `gastos` | **Ratified, with the verification resolved: the alternate key does NOT exist.** `GastoConfiguration.cs` has no `HasAlternateKey`, so gate §D adds `ALTER TABLE gastos ADD CONSTRAINT ak_gastos_id_gasto_id_tenant UNIQUE` — a **second ALTER over an existing table**, now inside the contract instead of assumed |
| 3 | Reliquidación out of scope, no speculative enum values | **Ratified** with two extra pieces of evidence: `proveedores` has no `id_lista_precio` to re-price against, and `tipo_movimiento_cc` is therefore **not** reused (one of its four values would be permanently dead) |
| 4 | Native enum over `text` + CHECK; launch set `compra \| pago \| ajuste`; retenciones/NC deferred | **Enum choice ratified** with the argument the decision asked for: `auditoria.accion` is an **open catalog growing every stage**, this is a **closed 4-value discriminator with one writer each** that the arithmetic and the imputación aggregation key off, and the mirror table's `tipo` is already a native enum. **Value set refined to four**: `apertura` is not speculative — its writer is this stage's migration |
| 5 | Contramovimiento on anulación in scope; gastos still not reversed | **Ratified**, with the tipo pinned (`ajuste`, mirroring `ServicioDeVentas.cs:657-662`) and one consequence made explicit: a paid-then-annulled compra leaves a **negative saldo (saldo a favor)**, surfaced rather than clamped |

**Refuted (explore's tentative model, §3 — not an Orchestrator Decision):**

1. **The alternate key `(id_movimiento, id_tenant)` on the new ledger is NOT needed.** The
   explore mirrors it 1:1 from `MovimientoCuentaCorrienteConfiguration.cs:48-49`, but that
   alternate key exists **only** to support the self-FK
   `fk_movimientos_cuenta_corriente_actualizacion` (same file, `:124-132`; doc-10:672-678) — the
   reliquidación marker. With reliquidación out of scope there is no self-FK and no table
   references this ledger, so the alternate key would be a unique constraint with no referrer.
   **Dropped**, with the cheap reversal recorded.
2. **The explore's FK/index count ("5 FKs / 5 support indexes … row 5 or row 6 swaps") is
   wrong.** `id_comprobante_compra` and `id_gasto` are **both** needed, not alternatives: the
   first carries the debt's origin *and* the imputación target (decision 7), the second the
   payment's provenance. The real shape is **6 FKs / 6 support indexes**, and one of them (the
   proveedor listing index) covers its FK by leading-column prefix. Total new indexes in the
   migration: **7**, counting the implicit one from §D.
3. **The candidate lock order in explore risk 5 is incomplete.** `turno → compra header →
   proveedor → ledger` omits the `lotes` and `stock`/`stock_lotes` locks that
   `EjecutarConfirmarAsync` and `EjecutarAnulacionAsync` already take between the header and the
   end of their transactions. The pinned order is
   `turnos_caja → comprobantes_compra → lotes → stock/stock_lotes → proveedores → ledger INSERT`,
   stated as the invariant *"`proveedores` is the last row lock"* — which is exactly how the
   checkout is already deadlock-free with `clientes` (`ServicioDeVentas.cs:773, 874-878, 898`).

## Proposal question round

Execution mode is `automatic-autonomous`, so these were resolved rather than asked. Each records
the assumption taken so a correction is cheap. **None blocks spec/design.**

1. **Is a supplier balance really an account, or just a report?** Assumed **an account**
   (doc-11:235 asks for pagos parciales, imputación and historial). If the owner only wants a
   better report, the whole stage collapses into a query and should not ship a table.
2. **Should a payment be imputable to more than one compra at once?** Assumed **no**
   (decision 7): one payment, one optional imputación, exactly like the gasto link today. The
   reopen condition is a real supplier invoice paid in one transfer covering several facturas.
3. **Is "saldo a favor" against a supplier an acceptable state?** Assumed **yes** (decision 5) —
   it is what anulando a paid compra means, and stage 7 already chose it for overpayments.
4. **Does the cashier see what we owe a supplier, or only a supervisor?** Assumed **the cashier
   reads, the supervisor adjusts** (decision 8), mirroring the client side exactly.
5. **Do retenciones matter to this business today?** Assumed **not yet** (decision 4). This is
   the assumption most likely to be wrong for an Argentine supplier circuit, and it is also the
   cheapest to fix later (one enum value + one write path) — which is why nothing speculative
   ships now.
6. **Should soft-deleting a proveedor with a non-zero saldo be blocked?** Assumed **no change**
   (Out of Scope): today's ABM allows it, the ledger is append-only and nothing corrupts. Flagged
   as a genuine product question rather than silently tightened.
7. **Is it acceptable that the ledger's history starts at the cutover** (one opening row instead
   of per-movement provenance)? Assumed **yes** (decision 1) — the provenance never existed, and
   fabricating it is the one thing an auditable ledger must not do.
