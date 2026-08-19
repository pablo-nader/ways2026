# Explore — Stage 15: Cuenta corriente de proveedores (ledger)

Fecha: 2026-08-17. Fase ejecutada por sdd-explore (sonnet) bajo mandato autónomo; contenido
persistido verbatim por el orquestador (el agente de fase no tenía Write en su toolset).

## 1. Current state (derived saldo)

- Spec: `openspec/specs/saldo-de-proveedor/spec.md` — no table, no cache, no ledger. `Σ compras confirmadas − Σ gastos (categoria=proveedor)`, explicitly declared an approximation (`docs/10-modelo-de-datos.md:832-834`).
- Implementation: `src/Ways.Application/Compras/ServicioDeSaldoDeProveedor.cs` — two aggregate EF queries (confirmed compras; gastos grouped by `id_comprobante_compra`, NULL-key group = unlinked gastos). No writer, no lock.
- Payment mechanism today: `gastos` (`src/Ways.Application/Gastos/ServicioDeGastos.cs`), `categoria=proveedor`, optional `id_comprobante_compra` link (`ExigirCompraLigableAsync` takes `SELECT ... FOR SHARE` on the compra header, never locks `proveedores`).
- Debt-increasing event: `ServicioDeCompras.ConfirmarAsync` (`src/Ways.Application/Compras/ServicioDeCompras.cs`) locks the compra header `FOR UPDATE` only, never `proveedores`.
- Endpoint: `GET /api/proveedores/{id}/saldo` (`src/Ways.Api/Endpoints/ProveedoresEndpoints.cs`), mapped top-level (bypassing the `GestionDeCatalogo` group) specifically so `Politicas.OperacionDePos` gates it.
- Web: `src/Ways.Web/src/componentes/ResumenSaldoDeProveedor.tsx` (shared by `Proveedores.tsx` and `Compras.tsx`).
- `proveedores` (`docs/10-modelo-de-datos.md` §2) has **no** `saldo`, **no** `limite_credito`/`credito_ilimitado` equivalent.
- Legacy check: `alsina/` E8 Proveedores (`docs/01-features-existentes.md:336-339`) is a bare ABM (nombre/razón social/CUIT only) — **no supplier CC ledger ever existed in legacy**. Etapa 15 mirrors etapa 7's *design pattern*, not legacy behavior.

## 2. What stage 7 mirrors exactly (precedent: `openspec/changes/archive/2026-08-05-stage-7-cuenta-corriente/`)

- `movimientos_cuenta_corriente` (doc-10 §8): append-only ledger, no `EntidadBase` inheritance, `saldo_resultante` snapshot never re-derived.
- `EscriturasDeCuentaCorriente` (`src/Ways.Application/CuentaCorriente/EscriturasDeCuentaCorriente.cs`): the ONE raw `UPDATE clientes SET saldo=saldo+$1 ... RETURNING` + the ONE raw `INSERT ... RETURNING id_movimiento`, called by every writer — never duplicated.
- Pinned total lock order: `turnos_caja → clientes → ledger INSERT`.
- Pago a cuenta (`RC`) reuses the sale/comprobante machinery (numeración, turno guard, arqueo) instead of a bare ledger INSERT, specifically to avoid a cash-invisible-to-arqueo gap.
- Reliquidación (`ReliquidadorDeConsumos` + `ServicioDeReliquidacion`): pure Domain re-pricer, one formula shared by preview/commit, self-FK marker (`id_movimiento_actualizacion`) instead of boolean, partial eligibility index, 500-row cap, zero-delta no-op.
- FK/index shape (`src/Ways.Infrastructure/Persistencia/Configuraciones/MovimientoCuentaCorrienteConfiguration.cs`): AK `(Id, IdTenant)`, one plain tenant index, composite `(FK, IdTenant)` index per composite FK, simple index for `id_empleado` (deliberately non-composite — a composite AK on `usuarios` would force `id_tenant NOT NULL`, breaking the platform-staff NULL sentinel).
- Authorization split: pago + estado de cuenta reads → `OperacionDePos`; reliquidación + ajuste manual → new `SupervisionDeCuentaCorriente` (Supervisor+Admin) — the stage's one deliberate parity departure.

**What does NOT transplant to proveedores:**

- Doc-11's etapa-15 alcance/decisiones abiertas never mention reliquidación — they mention retenciones/notas de crédito instead. Reliquidación exists for clients because unpaid fiado debt *owed by* clients loses value to inflation; a business's payable *to* a supplier has no symmetric concern. This should be confirmed explicitly in the proposal, not assumed.
- No `limite_credito`/`credito_ilimitado`/`id_lista_precio` equivalent on `proveedores` — stage 7's credit-limit and re-pricing machinery both key off columns proveedores doesn't have.
- The debt-increasing event is structurally different: stage 7's `Consumo` fires only when a sale's medio is `cuenta_corriente` (opt-in per sale); etapa 15's analog (`ConfirmarAsync`) has no "medio" concept — every confirmed compra is implicitly on account until a gasto pays it.
- No RC-comprobante precedent fits the payment side: RC's value was reusing turno/arqueo/numeración. `gastos` is already turno-scoped and already counted in `CalculadorDeArqueo`'s egress terms — the natural reuse target is `ServicioDeGastos`, not a new comprobante type (this is exactly doc-11 decisión abierta #2).

## 3. Tentative DB model (for the DB Change Gate — NOT approved, exploration only)

**New table `movimientos_cuenta_corriente_proveedor`** — scoping category: **operativa** (`id_tenant` + `id_punto_venta`, doc 09), same category as `movimientos_cuenta_corriente`/`gastos`/`comprobantes_compra`.

```sql
movimientos_cuenta_corriente_proveedor (
    id_movimiento        integer GENERATED BY DEFAULT AS IDENTITY,
    id_tenant             integer NOT NULL,
    id_proveedor          integer NOT NULL,
    fecha                 timestamptz NOT NULL,
    id_punto_venta        integer NOT NULL,
    id_empleado           integer NOT NULL,
    tipo                  tipo_movimiento_cc_proveedor NOT NULL,  -- new enum: compra | pago | ajuste (+ TBD)
    id_comprobante_compra integer NULL,   -- generated the "compra" movement
    id_gasto              integer NULL,   -- generated the "pago" movement (IF gastos stays the payment path)
    importe               numeric(14,2) NOT NULL,   -- signed: + increases debt, - reduces it
    saldo_resultante      numeric(14,2) NOT NULL,
    detalle               text NULL
);
-- pk_movimientos_cuenta_corriente_proveedor (id_movimiento)
-- ak_movimientos_cuenta_corriente_proveedor_id_movimiento_id_tenant (id_movimiento, id_tenant)
```

No `EntidadBase` — append-only, mirrors `movimientos_cuenta_corriente`/`movimientos_stock`.

**FKs and their ForeignKeyIndexConvention-backed support indexes (counted, mirroring `MovimientoCuentaCorrienteConfiguration.cs` 1:1):**

| # | FK | Shape | Support index |
|---|---|---|---|
| 1 | `fk_..._tenant` | simple → `tenants` | `ix_..._tenant` (IdTenant) |
| 2 | `fk_..._proveedor` | composite `(id_proveedor, id_tenant)` → `proveedores (id_proveedor, id_tenant)` | `ix_..._proveedor` (IdProveedor, IdTenant) — also the estado-de-cuenta listing index (would want `fecha` trailing, mirroring `ix_movimientos_cuenta_corriente_cliente_fecha`) |
| 3 | `fk_..._punto_venta` | composite `(id_punto_venta, id_tenant)` → `puntos_venta` | `ix_..._punto_venta` (IdPuntoVenta, IdTenant) |
| 4 | `fk_..._empleado` | simple → `usuarios` (non-composite, same reason as `fk_movimientos_cuenta_corriente_empleado`) | `ix_..._empleado` (IdEmpleado) |
| 5 | `fk_..._comprobante_compra` | composite `(id_comprobante_compra, id_tenant)` → `comprobantes_compra`, nullable | `ix_..._comprobante_compra` (IdComprobanteCompra, IdTenant) |
| 6 | `fk_..._gasto` (only if decisión #2 keeps `gastos` as payment path) | composite `(id_gasto, id_tenant)` → `gastos`, nullable — requires `gastos` to gain AK `(Id, IdTenant)` if absent | `ix_..._gasto` (IdGasto, IdTenant) |

Count: **5 FKs / 5 dedicated support indexes** either way (row 5 or row 6 swaps depending on decisión #2), plus the AK — same shape discipline as the client table's 6.

**Additive column `proveedores.saldo`:**

```sql
ALTER TABLE proveedores ADD COLUMN saldo numeric(14,2) NOT NULL DEFAULT 0;
```

Mirrors `clientes.saldo`. **Requires backfill**, unlike stage 7's marker column (purely additive, no history to reconcile). Every tenant with existing compra/gasto history has a non-zero derived saldo today — the migration needs either (a) one opening `ajuste` movement per proveedor computed via the exact `saldo-de-proveedor` formula, or (b) full replay of compra/gasto history as synthetic `compra`/`pago` movements (doc-11 decisión abierta #1). Backfill correctness is provable against the existing `saldo-de-proveedor` spec scenarios.

**New Postgres enum** `tipo_movimiento_cc_proveedor` — cannot reuse `tipo_movimiento_cc` (carries `actualizacion_precios`, meaningless unless reliquidación is confirmed in scope).

## 4. Risks / open decisions for the proposal

1. **Saldo migration strategy** (doc-11 #1): opening ajuste (cheap, loses per-movement provenance) vs. full replay (auditable, much larger migration than stage 7 ever needed — stage 7 shipped with zero backfill).
2. **Does `gastos` stay the payment mechanism, or does a dedicated payment write path replace it?** (doc-11 #2). Keeping `gastos` avoids re-deriving turno/arqueo integration but couples the ledger writer into `ServicioDeGastos.InsertarGastoAsync` rather than a clean parallel service.
3. **Confirm reliquidación is OUT of scope for proveedores** — current read of doc-11 alcance/decisiones abiertas: yes, out. If confirmed, the new enum should not reserve an unused value speculatively.
4. **Retenciones y notas de crédito de proveedor** (doc-11 #3) — drives whether the enum ships with 3 or more `tipo` values at launch.
5. **Lock order extension.** Neither `ServicioDeCompras.ConfirmarAsync` (compra `FOR UPDATE` only) nor `ServicioDeGastos`' compra-link path (compra `FOR SHARE` after turno `FOR SHARE`) currently locks `proveedores`. Adding a proveedor-row lock requires pinning a consistent order — candidate: `turno → compra header → proveedor → ledger insert` — verified consistent across both existing call sites, but must be pinned explicitly in design.md the way stage 7 pinned `turno → cliente → ledger`.
6. **Coexistence with existing anulación.** `ServicioDeCompras`'s anulación reverses stock but explicitly does **not** revert linked `gastos` (`docs/10-modelo-de-datos.md:465-466`, "sin motor de reversión de gastos"). A ledger movement written at confirm time would need a symmetric contramovimiento on anulación — new coupling `ServicioDeCompras` doesn't have today (the pattern exists in `ServicioDeVentas.AnularAsync` for clients, just not wired for compras).
7. **Concurrency surfaces to test**, mirroring stage 7's three: compra-confirm × payment on the same proveedor, two concurrent payments to the same proveedor, and compra-anulación × payment race if #6 is in scope.
8. **`estado_compra` doesn't need a partial-payment state** — payment status is already a derived read (`EstadoPago` in `ServicioDeSaldoDeProveedor`), and should stay derived post-ledger, now sourced from ledger movements instead of `gastos` directly.

## 5. Web/API surfaces affected

- `GET /api/proveedores/{id}/saldo` — becomes a ledger read; response shape likely stays compatible if payment-status derivation is preserved, now sourced from the ledger. Route stays top-level under `OperacionDePos` (stage-8 decision to preserve). `ResumenSaldoDeProveedor.tsx` is downstream.
- New: an estado-de-cuenta-equivalent endpoint/screen (movement history + running balance), mirroring `GET /api/clientes/{id}/cuenta-corriente` and `CuentaCorriente.tsx` — doc-11 alcance explicitly asks for "historial consultable".
- `src/Ways.Application/Gastos/ServicioDeGastos.cs` — if decisión #2 keeps `gastos`, `InsertarGastoAsync` gains the ledger write.
- `src/Ways.Application/Compras/ServicioDeCompras.cs` — `ConfirmarAsync` (and possibly `AnularAsync`, risk #6) gain ledger writes.
- `src/Ways.Api/Seguridad/Politicas.cs` — `SupervisionDeCuentaCorriente` is currently scoped semantically to client CC by every caller; reusing it for a proveedor-side ajuste manual is a policy-semantics stretch to flag explicitly, not a free reuse, even though its doc-comment says it was named generically for future tightening.
- Specs needing amendment: `openspec/specs/saldo-de-proveedor/spec.md` (its core requirement "Saldo Is A Derived Read, Never Persisted" is directly superseded — must be explicitly retired, the way stage 7 removed `consumo-cuenta-corriente`'s "No Reliquidación..." requirement), `openspec/specs/proveedores/spec.md`, `openspec/specs/gastos/spec.md`, `openspec/specs/comprobantes-compra/spec.md`.

## Ready for Proposal

Yes, with three decisions that should be resolved or explicitly deferred with rationale before/at proposal time: (1) saldo migration strategy (opening ajuste vs. full replay); (2) gasto-stays-the-payment-path vs. new payment write path; (3) confirm reliquidación is out of scope for proveedores.

## Orchestrator Decisions (mandato autónomo, 2026-08-17 — a formalizar por el proposal)

Ninguna de estas decisiones está en la lista de pendientes reservados del dueño; el
orquestador las resuelve bajo el mandato autónomo y el proposal debe formalizarlas con
opciones/tradeoffs/costo de revertir (o refutarlas con evidencia):

1. **Migración del saldo: asiento de apertura por proveedor** (opción a), calculado con la
   fórmula EXACTA del spec `saldo-de-proveedor` vigente, tipo `ajuste`, `detalle` que
   documenta la derivación. NO replay sintético: el historial nunca fue un ledger y
   fabricar movimientos `compra`/`pago` retroactivos inventa procedencia que no existió.
   Verificable contra los escenarios del spec actual.
2. **El pago sigue siendo `gastos`**: ya está turno-scoped y visible al arqueo — el mismo
   racional por el que la etapa 7 hizo del RC un comprobante. El writer del ledger se
   acopla a `ServicioDeGastos` (y la FK nullable es `id_gasto`, fila 6 de la tabla de FKs;
   verificar/agregar AK `(Id, IdTenant)` en `gastos` como parte del gate).
3. **Reliquidación FUERA de alcance** para proveedores: doc 11 no la pide y no hay simetría
   económica (la deuda es nuestra, no pierde valor para nosotros con inflación). El enum
   NO reserva valores especulativos.
4. **Retenciones y notas de crédito: DIFERIDAS** con registro explícito — enum
   `compra | pago | ajuste` al lanzamiento; PG soporta `ALTER TYPE ... ADD VALUE` (aunque
   irreversible, agregar un valor cuando exista el caso real es el camino barato; retirar
   uno especulativo no existe). Nota: si el proposal prefiere `text + CHECK` como la
   etapa 14 (auditoría), que lo argumente contra el precedente enum de la 7 y elija UNO.
5. **Contramovimiento en anulación de compra: EN alcance** — un ledger que diverge de la
   verdad derivada en la primera anulación nace roto. "Sin motor de reversión de gastos"
   se mantiene (los gastos no se revierten; el contramovimiento revierte la DEUDA de la
   compra anulada).
