# Proposal: Stage 5 — POS y ventas

## Intent

Implement Etapa 5 of `docs/10-modelo-de-datos.md` — "Comprobantes de venta + pagos +
stock + movimientos", the stage doc 10 marks as **Vender (paridad núcleo)**. Stages 1–4
built everything a sale *reads* (organización, clientes, artículos, precios con historia,
ofertas resueltas en batch). Nothing yet **writes** a sale: the system can price a cart it
cannot sell.

Three problems are solved at once:

1. **The legacy sells without integrity.** In `alsina/`, a sale is a serialized
   `ventas.articulos` string, a per-line `UPDATE articulos SET existencia = existencia − n
   WHERE barra = ...` **outside any transaction**, hardcoded tolerances ($10 / $20), a
   `saldo` column moved by hand, and `eliminado = 1` plus a *restaurar* path that inflates
   stock. Doc 10 replaces all of it with an auditable movement ledger.
2. **Stage 4 left the POS locked out.** `POST /api/ofertas/resolver` — and in fact every
   read the POS needs (artículos, códigos de barra, clientes, listas, parámetros) — is
   gated by `Politicas.GestionDeCatalogo` (admin only). `RolConocido.Vendedor` exists and
   is referenced by nothing. The stage-4 verify report carried this forward explicitly.
3. **Stock does not exist as a concept yet.** Articles have no per-punto-de-venta balance
   and no movement history, so nothing can be sold, adjusted, returned or audited.

## Scope

### In Scope

- **`comprobantes_venta` + `items_comprobante_venta` + `pagos_comprobante`** (doc 10 §4),
  operativa scope (`id_tenant` + `id_punto_venta`, doc 09), `UNIQUE (id_punto_venta,
  id_tipo_comprobante, numero)`, `estado_comprobante` enum (`emitido | anulado`).
  **Snapshot immutability**: items carry copies of `descripcion`, `codigo_barra`,
  `id_area`, `precio_unitario`, `id_lista_precio`, `id_oferta`, `descuento`,
  `porcentaje_iva` — a reprinted ticket never re-joins the live catalog.
- **`numeraciones_comprobante`** (doc 09) per `(id_punto_venta, tipo_comprobante)`, with
  atomic `UPDATE ... RETURNING` allocation. Visible number `PPPP-NNNNNNNN`.
- **`stock`** (PK `id_articulo, id_punto_venta`, `cantidad` as a cache of the ledger) and
  **`movimientos_stock`** with signed `cantidad` and `motivo_stock` enum. Stage 5
  implements `venta`, `anulacion` and `ajuste` only; `compra` / `transferencia` /
  `inventario` are enum values with no write path yet.
- **Checkout**: one transaction writes comprobante + items + pagos + movimientos de stock
  + stock cache + número + (if used) movimiento de cuenta corriente, or nothing.
- **Payment rules, parametrized** (legacy B6 parity, hardcoded values killed): rejection
  order preserved; `tolerancia_pago` and `vuelto_maximo` resolved through
  `ServicioDeParametros` (punto de venta > empresa > default) — **never hardcoded**;
  `vuelto` only for medios with `AdmiteVuelto`; `referencia` required per
  `RequiereReferencia`; cuenta corriente blocked for Consumidor Final and limited by
  `LimiteCredito` / `CreditoIlimitado`.
- **Devoluciones as NCX comprobantes** through the same flow (negative lines), with
  `id_comprobante_asociado` optional but populated when the return references an original.
- **Anulación**: `estado = anulado` + inverse `movimientos_stock` + CC contramovimiento,
  all in one transaction. **No `restaurar` endpoint, ever.**
- **Manual stock `ajuste`** (admin-only) so stock can be loaded before compras exist.
- **Narrow cuenta-corriente write slice** (pulled forward from stage 7): the
  `movimientos_cuenta_corriente` table, `consumo` written inside the sale transaction,
  `contramovimiento` on anulación, `Cliente.Saldo` maintained in the same transaction as
  the cache the credit check reads, `saldo_resultante` snapshotted per movimiento.
- **New `Politicas.OperacionDePos`** (Vendedor + Admin) and the POS read surface opened to
  it: artículo/código-de-barra lookup, cliente search, listas de precio, parámetros
  (read-only), catálogos fiscales/medios de pago, and `POST /api/ofertas/resolver`.
  ABM writes stay on `GestionDeCatalogo`.
- **Scan resolution** (legacy rule I.2, promised since stage 3): input `< 7` digits →
  `codigo_interno`; `>= 7` → `codigos_barra`; `activo` only; `<cantidad>*<codigo>` syntax;
  re-scanning an article sums quantity.
- **POS screen in `Ways.Web`**: cart, scan input, cliente picker, payment panel, ticket
  view; `react-async-state` compliant with `web-descriptor-tests` coverage.
- **Comprobante-letter resolution rule** (condición fiscal cross) as a pure, fully-tested
  Domain class — **dormant**: the POS only reaches TX (venta) and NCX (devolución).

### Out of Scope

- **Turnos de caja, arqueos, movimientos de caja, tesorería, gastos** — stage 6.
  `comprobantes_venta.id_turno_caja` ships **NULL** (see decision 1).
- **Cuenta corriente management, reliquidación a precio del día (F4), pagos de cuenta,
  CC UI/reporting** — stage 7, except the narrow write path above.
- **Comprobantes de compra, transferencias entre locales, inventario** — stage 8.
- **AFIP / CAE / fiscal invoicing** — deferred; `tipos_comprobante.es_fiscal` stays
  `false` and the letter rule stays dormant.
- **Comanda printing, tickets en espera (slots), `restaurar` ticket** — legacy features
  deliberately not reproduced (the slot model and the restore bug die by design).
- **Employee ↔ punto de venta assignment (`asignaciones_empleado`)** — deferred
  (decision 4).
- **Recargo por medio de pago** (`medios_pago.recargo_porcentaje`) — column stays dormant
  (decision 11).
- **Legacy historical sales migration** (doc 10 places the data load in this stage; the
  *engine* ships here, the ETL does not).
- **Declared-vs-calculated vuelto difference** (`ventas.saldo` in the legacy) — a caja
  reconciliation concern, stage 6.

## Capabilities

### New Capabilities

- `comprobantes-venta`: emission of TX/NCX comprobantes, snapshot items, totals
  (`subtotal` / `descuento_total` / `total`, IVA fields NULL while non-fiscal),
  `estado` lifecycle, anulación with contramovimientos, devoluciones as NCX.
- `numeracion-de-comprobantes`: atomic per-`(punto_venta, tipo_comprobante)` correlativo,
  `PPPP-NNNNNNNN` formatting, gap/duplicate behaviour under concurrency.
- `pagos-de-comprobante`: multi-medio payment capture and the full rejection-order
  ruleset (tolerancia, vuelto máximo, `AdmiteVuelto`, `RequiereReferencia`, CC limit,
  Consumidor Final exclusion).
- `stock`: per-punto-de-venta balance as a ledger cache, signed `movimientos_stock` with
  `motivo`, negative stock allowed, manual `ajuste` path.
- `consumo-cuenta-corriente`: narrow CC write path — `consumo` on sale,
  `contramovimiento` on anulación, credit-limit evaluation, `saldo_resultante`.
- `operacion-de-pos`: the POS authorization tier, explicit `idPuntoVenta` per request,
  scan resolution rules, cart pricing integration (lista del cliente → resolución de
  ofertas), checkout orchestration contract.

### Modified Capabilities

- `clientes`: `saldo` is **no longer frozen** — the CC consumo/contramovimiento moves it
  inside the sale transaction; `limite_credito` / `credito_ilimitado` become enforced at
  checkout; cliente **read** paths open to the POS policy (ABM unchanged).
- `resolucion-de-ofertas`: `POST /api/ofertas/resolver` authorization relaxed from
  `GestionDeCatalogo` to `OperacionDePos` (closes the stage-4 carryover). Resolution
  semantics unchanged.
- `codigos-barra`: the scan-resolution rule (`< 7` → `codigo_interno`, `>= 7` →
  `codigos_barra`, activos only, `N*codigo` syntax) becomes a specified behaviour; POS
  read access.
- `articulos`: read access under the POS policy (lookup for the cart), no ABM change.
- `parametros-operativos`: `tolerancia_pago` / `vuelto_maximo` become **consumed and
  server-authoritative** at checkout; read access under the POS policy for UI preview.

## Approach

1. **Reuse, not redesign.** `EntidadTenant` + RLS helpers, `ManejadorDeErrores` mapping
   contract, `db-error-backstops` per constraint, migration/testing patterns from
   stages 1–4. Operativa scoping (`id_tenant` + `id_punto_venta`) is new for entities but
   already specified in doc 09.
2. **Numeración clones a proven pattern.** `AsignadorDeNumeroCliente`'s raw-ADO atomic
   counter is the precedent; `numeraciones_comprobante` uses `UPDATE ... RETURNING` inside
   the sale transaction. No client-supplied numbers, ever.
3. **The checkout is ONE transaction** — comprobante, items, pagos, movimientos de stock,
   stock cache, número, CC movimiento. Stock decrement takes an advisory lock (the pattern
   proven in `ServicioDePrecios` / `ServicioDeOfertas`) **inside** that transaction; the
   locking order (numeración → stock por artículo ascendente → cliente) is fixed and
   documented to avoid deadlocks. Design pins it.
4. **Pure Domain first.** Payment validation (rejection order, tolerancia, vuelto, CC
   limit), line/total arithmetic, the scan-resolution parser and the comprobante-letter
   rule all live as DB-free Domain classes with exhaustive unit tests, mirroring
   `ResolvedorDePrecios` / `ResolvedorDeOfertas` / `PoliticaDeRoles`.
5. **Pricing has exactly one path**: `cliente.IdListaPrecio` → `ServicioDeOfertas`
   batch resolution. No per-client dual-price legacy hack (doc-01 rule I.4). Applied
   ofertas land as `items.id_oferta` + `descuento` — no phantom `OF...` lines.
6. **No server-side POS session.** The client sends `idPuntoVenta` explicitly per request,
   consistent with `ServicioDeParametros` and ADR-10's explicit-parameter posture.
7. **DB CHANGE GATE (CLAUDE.md), the largest of the project**: ~7 tables
   (`comprobantes_venta`, `items_comprobante_venta`, `pagos_comprobante`, `stock`,
   `movimientos_stock`, `numeraciones_comprobante`, `movimientos_cuenta_corriente`) +
   2 enums (`estado_comprobante`, `motivo_stock`) + RLS policies. The gate summary MUST
   group by **write path**, not by table, and must call out the cross-stage CC pull and
   the `id_turno_caja NULL` deviation.

## Affected Areas

| Area | Impact | Description |
|---|---|---|
| `src/Ways.Domain/Ventas` (new) | New | `ComprobanteVenta`, `ItemComprobanteVenta`, `PagoComprobante`, pure `ValidadorDePagos`, `CalculadorDeTotales`, `ResolvedorDeLetraComprobante` (dormant) |
| `src/Ways.Domain/Stock` (new) | New | `Stock`, `MovimientoStock`, `MotivoStock`, movement rules |
| `src/Ways.Domain/CuentaCorriente` (new) | New | `MovimientoCuentaCorriente`, credit-limit rule |
| `src/Ways.Domain/Clientes/Cliente.cs` | Modified | `Saldo` doc-comment: it now moves in stage 5 |
| `src/Ways.Application/Ventas` (new) | New | `ServicioDeVentas` (checkout, anulación), `AsignadorDeNumeroComprobante`, `ServicioDeStock` |
| `src/Ways.Application/Ofertas`, `Precios` | Unchanged | Consumed as-is (batch resolution) |
| `src/Ways.Infrastructure` | Modified | EF configs, the stage-5 migration, RLS policies for operativa tables, new backstop mappings |
| `src/Ways.Api/Seguridad/Politicas.cs` | Modified | New `OperacionDePos` policy (Vendedor + Admin) |
| `src/Ways.Api/Endpoints/*` | Modified/New | `VentasEndpoints`, `StockEndpoints` (new); POS read paths re-gated on `ClientesEndpoints`, `ArticulosEndpoints`, `CatalogosEndpoints`, `ParametrosEndpoints`, `OfertasEndpoints` (`/resolver`) |
| `src/Ways.Web` | New | POS screen + ticket view + descriptor/component tests |
| `docs/10-modelo-de-datos.md` | Modified (if deviations land) | Any schema deviation must be recorded — doc 10 must not drift |

## Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| Largest transaction of the project (6+ write paths under one commit) | High | Design pins the exact statement and lock order; integration tests assert all-or-nothing rollback on each failure point |
| Concurrent sales on the same articulo/punto de venta corrupting the stock cache | High | Advisory lock per `(tenant, articulo, punto_venta)` inside the sale transaction + rendezvous race tests (stage-4 precedent) |
| Deadlock between numeración, stock and cliente locks | Med-High | Fixed global lock order, documented and test-enforced |
| Numbering gaps/duplicates under concurrency or rollback | Med | Atomic `UPDATE ... RETURNING`; gaps accepted (non-fiscal TX/NCX), duplicates rejected by the UNIQUE index + backstop |
| Cross-stage CC pull grows into stage 7 by accident | Med | Write-only slice, explicitly no reliquidación / no CC UI; scope guard checked at verify |
| Selling without a turno de caja diverges from the legacy | Med | Documented deviation (decision 1); `id_turno_caja` nullable so stage 6 only adds the requirement |
| Negative stock allowed surprises the user | Med | Explicit legacy-parity decision (7); revisitable via a future parameter, not a schema change |
| Reviewer overload — biggest stage yet (7 tables + engine + API + POS screen) | High | Chained PRs stacked-to-main; `sdd-tasks` slices by write path; expect the largest slice count so far |
| Snapshot fields drifting back into live joins | Med | Spec pins reprint-after-catalog-change scenarios |

## Rollback Plan

Fully additive. Seven new tables, two new enums, new Domain namespaces, new endpoints and
a new POS route — all droppable without touching stage 1–4 flows. Changes to
already-shipped code are narrow and additive-in-effect:

- `Politicas.cs` **adds** a policy; existing policies keep their claim sets.
- POS read paths are **relaxed** (Admin keeps access — no admin loses anything); reverting
  restores the stricter gate.
- `Cliente.Saldo` moves only when a sale uses cuenta corriente; with the stage reverted,
  no code path writes it and the column returns to its dormant state.
- `ServicioDeOfertas` / `ServicioDePrecios` are consumed unchanged.

The migration is one additive migration; reverting it plus the doc-10 edit (if any)
restores stage 4 exactly.

## Dependencies

- Stages 1–4 (merged): organización + puntos de venta, clientes (incl. Consumidor Final
  protegido, `LimiteCredito` / `CreditoIlimitado`), artículos + códigos de barra, listas y
  precios con historia, `ServicioDeOfertas.ResolverAsync` (batch), `ServicioDeParametros`
  with `tolerancia_pago` / `vuelto_maximo` seeded, `TipoComprobante` TX/NCX and
  `MedioPago` (`AdmiteVuelto` / `RequiereReferencia`) seeded, `RolConocido.Vendedor`.
- **DB Change Gate approval — blocking**, before any migration is generated.
- `react-async-state` (mandatory) and `web-descriptor-tests` for the POS screen;
  `db-error-backstops` per constraint; `judgment-day` per slice before every PR.

## Success Criteria

- [ ] A Vendedor can complete a sale end-to-end: scan → cart priced by lista + ofertas →
      payment → comprobante emitido with a `PPPP-NNNNNNNN` number
- [ ] A failed sale leaves **nothing** behind: no comprobante, no item, no pago, no
      movimiento, no número consumed beyond the atomic allocation, no CC movement
- [ ] Concurrent sales of the same articulo never corrupt `stock.cantidad`
      (`cantidad` always equals the sum of its `movimientos_stock`)
- [ ] Anulación produces inverse movimientos and a CC contramovimiento; no `restaurar`
      endpoint exists
- [ ] A devolución is an NCX comprobante with negative lines, not a special-cased flag
- [ ] Payment rejections follow the legacy order, with tolerancia/vuelto read from
      parámetros — grep proves no hardcoded `10` / `20`
- [ ] Cuenta corriente is rejected for Consumidor Final and beyond `LimiteCredito`
      (unless `CreditoIlimitado`)
- [ ] A reprinted ticket is byte-identical after the catalog changes (snapshot proof)
- [ ] `POST /api/ofertas/resolver` is reachable by a Vendedor; ABM endpoints are not
- [ ] Every new constraint has its `db-error-backstops` mapping + race test

## Resolved product decisions (auto-mode question round)

Question round resolved in **auto mode** (2026-08-04): legacy defaults were adopted and
are documented here as assumptions the user reviews at the DB Change Gate. Binding for
spec/design/tasks unless corrected at the gate.

1. **Sales ship WITHOUT turno de caja.** `id_turno_caja` is written NULL; `turnos_caja` is
   stage 6, which will add the open-turno requirement. *Provenance*: **DEVIATION** from the
   legacy, which hard-requires an open caja. *Rationale*: forced by staging — requiring a
   turno now would drag arqueos and tesorería into stage 5. The column is nullable, so
   stage 6 only tightens a rule.
2. **New `Politicas.OperacionDePos` (Vendedor + Admin).** `POST /api/ofertas/resolver` is
   relaxed from `GestionDeCatalogo` to it, closing the stage-4 carryover — **and so is the
   rest of the POS read surface** (artículos, códigos de barra, clientes, listas,
   parámetros, catálogos fiscales/medios de pago), which the exploration confirmed is
   admin-only today. Writes stay admin. *Rationale*: `RolConocido.Vendedor` exists and is
   referenced by nothing; a POS a cashier cannot use is not a POS.
3. **No server-side "punto de venta actual".** The client selects the punto de venta and
   passes `idPuntoVenta` explicitly per request. *Provenance*: matches
   `ServicioDeParametros`' existing parameter pattern and ADR-10's explicit-parameter
   posture; the legacy's session-bound local is exactly the state we are not rebuilding.
4. **Employee ↔ punto de venta assignment DEFERRED.** Any tenant user holding the POS
   policy may operate any punto de venta of the tenant; `asignaciones_empleado` is **not**
   built in stage 5. *Rationale*: single-local tenants are the near-term reality; revisit
   when a multi-local tenant needs restriction. Documented assumption.
5. **Cuenta corriente is a first-class payment at checkout** (legacy parity), which pulls a
   **narrow slice of stage 7** forward: `movimientos_cuenta_corriente` created now with
   write-only paths (`consumo` in the sale transaction, `contramovimiento` on anulación)
   plus the credit-limit check. *Terminology correction to the brief*: the model uses
   `Cliente.LimiteCredito` + `CreditoIlimitado` (bool), **not** the legacy `acuerdo` with
   its `-1` sentinel; Consumidor Final is excluded. `Cliente.Saldo` (already in the schema,
   dormant since stage 2) becomes the maintained cache read by the check, updated in the
   same transaction, with `saldo_resultante` snapshotted per movimiento — the same
   cache-of-the-ledger shape doc 10 gives `stock.cantidad`. Design confirms cache vs.
   aggregation. **No reliquidación, no CC management UI** (stage 7).
6. **Anulación in scope**: `estado = anulado` + inverse `movimientos_stock` + CC
   contramovimiento, one transaction. **No `restaurar` endpoint, ever.** *Provenance*:
   doc 10 §4 kills the legacy "restaurar suma stock" bug by design.
7. **Negative stock ALLOWED** (no availability guard), plus a minimal admin-only manual
   `ajuste` path (`motivo = ajuste`) so stock can be loaded before compras (stage 8)
   exist. *Provenance*: legacy parity — the legacy never blocks a sale on stock. *Rationale*:
   blocking sales on a balance nobody has loaded yet would make the POS unusable on day one.
8. **The POS reaches only TX (venta) and NCX (devolución).** The comprobante-letter
   resolution rule (condición fiscal cross) ships as a pure, fully-tested Domain class but
   stays **dormant** until fiscal invoicing. *Rationale*: the rule is cheap to write once
   and expensive to retrofit; wiring it without AFIP would be theatre.
9. **Devoluciones are formalized as NCX comprobantes** through the same checkout flow with
   negative lines. *Provenance*: doc 10 §4 (`tipos_comprobante` with `signo` replacing the
   legacy `tipo` 1/2 flag).
10. **`id_comprobante_asociado` is OPTIONAL for NCX** (legacy parity allows standalone
    returns) but is populated when the return references an original comprobante.
11. **Recargo por medio de pago NOT applied** (added assumption). `medios_pago.recargo_porcentaje`
    exists in the schema but the legacy checkout never applies it (verified: doc-01 B6 has
    no recargo step; the seeded medios leave the column NULL). *Rationale*: applying it
    would make the comprobante total depend on the payment mix, i.e. totals that change
    after the lines are closed — a real modelling decision, not a parity detail. The column
    stays dormant; flagged for user correction at the gate.

## Note for sdd-tasks

Slice by **write path**, and expect the **largest slice count of the project so far**.
Indicative order:

1. **Foundational**: `OperacionDePos` policy + POS read-surface re-gating +
   `numeraciones_comprobante` (schema + atomic allocator + race tests).
2. **Schema gate**: the remaining 6 tables + 2 enums + RLS + EF configs + backstops
   (this is the DB Change Gate slice and the `size:exception` candidate).
3. **Checkout**: pure Domain rules (payment validation, totals, scan parser) +
   `ServicioDeVentas` transaction + endpoint. Devoluciones fold in here as NCX rather than
   forming their own slice.
4. **Anulación + ajuste manual de stock** (the inverse-movement paths).
5. **POS screen** in `Ways.Web` (likely splittable into cart and payment/ticket).

Apply the Review Workload Forecast discipline (400-line budget; exact guard lines
`Decision needed before apply`, `Chained PRs recommended`, `400-line budget risk`).
Delivery is chained PRs **stacked-to-main** per `protocolo-pr-solo-dev` and the stage-3/4
precedent, with `judgment-day` before every PR.
