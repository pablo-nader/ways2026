# Proposal: Stage 8 — Comprobantes de compra, transferencias e inventario

## Intent

Implement Etapa 8 of `docs/10-modelo-de-datos.md` (stage table, doc-10:639) — "Comprobantes de
compra + transferencias de stock", the stage doc 10 marks as **superar al legacy**. It is the
**last stage of doc 10**: archiving it closes the legacy-parity program and the rewrite stops
having a "pending model" at all.

Three gaps close here, and all three are *inbound* — every stage so far only knew how to take
goods and money **out**:

1. **Goods never enter the system.** Legacy C3 ("Alta de compra con detalle") is a whole screen
   that accumulates lines in `$_SESSION['compra']` and **never persists** — doc-01:203-208
   is explicit: *"⚠ Nunca persiste... Feature incompleto."* Today a purchase is a flat `gasto`
   with `tipo < 90` encoding the proveedor id (doc-01:210-219): the money is recorded, the
   **merchandise is not**. Stock only grows through the admin-only manual `ajuste`
   (`ServicioDeStock.AjustarAsync`), which is a correction tool being used as a receiving desk.
2. **`motivo_stock` has three dead values.** `Compra`, `Transferencia` and `Inventario` were
   reserved in stage 5 with no writer (`MotivoStock.cs:5-8`; `openspec/specs/stock/spec.md:8-10`),
   `movimientos_stock.id_punto_venta_destino` was created and never written
   (`MovimientoStock.cs:39-43`, FK already wired at `MovimientoStockConfiguration.cs:96-103`), and
   two FK columns were deliberately deferred to *this* stage because `comprobantes_compra` did not
   exist yet (doc-10:426-434 for `gastos`, doc-10:457-465 for `movimientos_stock`). The debt was
   scheduled, not forgotten — stage 8 is when it is paid.
3. **Moving stock between locales is manual folklore.** doc-10:472-474, verbatim: *"Transferencia
   entre locales: dos movimientos espejados — feature nueva que el legacy resolvía 'a mano y que
   Dios ayude'."* The legacy's only stock surface is E9, a **read-only** dashboard
   (doc-01:341-347).

Stage 8 makes the inventory *honest*: every unit that enters, moves or is recounted enters
through the same append-only ledger that already governs every unit that leaves. The legacy's D3
restore bug (doc-01:241 — restoring a ticket **added** stock instead of subtracting it) stays dead
by construction, because nothing in this stage edits a movement or a cache directly.

## Scope

### In Scope

- **`comprobantes_compra` + `items_comprobante_compra`** exactly per doc-10:370-398: supplier
  invoice identity (`numero_externo`, citext, **no correlativo propio** — doc-10:374-375), the
  dedupe UNIQUE (doc-10:384), `costo_unitario numeric(14,4)`, `actualiza_costo`, `precio_sugerido`,
  and the `estado_compra` enum `borrador | confirmada | anulada` (doc-10:382).
- **The three-state lifecycle** (decision 2): `borrador` is incrementally editable (the only
  mutable document in the system — it has no ledger effect yet); **`confirmar` is ONE transaction**
  that writes the entry `movimientos_stock` rows (`motivo = compra`, `id_comprobante_compra`
  populated), overwrites `articulos.costo_nominal` where `actualiza_costo`, and upserts the stock
  cache; `anular` reverses with contramovimientos (doc-10:401-404).
- **Price update as a separate, explicit action** (decision 3): doc-10:403 says the confirm
  *"ofrece"* updating sale prices. `precio_sugerido` is computed and stored per item through the
  **existing** pure `SugeridorDePrecio` (`SugeridorDePrecio.cs:39-55`, grupo-then-proveedor margin
  precedence); applying it goes through the **existing** `ServicioDePrecios.AbrirNuevoPrecioAsync`
  so price history is preserved. **Never silent, never inside the confirm transaction.**
- **Transferencias entre locales** (decision 5): one atomic transaction, **two mirrored
  `movimientos_stock` rows** (origin `−cantidad`, destino `+cantidad`, `motivo = transferencia`,
  `id_punto_venta_destino` populated), both stock caches upserted in that same transaction. No
  in-transit state, no transfer document.
- **Minimal conteo de inventario** (decision 1 — **the one autonomous scope call**): a per-articulo
  count endpoint that writes ONE movement with `motivo = inventario` whose `cantidad` is the
  **server-derived** delta `contada − actual` under the stock row lock, reusing `AjustarAsync`'s
  transaction shape. Distinct from `ajuste` for traceability. Closes the enum debt instead of
  spawning a phantom stage 9.
- **`gastos.id_comprobante_compra`** (column + FK, decision 8): *"la compra registra la mercadería;
  el gasto registra la plata"* (doc-10:406-407). Paying a compra is an ordinary `gasto` of
  `categoria = proveedor` under the **existing, untouched** turno gate.
- **Saldo de proveedor, derived only** (decision 6): `Σ compras confirmadas − Σ gastos de ese
  proveedor`. No new table, no saldo cache, no ledger (doc-10:408-409: *"cuenta corriente de
  proveedores embrionaria sin tabla extra"*).
- **Compra-clase `tipos_comprobante`** (decision 7): platform-side seeded rows, dual-path
  (seed-list + idempotent migration insert with the stage-7 `AND EXISTS` fresh-DB guard).
- **Web**: compras (list + borrador editor + confirmar + anular), transferencias, conteo de
  inventario, saldo de proveedor — `react-async-state` compliant with `web-descriptor-tests`
  coverage.

### Out of Scope

- **Full-count inventory workflow** (snapshot, freeze, session, variance report). Only the
  per-articulo conteo ships (decision 1).
- **A real proveedores current account** — no `movimientos_cuenta_corriente_proveedor`, no saldo
  cache, no imputación, no aging (decision 6). Deliberately **not** following stage 7's
  richer-ledger precedent; doc-10:409 is explicit.
- **Órdenes de compra / pedidos a proveedor** — nothing upstream of the invoice.
- **Recepción parcial** — a confirm receives the whole comprobante or nothing.
- **Notas de crédito de proveedor / devoluciones a proveedor** — anulación is the only reversal.
- **IVA crédito fiscal reporting, libro IVA compras, FE/AFIP integration** — compra tipos are
  `es_fiscal = false` (we never emit them; decision 7).
- **Automatic sale-price updates on confirm** (decision 3) and **per-proveedor cost history** —
  `costo_nominal` is a single current value, overwritten.
- **Bulk/CSV import of compras**, and **multi-PV compras** (one comprobante lands in exactly one
  punto de venta, doc-10:378).
- **In-transit / two-step transfers with acceptance at destination** (decision 5).
- **Reworking `gastos`, `turnos_caja` or the arqueo derivation** — the arqueo stays byte-untouched
  (decision 4); that is a *verify criterion*, not an assumption.
- **`ServicioDeArticulos` `articulos_empresas` replace-set concurrency gap** — still open, carried
  since stage 4, unrelated.

## Capabilities

### New Capabilities

- `comprobantes-compra`: schema at rest, `numero_externo` identity + dedupe, the
  `borrador → confirmada → anulada` lifecycle, the confirm transaction (stock entry +
  `costo_nominal` overwrite + cache), `precio_sugerido` as a suggestion never auto-applied,
  anulación by contramovimientos with the insufficient-stock refusal, authorization.
- `transferencias-de-stock`: the atomic two-mirrored-movement shape, the sufficiency rule at
  origin, `origen ≠ destino`, cross-tenant refusal, multi-item transfers, no in-transit state.
- `conteo-de-inventario`: the counted quantity as input (never a delta), the server-derived delta
  under the row lock, required `observaciones`, the zero-difference no-op, and its distinction from
  `ajuste`.
- `saldo-de-proveedor`: the derived read (compras confirmadas vs. gastos), per-compra payment
  status, explicit non-invariant status (approximation, not a ledger), empty state, scoping.

### Modified Capabilities

- `stock`: `compra`, `transferencia` and `inventario` gain write paths — the Purpose sentence
  *"`compra`, `transferencia`, `inventario` are reserved enum values with no write path yet"*
  (spec:8-10) is **removed**; `movimientos_stock` gains `id_comprobante_compra`; the sum-invariant
  requirement (spec:86-96) is extended to the three new motivos **and** to the two-sided transfer
  (each row's `id_punto_venta` is the location it affects).
- `gastos`: gains the optional `id_comprobante_compra` link and the rule pairing it with
  `categoria = proveedor`; the open-turno gate and `importe > 0` rule are unchanged.
- `arqueo-de-cierre`: clarification only — a proveedor gasto linked to a compra is an ordinary
  `SUM(gastos.importe on that medio)` term (spec:45-52). **No new term, no new formula.**
- `proveedores`: a proveedor referenced by a `comprobante_compra` cannot be removed (FK Restrict),
  and gains the derived saldo read entry point.
- `operacion-de-pos`: the compra/transferencia/inventario write paths stack
  `Politicas.GestionDeCatalogo` over `Politicas.OperacionDePos` (decision 11); the compra list and
  the proveedor saldo read stay on `OperacionDePos`.

## Approach

1. **The confirm is the centerpiece, and it is a transaction shape we already own.**
   `ServicioDeStock.AjustarAsync` (`ServicioDeStock.cs:32-70`) is the precedent: pre-checks
   outside, `EstrategiaSinReintento`, one explicit transaction, raw ledger `INSERT`, then the
   atomic `INSERT ... ON CONFLICT DO UPDATE ... RETURNING` cache upsert (`:97-119`) whose own row
   lock provides serialization. Every stage-8 stock writer is that shape, N times, plus the
   `costo_nominal` overwrite. **No new concurrency primitive is invented.**
2. **Totals and margins ship pure-Domain first.** A DB-free calculator for the compra item/header
   arithmetic (cantidad = `unidades + bultos × unidades_por_bulto` per doc-01:206, descuento, IVA
   per alícuota, totals at `numeric(14,4)` cost precision) and the already-existing
   `SugeridorDePrecio`, exhaustively unit-tested, mirroring `CalculadorDeArqueo` /
   `ValidadorDePagos` / `ReliquidadorDeConsumos`. Persistence is a thin transaction around them.
3. **Sum-invariant discipline is non-negotiable** (decision 9). Every new writer goes through
   append-only ledger + same-transaction cache upsert. **No endpoint accepts a delta**: the compra
   supplies quantities, the transfer supplies a quantity, the conteo supplies the *counted* total
   and the server derives the delta. The D3/D7 bug classes stay unrepresentable.
4. **Greenfield means the spec carries all the weight.** Unlike stages 5–7, there is **no legacy
   behaviour to match** — C3 never persisted. Every rule here is a decision, not a port. That is
   why this proposal pins 11 of them explicitly and why the spec phase must convert each into
   scenarios in both directions.
5. **Guarded paths are touched surgically.** `ServicioDeStock`, `ServicioDeVentas` and the arqueo
   derivation are the project's most-tested code. Stage 8 **adds** writers next to them; it must not
   edit the sale transaction at all. The arqueo staying byte-identical is proven by test, not
   asserted.
6. **DB CHANGE GATE (CLAUDE.md), exercised in autonomous mode.** The largest schema surface since
   stage 5: two new tables, one new Postgres enum, two deferred FK columns landing at last, and
   global catalog rows. The model summary and evaluation are recorded below and in `state.yaml`.

## Affected Areas

| Area | Impact | Description |
|---|---|---|
| `src/Ways.Domain/Compras` (new) | New | `ComprobanteCompra`, `ItemComprobanteCompra`, `EstadoCompra`, pure `CalculadorDeCompra` (cantidad/descuento/IVA/totales) |
| `src/Ways.Domain/Stock/MotivoStock.cs` | Modified | The "valores reservados sin escritor" doc-comment (`:5-8`) loses its reservation clause |
| `src/Ways.Domain/Stock/MovimientoStock.cs` | Modified | `IdComprobanteCompra` added (`:16-18` deferral note removed); `IdPuntoVentaDestino` doc (`:39-43`) stops saying "never written" |
| `src/Ways.Domain/Gastos/Gasto.cs` | Modified | `IdComprobanteCompra` added (`:11-15` deferral note removed) |
| `src/Ways.Application/Compras` (new) | New | `ServicioDeCompras` (borrador CRUD, confirmar, anular, list), `ServicioDeSaldoDeProveedor` (derived read) |
| `src/Ways.Application/Stock/ServicioDeStock.cs` | Modified | `TransferirAsync` + `ContarAsync` alongside `AjustarAsync`, same transaction shape |
| `src/Ways.Api/Endpoints` | New/Modified | `ComprasEndpoints`, `/api/stock/transferencias`, `/api/stock/conteos`, proveedor saldo; authorization per decision 11 |
| `src/Ways.Infrastructure/Persistencia` | Modified | Migration `ComprasYTransferenciasEtapa8`; EF configs; `estado_compra` enum registered in **both** `DependencyInjection.cs:99-105` **and** `WaysDbContextFactory.cs:39-45`; RLS; `db-error-backstops` |
| `src/Ways.Infrastructure/Persistencia/InicializadorDeBaseDeDatos.cs` | Modified | `TiposComprobanteBase` (`:63-76`) gains a `Clase` field — today `Clase = Venta` is hardcoded at `:424` — plus the dual-path idempotent insert (`:420-437`) |
| `src/Ways.Web` | New/Modified | Compras, transferencias, conteo screens + proveedor saldo view + `api/compras.ts` + descriptor tests |
| `docs/10-modelo-de-datos.md` | Modified | §5/§6 status notes (etapa 8 implemented; the two deferred FKs landed), §1 compra-tipo note, stage table |

## Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| A compra-clase `codigo` colliding with a venta code — `ux_tipos_comprobante_codigo` is UNIQUE on `codigo` **alone** (`TipoComprobanteConfiguration.cs:54-57`) and `ResolverTipoComprobanteAsync` looks up **by codigo alone** (`ServicioDeVentas.cs:697-702`), so a second "FA" could shadow the sale path | High | Prefixed codes (`C-FA`/`C-FB`/`C-FC`, decision 7); the UNIQUE index is **not** widened; a test asserts every venta code still resolves to its venta row after the seed |
| Seeding compra tipos breaks the emptiness-guarded seeder on a **fresh** database (stage-7's caught bug: the migration insert ran first, the `!AnyAsync()` block then skipped, losing the other catalog rows) | High | Dual path with the `AND EXISTS` fresh-DB guard, exactly as shipped in `CuentaCorrienteEtapa7`; a dedicated fresh-database boot test |
| The two-sided transfer breaking the sum-invariant (`specs/stock/spec.md:86-96`), the project's load-bearing stock requirement | High | Each row's `id_punto_venta` is **the location it affects**; both rows + both upserts in one transaction; the invariant test is extended to a mixed sequence including a transfer and asserted **per punto de venta** |
| Confirm partially applied (stock in, `costo_nominal` not, or vice versa) | High | One transaction, `EstrategiaSinReintento`, decide-then-commit; fault-point tests at each step assert nothing moved |
| The same supplier invoice entered twice (double stock, double debt) | Med-High | The UNIQUE triple as a real constraint + `db-error-backstops` mapping to a `409`; a race test |
| A confirmed compra anulada after the goods were sold, driving stock negative silently | Med-High | Refused with a `409` naming the remedy (decision 10); a deliberate asymmetry vs. ventas, documented in the spec |
| `costo_nominal` overwritten by a wrong cost, then anulación not reverting it | Med | Documented as deliberate (decision 10); the remedy is an explicit articulo edit; the spec says so in a scenario |
| Touching `ServicioDeStock` regressing the ajuste path or the sale decrement | Med | New methods, not edits; the entire stage-5/6/7 suite stays green as a slice gate; `judgment-day` per PR |
| Greenfield scope creep (a full WMS hiding behind "inventario") | Med-High | Decision 1 pins the minimal conteo; the full-count workflow is explicitly out of scope and trimmable as one slice |
| Reviewer overload — the largest stage since 5 (two tables, three write paths, four screens) | High | Chained PRs stacked-to-main; slice by write path; Review Workload Forecast discipline |

## Rollback Plan

Almost fully additive; nothing in stages 1–7 changes shape.

- `comprobantes_compra` and `items_comprobante_compra` are **new tables** — dropping them removes
  the feature entirely. No stage 1–7 read path joins them.
- `movimientos_stock.id_comprobante_compra` and `gastos.id_comprobante_compra` are **additive and
  nullable**; dropping them restores the pre-stage-8 shape bit-for-bit (existing rows carry NULL).
- The `motivo_stock` values `compra`/`transferencia`/`inventario` **already exist** in the database
  — reverting the code removes the writers, and the enum returns to being reserved. Movements
  already written stay valid and stay counted: the ledger is append-only, so **no repair is ever
  needed** and no cache is left inconsistent (every stage-8 write went through the same upsert).
- Compra-clase `tipos_comprobante` rows deactivate (`activo = false`) instead of being deleted,
  the same honest `Down` shipped for `RC` in stage 7.
- The `estado_compra` Postgres enum drops with its tables.
- **No backfill, no rewrite, no migration of existing data.**

One additive migration; reverting it plus the doc-10 edits restores stage 7 exactly.

## Dependencies

- Stages 1–7 (merged and archived): `articulos` (`costo_nominal`, `costo_lista`,
  `descuento_proveedor`), `proveedores` (`margen`), `grupos.margen`, `alicuotas_iva`,
  `tipos_comprobante` + `clase_comprobante` (`ClaseComprobante.cs:10` already has `Compra`),
  `stock` / `movimientos_stock` / `ServicioDeStock.AjustarAsync`, `gastos` + its turno gate,
  `SugeridorDePrecio`, `ServicioDePrecios.AbrirNuevoPrecioAsync`, `EstrategiaSinReintento`,
  `ManejadorDeErrores`, `Politicas.OperacionDePos` / `GestionDeCatalogo`.
- **DB Change Gate evaluation — exercised by the orchestrator in autonomous mode** (see
  `state.yaml`), before any migration is generated.
- `react-async-state` (mandatory) and `web-descriptor-tests` for every new screen;
  `db-error-backstops` per new constraint; `dto-contract-honesty`; `judgment-day` before every PR.

## Success Criteria

- [ ] A compra is built as `borrador` across several requests, confirmed once, and the goods appear
      in `stock` at the comprobante's punto de venta with `motivo = compra` movements carrying
      `id_comprobante_compra`
- [ ] Confirming overwrites `costo_nominal` **only** for items with `actualiza_costo = true`
- [ ] `precio_sugerido` is computed and shown but **never** applied by the confirm; applying it is a
      separate call that opens a new `precios` row (history preserved)
- [ ] The same `(proveedor, tipo, numero_externo)` cannot be confirmed twice — the constraint fires
      and is mapped to a business error, proven under concurrency
- [ ] A failed confirm leaves stock, cache, `costo_nominal` and `estado` untouched (fault-point test)
- [ ] Anulando a confirmed compra writes contramovimientos and restores the cache; it is **refused**
      with a `409` when the reversal would leave stock negative, and `costo_nominal` is not reverted
- [ ] A transferencia writes exactly **two** movements in one transaction and both caches move by
      `∓cantidad`; a failure moves neither
- [ ] A transferencia with insufficient origin stock, with `origen = destino`, or with a destino of
      another tenant is rejected — while a **sale** of the same articulo still goes negative
      (the asymmetry is proven in both directions)
- [ ] A conteo writes one `motivo = inventario` movement equal to `contada − actual`, and a
      zero-difference conteo writes **no** ledger row
- [ ] No endpoint anywhere in this stage accepts a delta, a total or a stock quantity to store
      directly
- [ ] `stock.cantidad` equals `SUM(movimientos_stock.cantidad)` per `(articulo, punto_venta)` over a
      scenario mixing venta, ajuste, anulación, compra, transferencia and inventario
- [ ] Paying a compra creates an ordinary `gasto` linked to it, under the existing open-turno gate,
      and the turno's arqueo changes by exactly that importe **with no new derivation term** — the
      `CalculadorDeArqueo` code is proven unchanged
- [ ] The proveedor saldo read equals `Σ compras confirmadas − Σ gastos`, ignores borradores and
      anuladas, and stores nothing
- [ ] Compra/transferencia/conteo write paths return `403` for a Vendedor; the compra list and the
      proveedor saldo do not
- [ ] Every venta `tipos_comprobante` code still resolves to its venta row after the compra seed,
      on **both** a fresh database and one migrated from stage 7
- [ ] Every new constraint has its `db-error-backstops` mapping + race test

## Resolved product decisions (autonomous mode)

**All decisions below were resolved by the ORCHESTRATOR under the user's explicit autonomous
mandate (2026-08-05), including the DB Change Gate.** Provenance and rationale are recorded per
decision so the user can audit them in the final summary. Binding for spec/design/tasks.

1. **Scope is compras + transferencias + a MINIMAL inventario — ⚑ THE ONE AUTONOMOUS SCOPE CALL,
   FLAGGED FOR THE FINAL SUMMARY.** The stage table (doc-10:639) names only *"Comprobantes de
   compra + transferencias de stock"*, but `motivo_stock` reserves `inventario` (doc-10:449-450,
   `MotivoStock.cs:5-8`) and stage 8 is the **last** stage of doc 10. *Rationale*: leaving one enum
   value writer-less would spawn a phantom stage 9 for a single endpoint that reuses machinery this
   stage already builds. Shipped: a **per-articulo conteo** (`motivo = inventario`, distinct from
   `ajuste` so a recount is not confused with a correction, reusing `AjustarAsync`'s transaction
   shape). **NOT** shipped: any full-count snapshot/freeze/variance workflow. *Trim path*: if the
   user disagrees, dropping the conteo removes exactly one small slice and nothing else changes.
   *(orchestrator-resolved, autonomous mode)*
2. **Compra lifecycle exactly per doc-10:401-404.** `borrador` — incrementally editable, with the
   remito in hand, **no ledger effect whatsoever**; `confirmar` — **ONE transaction**: entry
   `movimientos_stock` rows (`motivo = compra`, `id_comprobante_compra` set), `costo_nominal`
   overwritten where `actualiza_costo` (doc-10:396), stock cache upserted; `anulada` — reversed by
   contramovimientos (doc-10:404, the stage-5 anulación pattern). Confirmada and anulada
   comprobantes are **immutable**. *Note*: a mutable `borrador` is a deliberate exception to the
   project's append-only posture, and it is safe **precisely because** a borrador has produced no
   movement — the spec must state that as the reason. *(orchestrator-resolved, autonomous mode)*
3. **`precio_sugerido` is a suggestion; updating sale prices is a SEPARATE explicit action.**
   doc-10:403 says the confirm *"**ofrece** actualizar precios de venta según margen del
   grupo/proveedor"* — offers, not applies. `precio_sugerido` is computed per item through the
   existing pure `SugeridorDePrecio` (`SugeridorDePrecio.cs:39-55`, whose own doc-comment already
   states *"La sugerencia NUNCA se aplica sola"*) and stored on the item as an audit of what was
   proposed at receiving time; applying it goes through `ServicioDePrecios.AbrirNuevoPrecioAsync`
   (`ServicioDePrecios.cs:80`) so price history is preserved. *Rationale*: a silent margin-driven
   repricing of the whole catalog on a receiving confirm is the single most dangerous thing this
   stage could do. *(orchestrator-resolved, autonomous mode)*
4. **`numero_externo` is the identity; there is no correlativo propio.** doc-10:374-375 verbatim:
   *"el número DEL PROVEEDOR ('0003-00012345'); acá no hay correlativo propio"*. `NumeracionComprobante`
   is **not** involved — a compra is a document we *receive*. The dedupe is the UNIQUE of
   doc-10:384, tenant-scoped and restricted to non-anuladas (see the gate). *Consequence*:
   `numero_externo` may be NULL while `borrador` (the invoice may arrive after the goods) and is
   **required to confirm**. *(orchestrator-resolved, autonomous mode)*
5. **Transferencia = ONE transaction, TWO mirrored movements.** Origin row (`id_punto_venta =
   origen`, `cantidad = −q`), destination row (`id_punto_venta = destino`, `cantidad = +q`), both
   `motivo = transferencia` with `id_punto_venta_destino` populated (column and FK already exist —
   `MovimientoStockConfiguration.cs:51`, `:96-103`), both caches upserted in the same transaction.
   **No in-transit state, no transfer document.** ⚑ **VERIFIED CORRECTION TO THE INCOMING BRIEF**:
   the brief called the insufficient-origin-stock refusal *"the same rule class as venta"* — it is
   the **opposite**. `openspec/specs/stock/spec.md:34-35` states that at checkout *"Availability is
   NOT checked — negative stock is allowed (legacy parity)"*. Refusing a transfer is therefore a
   **deliberate tightening**, justified by a principle the spec must pin: **counter operations never
   block on stock; back-office stock-reducing operations do** — a cashier must never be stopped
   mid-sale, whereas a depot move that would invent units costs nothing to refuse.
   *(orchestrator-resolved, autonomous mode)*
6. **Proveedor cuenta corriente stays embrionaria — ⚑ FLAGGED FOR THE FINAL SUMMARY.**
   doc-10:408-409 verbatim: *"Una compra puede estar impaga (sin gasto asociado) — eso ya da una
   cuenta corriente de proveedores embrionaria **sin tabla extra**"*. So: **no** movement table,
   **no** saldo cache, **no** imputación, **no** aging. The saldo is a **derived read**:
   `Σ compras confirmadas.total − Σ gastos (categoria = proveedor, id_proveedor = X)`, with
   per-compra payment status computed from the **linked** gastos only. Borradores and anuladas are
   excluded. The read is explicitly documented as an **approximation, not an invariant** — an
   unlinked proveedor gasto still reduces it, because it is still money paid to that supplier.
   *Departure recorded*: stage 7 set a richer-ledger precedent (`movimientos_cuenta_corriente` with
   `saldo_resultante`) that is **deliberately NOT followed here** because doc 10 forbids the extra
   table. A full proveedor CC is a future change, not a gap. *(orchestrator-resolved, autonomous mode)*
7. **Compra-clase `tipos_comprobante` are platform-seeded with PREFIXED codes.** *Constraint
   verified in code*: `ux_tipos_comprobante_codigo` is UNIQUE on `codigo` **alone**
   (`TipoComprobanteConfiguration.cs:54-57`) **and** `ResolverTipoComprobanteAsync` resolves by
   `codigo` alone before checking `Clase` (`ServicioDeVentas.cs:697-702`) — a compra row reusing
   `"FA"` could be returned by that `FirstOrDefaultAsync` and break the sale path. *Decision*: seed
   `C-FA`, `C-FB`, `C-FC` (`clase = compra`, `letra` A/B/C, `signo = +1`, `discrimina_iva` true only
   for A, `afecta_stock = true`, `es_fiscal = false`). *Rejected alternative*: widening the index to
   `(clase, codigo)` — it would let compra rows reuse `FA`, but it changes a stage-1 global index
   **and** leaves the by-codigo lookups ambiguous forever. `es_fiscal = false` because the flag means
   *"¿reporta a AFIP/ARCA cuando exista FE?"* (doc-10:88) and we never **emit** a supplier's invoice;
   this says nothing about IVA crédito, which is out of scope. Design may refine the exact strings —
   the **globally-unique-codigo constraint is binding**. *(orchestrator-resolved, autonomous mode)*
8. **The two deferred FKs land together, in this stage's migration.**
   `movimientos_stock.id_comprobante_compra` (doc-10:451, deferral note doc-10:457-465,
   `MovimientoStockConfiguration.cs:48-50`) and `gastos.id_comprobante_compra` (doc-10:416, deferral
   note doc-10:426-434, `Gasto.cs:11-15`) — **column + composite FK each**, the established pattern.
   Both doc-10 notes name etapa 8 explicitly; this is scheduled debt being paid, not new design.
   *(orchestrator-resolved, autonomous mode)*
9. **Sum-invariant discipline: every stock writer is append-only ledger + same-transaction cache
   upsert.** The shape is `ServicioDeStock.AjustarAsync` (`:32-70`, `:97-119`). **No endpoint accepts
   a client-supplied delta or a stock quantity to store**: the compra supplies quantities, the
   transfer supplies a quantity, the conteo supplies the *counted total* and the **server derives**
   the delta under the stock row lock. *Rationale*: this is what keeps the legacy D3 bug
   (doc-01:241 — restore **added** stock) and the D7 class unrepresentable rather than merely fixed.
   *(orchestrator-resolved, autonomous mode)*
10. **Anulación of a confirmed compra: contramovimientos, refused when it would go negative,
    `costo_nominal` not reverted.** (a) The reversal writes `motivo = anulacion` contramovimientos
    (doc-10:449 already reserves `anulacion` for exactly this) and upserts the caches in the same
    transaction. (b) It is **refused with a `409`** naming the offending articulos when the reversal
    would leave any stock negative — the goods already left, so pulling them back would make the
    ledger claim units that do not exist. The remedy (a conteo or an ajuste) is available to the
    **same role** that can annul (decision 11), so the refusal never traps the operator.
    *Asymmetry, deliberate*: a **sale** may drive stock negative; annulling a **compra** may not
    (same principle as decision 5). (c) `costo_nominal` is **NOT** reverted: cost history has moved
    on and other prices may already derive from it; a wrong cost is fixed by editing the articulo.
    *(orchestrator-resolved, autonomous mode)*
11. **Authorization mirrors the closest existing precedent: the manual stock writer.**
    *Precedent verified in code*: `POST /api/stock/ajustes` stacks `Politicas.GestionDeCatalogo`
    over the group's `Politicas.OperacionDePos` (`StockEndpoints.cs:24-30`), and since
    `GestionDeCatalogo` is Admin-only (`Politicas.cs:15`, `:68-70`) the AND composition makes the
    only existing manual stock-write path **Admin-only** (`openspec/specs/stock/spec.md:50-56`).
    *Decision*: compra borrador/confirmar/anular, transferencias, conteos and the price-application
    action use **that same stacking**; the compra list, compra detail and proveedor saldo reads stay
    on `OperacionDePos`; paying a compra is an ordinary gasto and keeps the gastos endpoint's
    existing `OperacionDePos` gate (`GastosEndpoints.cs:12`). *Rejected*: inventing a new tier or
    reusing `SupervisionDeCuentaCorriente` (`Politicas.cs:57`) — that policy is named for balance
    supervision, and stage 8 has no reason to be **looser** than the existing manual stock writer it
    is replacing. A Supervisor tier can be added later with no schema change. *Consistency note*: the
    stage-7 product flag (an `OperacionDePos` screen reachable only from an Admin-only nav entry)
    does **not** recur — every stage-8 write screen is Admin-only, matching its nav.
    *(orchestrator-resolved, autonomous mode)*

### DB Change Gate — orchestrator evaluation (autonomous mode)

Model presented for the record, grouped by write path.

**(a) Compras — two new `[operativa]` tables (doc-10:370-398), tenant-scoped per doc 09:**

- `comprobantes_compra`: `id_comprobante_compra`, `id_tenant`, `id_proveedor`,
  `id_tipo_comprobante` (clase = compra), `numero_externo citext NULL`, `fecha_comprobante date`,
  `fecha_recepcion timestamptz`, `id_punto_venta`, `id_empleado`, `subtotal`, `descuento_total`,
  `iva_total NULL`, `total`, `observaciones`, `estado estado_compra NOT NULL`.
  **No baja lógica** — same posture as `comprobantes_venta`: state transitions, never deletion.
- `items_comprobante_compra`: `id_item`, `id_comprobante_compra`, `orden`, `id_articulo`,
  `descripcion` (snapshot), `cantidad numeric(12,3)`, `bultos numeric(10,2) NULL`,
  `unidades_por_bulto numeric(10,2) NULL`, `costo_unitario numeric(14,4)`,
  `descuento numeric(14,2) DEFAULT 0`, `id_alicuota_iva`, `porcentaje_iva numeric(5,2)`,
  `total numeric(14,2)`, `actualiza_costo boolean DEFAULT true`, `precio_sugerido numeric(14,2) NULL`.
- **Dedupe**: doc-10:384 specifies `UNIQUE (id_proveedor, id_tipo_comprobante, numero_externo)`.
  **Two conformity adjustments, approved and logged**: (i) it must include `id_tenant` — every
  operativa table is tenant-scoped and doc 10's DDL sketches omit it by convention; (ii) it ships as
  a **partial** unique index excluding `estado = 'anulada'` (and NULL `numero_externo`), so a
  mistyped invoice that was annulled can be re-entered correctly.
- **New Postgres enum `estado_compra`** (`borrador | confirmada | anulada`, doc-10:382) — must be
  registered in **both** `DependencyInjection.cs:99-105` **and** `WaysDbContextFactory.cs:39-45`.
- Composite FKs `(id, id_tenant)` per the established pattern; `id_empleado` stays a **simple** FK to
  `usuarios` (the deliberate deviation documented at doc-10:466-470).

**(b) Deferred FKs landing (decision 8):** `movimientos_stock.id_comprobante_compra` and
`gastos.id_comprobante_compra` — both `int NULL` + composite FK, additive, `ON DELETE RESTRICT`,
each with its supporting index. **Both were explicitly scheduled for this stage** by doc-10:457-465
and doc-10:426-434.

**(c) Transferencias / inventario:** **no schema change at all.** `motivo_stock` already carries
`transferencia` and `inventario` (doc-10:449-450) and `id_punto_venta_destino` already exists with
its FK (`MovimientoStockConfiguration.cs:51`, `:96-103`).

**(d) Global catalog:** three new `tipos_comprobante` rows of `clase = compra` (decision 7).
`TiposComprobanteBase` (`InicializadorDeBaseDeDatos.cs:63-76`) currently hardcodes
`Clase = ClaseComprobante.Venta` at `:424`, so the tuple gains a `Clase` field. Dual path
(seed-list + idempotent migration `INSERT ... WHERE NOT EXISTS` **with the `AND EXISTS` fresh-DB
guard**) — the stage-7 lesson: without that guard, a fresh deployment gets the compra rows before
the seeder, whose `!AnyAsync()` check then skips and silently loses every other catalog row.

**(e) Conformity:** strict doc-10 §5/§6 shape; no change to any existing column or index; no change
to `estado_comprobante`, `categoria_gasto`, `motivo_stock` or the arqueo derivation; RLS and
operativa scoping identical to every other tenant table. Migration name:
`ComprasYTransferenciasEtapa8`.

**Evaluation: APPROVED by the orchestrator under the autonomous mandate**, conditional on
(i) the compra-tipo seed shipping dual-path with the `AND EXISTS` fresh-DB guard and proven on both
a fresh and a stage-7-migrated database, (ii) compra codes being globally unique against every
existing venta code with a test proving venta resolution is unaffected, (iii) the dedupe unique
being tenant-scoped and partial as described, and (iv) every new constraint having a
`db-error-backstops` mapping plus a race test. Recorded in `state.yaml` for the user's final summary.

## Note for sdd-tasks

Slice by **write path**. Indicative order:

1. **Schema + seed gate**: migration `ComprasYTransferenciasEtapa8` (two tables, `estado_compra`
   enum in both registration sites, the two deferred FK columns, the partial dedupe index, RLS,
   backstops) + the compra-tipo dual-path seed + the doc-10 §1/§5/§6 notes **in the same slice** so
   the doc never drifts (stage-7 gate condition, reused).
2. **Compra borrador**: pure `CalculadorDeCompra` first (cantidad = `unidades + bultos ×
   unidades_por_bulto`, descuento, IVA per alícuota, totals), then the borrador CRUD + item editing
   + `precio_sugerido` via `SugeridorDePrecio`.
3. **Confirmar + anular** — the centerpiece and the guarded path: the one transaction (stock entry,
   `costo_nominal`, cache), the contramovimientos, the negative-stock refusal. **Its own full
   judgment-day round**, with the entire stage-5/6/7 suite green.
4. **Transferencias + conteo de inventario**: both new `ServicioDeStock` writers + the extended
   sum-invariant test (per punto de venta, mixed sequence).
5. **Gasto ↔ compra link + proveedor saldo + web compras**: the `id_comprobante_compra` on the gasto
   write path, the derived saldo read, the **proof that the arqueo derivation is byte-unchanged**,
   and the compras screen.
6. **Web transferencias + conteo + proveedor saldo view** (splittable if the forecast demands it).

Apply the Review Workload Forecast discipline (400-line budget; exact guard lines
`Decision needed before apply`, `Chained PRs recommended`, `400-line budget risk`). This is the
largest stage since 5 — expect **High** budget risk and chained PRs **stacked-to-main** per
`protocolo-pr-solo-dev`, with `judgment-day` before every PR.

## Deferred / adjacent (recorded, not in scope)

- **Full-count inventory workflow** (snapshot, freeze, variance report) — future change.
- **A real proveedores current account** with its own movement ledger — future change, deliberately
  refused here by doc-10:409.
- **Órdenes de compra, recepción parcial, notas de crédito de proveedor.**
- **Libro IVA compras / crédito fiscal / FE** — compra tipos are `es_fiscal = false`.
- **`ServicioDeArticulos` `articulos_empresas` replace-set concurrency gap** — still open, carried
  since stage 4.
- **Vendedor UI reachability of the estado de cuenta screen** — the stage-7 product flag, still
  awaiting the user's navigation decision; unrelated to this stage.

## Proposal question round (autonomous mode — not asked, recorded for review)

The user delegated all decisions, so no question round was run. The questions that **would** have
been asked, and the assumptions taken instead, are:

1. *Should stage 8 include inventario at all, or leave it for a stage 9?* → **Assumed included,
   minimally** (decision 1) — the one autonomous scope call.
2. *Should confirming a compra update sale prices automatically?* → **Assumed no** (decision 3);
   suggestion stored, application explicit.
3. *Should a proveedor account be a real ledger like the client one?* → **Assumed no** (decision 6);
   derived read only, per doc-10:409.
4. *May annulling a compra drive stock negative?* → **Assumed no** (decision 10), against the
   ventas precedent, with the remedy in the same role's hands.
5. *Who operates the depot — Admin only, or Supervisor too?* → **Assumed Admin only** (decision 11),
   mirroring the existing manual stock writer.

If any of these five is wrong, say so before `sdd-tasks`: 1 and 5 are cheap to change, 3 and 4 are
spec-level, and 2 is the only one that would change the confirm transaction.
