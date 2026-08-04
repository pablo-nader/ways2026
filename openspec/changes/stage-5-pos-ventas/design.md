# Design: Stage 5 — POS y ventas

## Technical Approach

Reuse, not redesign (proposal §Approach). The stage adds three **write paths** — emisión de
comprobante, movimiento de stock, consumo de cuenta corriente — that share exactly one
transaction. Everything expensive and every decision that involves money happens **before**
that transaction, as pure computation over already-fetched data; the transaction itself only
executes a pre-computed, immutable `PlanDeVenta` with a fixed statement order.

**Key insight that shapes the whole stage:** a sale is not "one service doing six things", it
is *decide, then commit*. Pricing, oferta resolution, parámetros, payment validation and CC
gating are **reads + pure rules** resolved once, outside the transaction, against a pinned
`momento`. If any of them ran inside the retryable transaction lambda, an execution-strategy
retry could produce a **different total than the one the payment mix was validated against** —
the customer would be charged an amount nobody validated. So the transaction receives a frozen
plan and does nothing but write it. That split is what makes the atomicity tests meaningful and
keeps the checkout query budget **constant in the number of lines** (≤ 16 round trips).

The second insight: every mutable row this stage touches is written with a **single atomic
statement that both takes its own row lock and returns the post-state** (`UPDATE … RETURNING`,
`INSERT … ON CONFLICT DO UPDATE … RETURNING`). No read-modify-write anywhere in the sale. That
is what removes the need for advisory locks (decision 1) and what makes `stock.cantidad` and
`clientes.saldo` provably equal to the sum of their ledgers.

## Architecture Decisions

| # | Decision | Alternatives considered | Rationale |
|---|---|---|---|
| 1 | **No advisory locks in the sale.** Stock is written with `INSERT INTO stock … ON CONFLICT (id_articulo, id_punto_venta) DO UPDATE SET cantidad = stock.cantidad + $delta RETURNING cantidad`; `clientes.saldo` with `UPDATE clientes SET saldo = saldo + $delta … RETURNING saldo`; the número with the doc-09 `UPDATE … RETURNING`. Each statement takes the row lock it needs, atomically, in one round trip | `pg_advisory_xact_lock` per `(tenant, articulo, punto_venta)` as the proposal's Approach §3 binds (the `ServicioDePrecios`/`ServicioDeOfertas` pattern) | The advisory lock exists in stages 3–4 because those services **read, decide, then write** (a row may not exist yet, so `FOR UPDATE` had nothing to lock). Here nothing is decided from the read: the delta is already known, so the write itself is the whole operation and its row lock is strictly narrower and shorter. **DEVIATION from proposal Approach §3 — declared at the DB Change Gate.** If the gate prefers the advisory lock it is purely additive (one `SELECT pg_advisory_xact_lock` before each upsert, same ascending order, no other change) |
| 2 | **Lock order pinned: numeración → stock ascendente por `id_articulo` → cliente** (proposal binding, kept) | Numeración **last**, to shorten the window in which a punto de venta is serialized | Ascending `id_articulo` is non-negotiable: the upsert row locks of decision 1 are implicit, so two sales sharing articles {A,B} and {B,A} would deadlock without a total order. Numeración-first costs throughput (the whole sale serializes per `(punto de venta, tipo)`), and that cost is accepted: a punto de venta is one cashier at one terminal, sales are sequential by nature, and "the first statement of every sale is the allocator" is an invariant a reviewer can audit in one glance. Cliente is last because it is optional (CC only) and the least contended |
| 3 | **`ServicioDeVentas` receives ids and quantities; every money value is computed server-side.** `LineaDeVenta(IdArticulo, Cantidad, CodigoBarra?)` — no `precioUnitario`, no `descuento`, no `total` in the request | Trust the prices the POS already displayed (one fewer resolution round trip) | This is the legacy's central defect class (doc-01 D7 ⚠: caja totals copied from a manipulable POST). The client's displayed price is a **preview**; the authoritative price is `ServicioDeOfertas.ResolverAsync` re-run server-side at checkout. A price that changed between cart and checkout produces a different total, which the payment validator then rejects or accepts on its own merits — never a total the client dictated |
| 4 | **NCX carries negative `cantidad` in its items** (proposal decision 9); `tipos_comprobante.signo` **validates** the sign instead of flipping it (`ReglaDeComprobantes`: TX ⇒ every `cantidad > 0`, NCX ⇒ every `cantidad < 0`) | Positive quantities everywhere + `signo` applied at read time | With signed lines, totals, stock movements and CC movements are all uniform (`total` negative, movimiento `= −cantidad`, CC `importe = total`) and `Σ comprobantes.total` is the net figure with no interpretation layer. `signo` keeps a real job (a rule that rejects a TX with negative lines) instead of becoming redundant metadata |
| 5 | **Pure Domain first**: `ValidadorDePagos`, `CalculadorDeTotales`, `ReglaDeComprobantes`, `ParserDeEscaneo`, `ResolvedorDeLetraComprobante` (dormant) are DB-free statics in `Ways.Domain.Ventas` | Validation inside `ServicioDeVentas`, tested through HTTP | Same bar as `ResolvedorDeOfertas`/`ResolvedorDePrecios`/`PoliticaDeRoles`. The legacy's B6 rejection **order** is observable behaviour (which message a doubly-invalid payload gets), so it must be pinned by exhaustive unit tests, not by integration luck |
| 6 | **POS read surface: the group gate becomes `OperacionDePos` and every write endpoint stacks `.RequireAuthorization(GestionDeCatalogo)`** | Split each resource into a read `MapGroup` and a write `MapGroup` | ASP.NET Core **composes** authorization metadata with AND — an endpoint-level policy can only *narrow* its group's, never relax it. So relaxing reads requires the group to carry the looser policy; stacking the admin policy on writes reproduces today's behaviour exactly (`OperacionDePos ∧ GestionDeCatalogo` = Admin). Two groups per resource would duplicate the prefix, split the OpenAPI tag and double the surface to audit. **Omission risk is covered by a test, not by discipline** (decision 10) |
| 7 | **Scan resolution is server-side and returns identity only** — `GET /api/articulos/escaneo?entrada=3*7790001`, `ParserDeEscaneo` (pure) + one query. It does **not** price | Parse in the browser; or return price + oferta in the same response | Rule I.2 (`< 7` ⇒ `codigo_interno`, `>= 7` ⇒ `codigos_barra`) is a specified business rule, so it must be identical for every client. Pricing stays on the single path (`POST /api/ofertas/resolver`) because `cantidad_minima` ofertas depend on the **whole line quantity** — a re-scan that sums quantity invalidates the previous price anyway, so a per-scan price would be wrong half the time |
| 8 | **`numeraciones_comprobante` keeps doc 09's PK `(id_punto_venta, tipo_comprobante)`** with `id_tenant` as a non-key column for RLS, and `tipo_comprobante varchar(30)` holding `tipos_comprobante.codigo` | PK `(id_tenant, id_punto_venta, tipo_comprobante)`; or `id_tipo_comprobante integer` FK | `id_punto_venta` is a **global** identity, so the doc-09 PK is already globally unique; adding `id_tenant` would be redundant (same shape as `pk_ofertas_listas`, which carries `id_tenant` for RLS/FKs but keeps it out of the PK). A composite FK to `puntos_venta (id_punto_venta, id_tenant)` makes the denormalized tenant unable to lie. `varchar(30)` over an FK because doc 09 explicitly numbers non-comprobante things (`retiro`, `cierre_caja` — stage 6) in this same table; the codigo is never client input (it comes from a `TipoComprobante` row already loaded) |
| 9 | **The counter row is created lazily, inside the sale transaction** (`INSERT … ON CONFLICT DO NOTHING`, then `UPDATE … RETURNING`), exactly like `AsignadorDeNumeroCliente` | Seed counters when a punto de venta is created (`ServicioDeOrganizacion`) | Seeding would couple stage-1 code to stage-5 concepts and would need a backfill for every punto de venta already created. One extra PK-hit statement per sale is cheaper than that coupling |
| 10 | **`ServicioDeEscaneo` + `ServicioDeVentas` are dedicated Application services**; `ServicioDeArticulos` (ABM) is not extended | Add POS methods to `ServicioDeArticulos` | Same divergence `ServicioDeArticulos`/`ServicioDePrecios`/`ServicioDeOfertas` already took: an ABM service and an operational service have different authorization, different transactions and different query shapes |
| 11 | **`id_empleado` = `IContextoDeUsuario.UsuarioId`**, composite FK to `usuarios (id_usuario, id_tenant)` | Create an `empleados` table now | No `empleados` table exists in stages 1–4 and doc 10 does not define one; the operator of a sale *is* the authenticated user. Recorded as an Open Question for the day payroll/employee data is modelled |
| 12 | **Dedicated `Pos.tsx` screen**; the cart is a **pure reducer** in `src/api/carrito.ts`, payment math a pure module in `src/api/pagos.ts` | Build the cart with `useState` mutations inside the component | `web-descriptor-tests`' bar is the *helper*, not the descriptor literally (stage-4 precedent). A cart is the one piece of this screen with genuine branching logic (re-scan sums quantity, `N*` prefix, negative lines for NCX) — extracting it is what makes it testable without the DOM, and what keeps `react-async-state` rule 1 (build from `prev`) structurally true |

## Table Shapes (DB CHANGE GATE — grouped by WRITE PATH, not by table)

The gate is presented as **one approval covering both migrations** (decision: migration
sequencing) because the user must see the whole model at once (CLAUDE.md).

### Write path A — Emisión de comprobante

| Table | Scope | Key columns | Constraints |
|---|---|---|---|
| `numeraciones_comprobante` | operativa (`id_tenant` + `id_punto_venta`) | `tipo_comprobante varchar(30)`, `proximo_numero bigint NOT NULL DEFAULT 1` | `pk_numeraciones_comprobante (id_punto_venta, tipo_comprobante)`; `fk_numeraciones_comprobante_punto_venta (id_punto_venta, id_tenant)`, `fk_numeraciones_comprobante_tenant`; `ix_numeraciones_comprobante_tenant` |
| `comprobantes_venta` | operativa | `id_tipo_comprobante int`, `numero bigint`, `fecha timestamptz`, `id_punto_venta`, `id_turno_caja int NULL` (**always NULL in stage 5**, decision 1 of the proposal), `id_empleado`, `id_cliente`, `id_comprobante_asociado int NULL`, `subtotal/descuento_total/total numeric(14,2)`, `neto_gravado/iva_total numeric(14,2) NULL`, `direccion_entrega/observaciones text NULL`, `estado estado_comprobante` | `pk_comprobantes_venta`; `ak_comprobantes_venta_id_id_tenant`; `ux_comprobantes_venta_numero (id_punto_venta, id_tipo_comprobante, numero)`; `ck_comprobantes_venta_numero_positivo (numero > 0)`; FKs `fk_comprobantes_venta_{tenant,punto_venta,cliente,empleado,tipo_comprobante,comprobante_asociado}` (composite with `id_tenant` except `tipo_comprobante`, which is global/ADR-11); `ix_comprobantes_venta_{tenant,punto_venta_fecha,cliente,asociado}` |
| `items_comprobante_venta` | child (`id_tenant` only — **no** `id_punto_venta`) | `id_comprobante_venta`, `orden int`, `id_articulo int NULL`, **snapshot**: `descripcion text`, `codigo_barra text NULL`, `id_area`, `id_lista_precio`, `id_oferta int NULL`, `id_alicuota_iva`, `porcentaje_iva numeric(5,2)`, `cantidad numeric(12,3)`, `precio_unitario numeric(14,2)`, `descuento numeric(14,2) NOT NULL DEFAULT 0`, `total numeric(14,2)` | `pk_items_comprobante_venta`; `ux_items_comprobante_venta_orden (id_comprobante_venta, orden)`; FKs `fk_items_comprobante_venta_{tenant,comprobante,articulo,area,lista_precio,oferta,alicuota_iva}`; `ix_items_comprobante_venta_{tenant,comprobante,articulo}` |
| `pagos_comprobante` | child (`id_tenant` only) | `id_comprobante_venta`, `id_medio_pago`, `importe numeric(14,2)`, `referencia text NULL`, `vuelto numeric(14,2) NOT NULL DEFAULT 0` | `pk_pagos_comprobante`; `ak_pagos_comprobante_id_id_tenant` (referenced by the CC movimiento); `ck_pagos_comprobante_vuelto_no_negativo (vuelto >= 0)`; FKs `fk_pagos_comprobante_{tenant,comprobante,medio_pago}`; `ix_pagos_comprobante_{tenant,comprobante,medio_pago}` |

Children carry `id_tenant` (RLS needs it on every table) but **not** `id_punto_venta`: it is
derivable from the parent, and duplicating it would invite drift with no query that needs it.
Same shape as `ofertas_listas`.

### Write path B — Movimiento de stock

| Table | Scope | Key columns | Constraints |
|---|---|---|---|
| `stock` | operativa | `id_articulo`, `id_punto_venta`, `cantidad numeric(12,3) NOT NULL DEFAULT 0` (**cache of the ledger**), `minimo/reposicion numeric(12,3) NULL` | `pk_stock (id_articulo, id_punto_venta)`; FKs `fk_stock_{tenant,articulo,punto_venta}`; `ix_stock_{tenant,punto_venta}`. **No CHECK on `cantidad`** — negative stock is allowed (proposal decision 7) |
| `movimientos_stock` | operativa | `id_articulo`, `id_punto_venta`, `cantidad numeric(12,3)` (signed), `motivo motivo_stock`, `id_comprobante_venta NULL`, `id_comprobante_compra NULL` (**column deferred to stage 8 — not created now**), `id_punto_venta_destino NULL` (created, never written), `id_empleado`, `observaciones text NULL`, `creado_el timestamptz` | `pk_movimientos_stock`; `ck_movimientos_stock_cantidad_no_cero (cantidad <> 0)`; FKs `fk_movimientos_stock_{tenant,articulo,punto_venta,punto_venta_destino,comprobante_venta,empleado}`; `ix_movimientos_stock_{tenant,articulo_punto_venta,comprobante_venta}`. **Append-only by contract**: no endpoint updates or deletes a movimiento, ever |

`id_comprobante_compra` is **not** created in stage 5: `comprobantes_compra` does not exist, so
the FK cannot be declared and a bare nullable int would be an unconstrained lie. Stage 8 adds
the column and its FK together (declared deviation from doc 10 §6).

### Write path C — Consumo de cuenta corriente

| Table | Scope | Key columns | Constraints |
|---|---|---|---|
| `movimientos_cuenta_corriente` | operativa | `id_cliente`, `fecha timestamptz`, `id_punto_venta`, `id_empleado`, `tipo tipo_movimiento_cc`, `id_comprobante_venta NULL`, `id_pago_comprobante NULL`, `importe numeric(14,2)` (+ aumenta deuda), `saldo_resultante numeric(14,2)`, `detalle text NULL` | `pk_movimientos_cuenta_corriente`; FKs `fk_movimientos_cuenta_corriente_{tenant,cliente,punto_venta,empleado,comprobante_venta,pago_comprobante}`; `ix_movimientos_cuenta_corriente_{tenant,cliente_fecha,comprobante_venta}` |

`clientes.saldo` becomes the **maintained cache** of this ledger, updated in the same statement
that returns `saldo_resultante`. Cache, not aggregation (proposal decision 5, confirmed here):
the credit check must be O(1) inside the transaction, and the invariant
`clientes.saldo = Σ movimientos_cuenta_corriente.importe` is proved by test, exactly like
`stock.cantidad`.

### Enums (three, not two)

`estado_comprobante` (`emitido | anulado`), `motivo_stock`
(`venta | compra | anulacion | ajuste | transferencia | inventario` — only the first, third and
fourth get a write path), `tipo_movimiento_cc`
(`consumo | pago | ajuste | actualizacion_precios` — only `consumo` gets a write path).
**Correction to the proposal**, which counted two: the CC table ships with its own enum. Native
Postgres enums, same criterion as `comportamiento_medio_pago`/`estado_tenant`.

## The Sale Transaction (binding statement order)

```
── outside the transaction (reads + pure rules, executed exactly once) ───────────
  momento := reloj.Ahora                        (pinned; never re-read on retry)
  tipo   := TipoComprobante(codigo)             activo, clase venta, es_fiscal = false
  cliente, puntoVenta (→ id_empresa)            tenant-scoped, ADR-8 404
  resolución := ServicioDeOfertas.ResolverAsync(lineas, momento)      ← 7 queries
  snapshot   := articulos + codigos_barra + alicuotas                 ← 2 queries
  items      := CalculadorDeTotales.Materializar(...)                 ← pure
  parámetros := tolerancia_pago, vuelto_maximo (PV > empresa > default) ← 2 queries
  medios     := MedioPago[] de los pagos pedidos                      ← 1 query
  ValidadorDePagos.Validar(total, pagos, medios, tolerancia, vueltoMax,
                           esConsumidorFinal, creditoDisponible)      ← pure, legacy order
  plan := PlanDeVenta(immutable)
── estrategia.ExecuteAsync(async () => { BeginTransaction ────────────────────────
  1. INSERT INTO numeraciones_comprobante … ON CONFLICT DO NOTHING
     UPDATE numeraciones_comprobante SET proximo_numero = proximo_numero + 1
       WHERE id_punto_venta = $1 AND tipo_comprobante = $2
       RETURNING proximo_numero - 1                     ← row lock, held to COMMIT
  2. INSERT comprobantes_venta (estado = emitido)  →  id
  3. INSERT items_comprobante_venta   (orden 1..N, snapshot values)
  4. INSERT pagos_comprobante         (→ id_pago, needed by step 6)
  5. FOR EACH línea ORDER BY id_articulo ASC:          ← total order ⇒ no deadlock
       INSERT movimientos_stock (cantidad = −item.cantidad, motivo = venta)
       INSERT INTO stock … ON CONFLICT DO UPDATE
         SET cantidad = stock.cantidad + $delta RETURNING cantidad
  6. IF pago con comportamiento = CuentaCorriente:
       UPDATE clientes SET saldo = saldo + $importe … RETURNING saldo
       IF NOT credito_ilimitado AND saldo > limite_credito → throw (rollback)
       INSERT movimientos_cuenta_corriente (tipo = consumo, saldo_resultante = ←)
  COMMIT }) ─────────────────────────────────────────────────────────────────────
```

**Retry contract.** `CreateExecutionStrategy` wraps the whole transaction (stage-3/4 precedent).
The lambda builds **every EF entity from the immutable plan on each attempt** — never reuses an
instance `Add`ed by a failed attempt, and never mutates an entity loaded outside (EF does not
untrack `Added` entities after a rollback, and a tracked `cliente.Saldo += x` would double on
retry; step 6 is raw ADO precisely for that reason). Advisory-lock-free by decision 1, so there
is nothing to leak; all row locks die with the rollback. **A retry consumes another número** —
gaps are accepted (TX/NCX are non-fiscal), duplicates are not.

**Failure semantics**: any throw between steps 1 and 6 leaves *nothing* persisted — except the
número, which is consumed by design (the counter is not rolled back to a previous value; it
simply advances). This is the honest expected outcome and the atomicity tests assert exactly it.

## Checkout Contract

```csharp
record SolicitudDeVenta(
    int IdPuntoVenta, int IdCliente, string CodigoTipoComprobante,   // "TX" | "NCX"
    int? IdComprobanteAsociado,
    IReadOnlyList<LineaDeVenta> Lineas, IReadOnlyList<PagoDeVenta> Pagos,
    string? DireccionEntrega, string? Observaciones);

record LineaDeVenta(int IdArticulo, decimal Cantidad, string? CodigoBarra);   // sin precios
record PagoDeVenta(int IdMedioPago, decimal Importe, string? Referencia, decimal Vuelto);
```

`ValidadorDePagos` rejection order (legacy B6 parity, parametrized — **no literal `10`/`20`
anywhere in the codebase**):

| # | Legacy rule | Domain code |
|---|---|---|
| 1 | todos los medios en 0 y total > 0 | `pago_no_ingresado` |
| 2 | `Σ importe + tolerancia < total` | `tolerancia_de_pago_superada` |
| 3 | `Σ vuelto > vuelto_maximo` | `vuelto_excedido` |
| 4+5 | vuelto sobre un medio con `AdmiteVuelto = false` (generaliza "tarjetas" y "cuenta corriente") | `medio_no_admite_vuelto` |
| — | cuenta corriente con Consumidor Final | `cuenta_corriente_no_permitida` |
| 6 | `saldo + consumo > limite_credito` y no `credito_ilimitado` | `limite_credito_excedido` |
| new | `RequiereReferencia` sin referencia | `referencia_de_pago_requerida` |
| new | `Σ vuelto > max(0, Σ importe − total)` | `vuelto_invalido` |

`CalculadorDeTotales` (rounding order pinned, `MidpointRounding.AwayFromZero` — same POS
criterion as `ResolvedorDeOfertas`): `descuento = round(descuentoUnitario × cantidad, 2)`;
`item.total = round(cantidad × precio_unitario, 2) − descuento`; `subtotal = Σ round(cantidad ×
precio_unitario, 2)`; `descuento_total = Σ descuento`; `total = subtotal − descuento_total`, and
`total == Σ item.total` is asserted (that redundancy is doc 10's "verificados por dominio").
`neto_gravado`/`iva_total` stay NULL while `discrimina_iva = false`.

## Protection Rules

| Rule | Enforcement today | DB-level |
|---|---|---|
| Client never sets a price | contract shape (`LineaDeVenta` has no money field) + server re-resolution | — |
| Payment mix valid, in legacy order | `ValidadorDePagos` (pure) before the transaction | — |
| Credit limit not exceeded under concurrency | `UPDATE … RETURNING saldo` + post-check inside the transaction (the pre-check outside is best-effort UX) | — |
| `stock.cantidad = Σ movimientos_stock` | movimiento + upsert in the same transaction, single statement each | — |
| `clientes.saldo = Σ movimientos_cc` | idem | — |
| Sign of lines matches `tipos_comprobante.signo` | `ReglaDeComprobantes` (pure) | — |
| `id_comprobante_asociado` only on NCX, pointing at an `emitido` TX of the same punto de venta and cliente | `ServicioDeVentas` tenant-scoped check | `fk_comprobantes_venta_comprobante_asociado` |
| A comprobante is anulado at most once | `UPDATE … SET estado = 'anulado' WHERE estado = 'emitido'`; 0 rows ⇒ 409 `comprobante_ya_anulado` | `estado_comprobante` enum |
| Movimientos are never edited | no endpoint exists; `restaurar` is not implemented, ever | append-only by contract |
| Número never client-supplied | `AsignadorDeNumeroComprobante` is the only writer | `ux_comprobantes_venta_numero` |

## Authorization Surface

`Politicas.OperacionDePos` = `RolConocido.Vendedor` + `RolConocido.Admin` (Root excluded, same
criterion as `GestionDeCatalogo`: "root administra tenants, no opera ninguno").

| Group | Group gate becomes | Writes stack |
|---|---|---|
| `/api/articulos` (incl. `/{id}/precios`, `/{id}/codigos-barra`, `/escaneo`) | `OperacionDePos` | `GestionDeCatalogo` on POST/PUT/DELETE |
| `/api/clientes`, `/api/catalogos/{recurso}`, `/api/catalogos-fiscales`, `/api/parametros` | `OperacionDePos` | idem |
| `/api/ofertas` | `OperacionDePos` | `GestionDeCatalogo` on POST `/`, PUT, DELETE — **not** on POST `/resolver` (closes the stage-4 carryover) |
| `/api/ventas`, `/api/stock` | `OperacionDePos` | `GestionDeCatalogo` only on `POST /api/stock/ajustes` (admin-only, proposal decision 7) |
| `/api/usuarios`, `/api/proveedores`, `/api/empresas`, `/api/puntos-venta`, `/api/plataforma/*` | unchanged | unchanged |

**Omission guard (mandatory).** A `SuperficieDeAutorizacionTests` walks `EndpointDataSource` and
asserts that every endpoint whose HTTP method is not GET carries `GestionDeCatalogo`, against an
explicit allowlist (`POST /api/auth/*`, `POST /api/ofertas/resolver`, `POST /api/ventas`,
`POST /api/ventas/{id}/anulacion`). A future write endpoint added without the stacked policy
fails this test instead of silently shipping open to a cashier.

**Two shipped tests invert** (their assertion is exactly what stage 5 changes):
`ClientesEndpointsTests.UnVendedorNoPuedeListarListasDePrecio` and
`ArticulosEndpointsTests.UnVendedorNoPuedeListarCodigosDeBarra` become
`…PuedeListar…`. Every other `UnVendedorNoPuede…` test asserts a **write** and must stay red for
a Vendedor — that is the regression net for decision 6.

## API Surface (ADR-8: uniform 404 cross-tenant)

| Endpoint | Policy | Notes |
|---|---|---|
| `POST /api/ventas` | `OperacionDePos` | Checkout. 201 + `Location`, body = comprobante emitido con `numeroVisible` |
| `GET /api/ventas/{id}` | `OperacionDePos` | Reprint — reads the snapshot, never re-joins the catalog |
| `GET /api/ventas` | `OperacionDePos` | Filtros `idPuntoVenta`, `desde/hasta`, `idCliente`, `estado`; paginado |
| `POST /api/ventas/{id}/anulacion` | `OperacionDePos` | **POST, not DELETE**: produces rows (inverse movimientos + contramovimiento CC), it does not remove any. Makes "no `restaurar`" structurally obvious |
| `GET /api/articulos/escaneo?entrada=` | `OperacionDePos` | `ParserDeEscaneo` + 1 query; returns identity, snapshot fields and the parsed `cantidad` |
| `GET /api/stock?idPuntoVenta=&idArticulo=` | `OperacionDePos` | Balance for the POS badge |
| `POST /api/stock/ajustes` | `OperacionDePos` ∧ `GestionDeCatalogo` | Manual `ajuste`; same movimiento+upsert pair, one transaction |

`PPPP-NNNNNNNN` is formatted by a pure `NumeroDeComprobante.Formatear(idPuntoVenta, numero)`.

## Backstop Map (db-error-backstops)

| Constraint | Mapping | Test |
|---|---|---|
| `ux_comprobantes_venta_numero` | 23505 → 409 `numero_de_comprobante_duplicado`. **Ordering trap**: the name contains `_numero`, so `ClasificarUnicidad`'s existing `_numero` branch would misclassify it as `numero_duplicado` ("Ya existe un cliente con ese número"). The new branch MUST be added **before** it — the same trap `_codigo_interno` created in stage 3 | (a) concurrent sales in one punto de venta ⇒ sequential numbers, backstop never fires; (b) raw-SQL duplicate ⇒ SQLSTATE 23505 + translated code |
| `pk_stock`, `pk_numeraciones_comprobante` | 23505 → 409 `stock_duplicado` / `numeracion_duplicada` | **Documented exemption from a race test**: both writes go through `ON CONFLICT`, so a normal path can never raise them. Raw-SQL SQLSTATE test only |
| `ck_comprobantes_venta_numero_positivo`, `ck_pagos_comprobante_vuelto_no_negativo`, `ck_movimientos_stock_cantidad_no_cero` | 23514 → 400 via a new `ClasificarCheckDeVentas`, **exact-name switch** (never `Contains`) appended after `ClasificarCheckDeOfertas` | Raw-SQL INSERT asserting 23514 + translated code |
| All `fk_*` of the seven tables | existing generic `fk_` prefix → 400 `referencia_invalida` — **no code change**, confirmed as in stages 3/4 | Integration: `idCliente`/`idArticulo`/`idMedioPago` of another tenant ⇒ 400, never 500 |
| numeric overflow on totals | existing 22003 → 400 `valor_fuera_de_rango` | Covered by the shipped mapping |
| Double anulación | domain 409 `comprobante_ya_anulado` from the conditional UPDATE (0 rows), **not** `DbUpdateConcurrencyException` | Two concurrent anulaciones ⇒ exactly one 200 + one 409 |

**Reachability, honestly.** Of everything above, only **four** surfaces are genuinely racy in
normal operation, and each gets a rendezvous race test: (1) two sales of the same articulo in
the same punto de venta — `stock.cantidad` must equal `Σ movimientos_stock`; (2) two CC sales of
the same cliente near the limit — the limit must never be exceeded and `saldo` must equal
`Σ movimientos_cc`; (3) two anulaciones of the same comprobante; (4) two sales in the same
punto de venta racing the counter. The CHECKs and the two PKs are schema defense against
raw/out-of-band writes only — same family as `ck_clientes_cf_protegido`.

## Migration Sequencing

**Two migrations, one gate.** `NumeracionDeComprobantesEtapa5` (one table + RLS) ships with
slice 1 so the allocator and its race tests land before anything can emit; `VentasStockYCuentaCorrienteEtapa5`
(six tables + three enums + RLS) ships with slice 2. A single migration for seven tables would
exceed the 400-line review budget on its own. Both are presented to the user in **one** DB
Change Gate approval, grouped by write path (A/B/C above), and the gate summary must call out:
(a) the cross-stage cuenta-corriente pull; (b) `id_turno_caja` always NULL (deviation from the
legacy, proposal decision 1); (c) recargo dormant (proposal decision 11) for confirmation;
(d) the advisory-lock deviation (decision 1 here); (e) three enums, not two; (f) `id_empleado`
→ `usuarios`; (g) `movimientos_stock.id_comprobante_compra` deferred to stage 8. RLS
(`HabilitarRlsDeTenant`) for every table in the **same** migration that creates it (ADR-15).
Explicit snake_case `pk_*`/`ix_*`/`fk_*` names throughout (EF's PascalCase default is the
stage-3 trap). Any deviation lands in `docs/10-modelo-de-datos.md` in the **same PR** as the
migration.

## POS Screen Composition

`src/Ways.Web/src/paginas/Pos.tsx` + pure modules `src/api/carrito.ts` (reducer),
`src/api/pagos.ts` (vuelto/mezcla math, mirrors the server validator for instant UX and is
**never** authoritative), `src/api/ventas.ts` (request/response mappers). Layout: scan input +
cart table (left), cliente selector + totals + payment panel (right), ticket view after
checkout. Precedent for the whole shape: `Articulos.tsx` after its thirteen judgment-day rounds
(`ocupado = guardando || eliminando || escriturasHijas > 0`, `tokenEdicionRef`,
`generacionCargaRef`, `cargaInicialHechaRef`).

`react-async-state` obligations, named per rule:

| Rule | Obligation in this screen |
|---|---|
| 1 | Every cart mutation goes through `reducirCarrito` inside a functional updater; no helper reads component state inside an updater |
| 2 | `generacionResolucionRef` gates every `/resolver` response **and** the checkout's own response; `tokenEscaneoRef` gates scan lookups |
| 3 | Every cart mutation (scan, quantity change, line removal, cliente change, clear) bumps the resolution generation **before** changing the lines |
| 4 | The `finally` that clears `resolviendo`/`cobrando` is generation-gated |
| 5 | Disabled window runs from the "Cobrar" click until the ticket has rendered; per-line busy flags for quantity edits, never one page-level boolean |
| 6 | A 2xx checkout is never reported as failure: the post-write ticket fetch has its own try/catch and its own message ("la venta se registró con el número X; no se pudo abrir el ticket") |
| 7 | Medios de pago / parámetros failing to load produce a visible aviso **and** an actually-disabled "Cobrar" — the copy matches the enforcement |
| 8 | `key={idComprobante ?? 'venta-en-curso'}` on the payment/ticket subtree |
| 9 | While the checkout POST is outstanding, **every** superseding action is blocked (scan input, line edits, cliente change, cancel, new sale) plus a first-line `if (cobrando) return` re-entrancy guard. A duplicated sale is the worst defect this screen can ship |

## Testing Strategy

| Layer | What | Approach |
|---|---|---|
| Unit (Domain) | `ValidadorDePagos`: every rejection rule **and its order** (a payload violating rules 2 and 6 must report 2); tolerancia/vuelto boundaries from parameters; `AdmiteVuelto`/`RequiereReferencia`; CF exclusion; limit vs `CreditoIlimitado`. `CalculadorDeTotales`: rounding order, discount clamp, negative NCX lines, `total == Σ item.total`. `ReglaDeComprobantes`: sign vs `signo`, estado transitions, asociado rules. `ParserDeEscaneo`: `N*codigo`, the 7-digit boundary (6/7/13), garbage. `ResolvedorDeLetraComprobante`: full condición-fiscal cross (dormant but exhaustive) | Pure, no DB — the bulk of the stage's test mass, same bar as `ResolvedorDeOfertas` |
| Unit (Web) | `carrito.ts` (re-scan sums quantity, `N*`, removal, NCX negatives), `pagos.ts` (vuelto math, CC disabled for CF), `ventas.ts` mappers | Colocated `*.test.ts` per `web-descriptor-tests` |
| Component (Web) | Scan → line; CC option hidden/disabled for Consumidor Final; vuelto input disabled on a medio without `AdmiteVuelto`; **double-click on "Cobrar" issues exactly one POST** | `Pos.test.tsx`, RTL + `user-event`, `vi.mock('../api/cliente')` |
| Integration (atomicity) | Force a failure at **each** of the six steps and assert nothing persisted: no comprobante, no item, no pago, no movimiento, no CC row, `clientes.saldo` unchanged — **and the número consumed** (the documented gap) | Real Postgres; failure injected via constraint violation / cancellation |
| Integration (concurrency) | The four racy surfaces of the backstop map, with a forced rendezvous (`ParametrosTests` precedent) | Assert invariants (`cantidad = Σ movimientos`, `saldo = Σ movimientos`), not just status codes |
| Integration (budget) | Checkout with 2, 20 and 50 lines issues the **same** command count (≤ 16 + writes) | `DbCommand` interceptor, the stage-4 guard against a silent N+1 |
| Integration (snapshot) | Sell, then change the articulo's `descripcion`, `precio`, `area`, then re-read the comprobante ⇒ byte-identical items | The reprint contract |
| Integration (parity) | Legacy B6 rejection order end-to-end; no literal `10`/`20` in the source (grep assertion) | Success criteria of the proposal |
| Integration (RLS + auth) | Raw-SQL RLS proof per new table; the authorization-surface test; the two inverted Vendedor tests | `Ways.IntegrationTests` |

## Open Questions

- [ ] **`PPPP` has no business number.** `puntos_venta` has no `numero` column — stage 5
  formats `PPPP` by zero-padding `id_punto_venta`, which is a **global** identity and will
  exceed four digits with enough tenants. Harmless while TX/NCX are non-fiscal; fiscal
  invoicing will need a real `puntos_venta.numero` (unique per empresa) plus a backfill.
  Flagged, not built.
- [ ] **`id_empleado` is the authenticated user** (decision 11). Revisit when employees are
  modelled separately from login accounts.
- [ ] **Recargo por medio de pago stays dormant** (proposal decision 11) — needs the user's
  confirmation at the gate. Applying it would make the total depend on the payment mix.
- [ ] **Anulación is allowed to a Vendedor**, not just an Admin. Legacy parity (a cashier voids
  a mis-rung ticket) and the audit trail is the protection — but it is a product call worth an
  explicit yes/no at the gate.
- [ ] **No idempotency key on checkout.** Double-submit is blocked client-side (rule 9) and by
  the disabled window; a server-side key (`ux_comprobantes_venta_idempotencia`) would be the
  belt-and-braces but doc 10 has no such column. Flagged.
- [ ] **Legacy `**` → código `999999999`** ("artículo descuento") is **not** reproduced. Doc 10's
  replacement is the free-concept line (`items.id_articulo NULL`), which stage 5 does not build
  either. Spec should state the omission out loud.
- [ ] **Timezone** for `fecha` and for the oferta window (inherited stage-4 open question):
  server local time until a tenant timezone is modelled.
