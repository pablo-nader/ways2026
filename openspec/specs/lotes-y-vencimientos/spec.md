# Lotes Y Vencimientos Specification

## Purpose

Owns the lot end to end (doc-11:142-163, proposal decisions 1-12, gate
amendments 1-3): lot identity and its frozen expiry, the `stock_lotes`
per-lot balance and its two invariants alongside `stock`, effective lot
control as the AND of a tenant-wide articulo flag and an empresa parametro,
the FEFO default contract, the "sin identificar" lot and the net-zero
`reclasificacion` reconciliation that gives pre-existing stock a lot without
moving a unit of the aggregate, the first-class `decomiso` circuit, the
pull-model vencimientos report and its `/export` sibling, and the stage's
binding constraint: with the module off, checkout must not gain a single
round-trip.

## Requirements

### Requirement: Lot Identity Schema At Rest

`lotes` MUST be tenant-wide scoped (`id_tenant`, no `id_empresa` — doc 09's
category for `articulos`, gate amendment 1), carrying `id_articulo`,
`codigo text NOT NULL`, `fecha_vencimiento date NULL`,
`es_sin_identificar boolean NOT NULL DEFAULT false`, and full `EntidadTenant`
audit columns (`created_at`/`updated_at`/`deleted_at`). `fecha_vencimiento`
MUST be NULL if and only if `es_sin_identificar = true`
(`ck_lotes_vencimiento_segun_tipo`). `codigo` MUST be non-blank
(`ck_lotes_codigo_no_vacio`) and, when the caller omits it, MUST be
server-derived from the ISO expiry (`YYYY-MM-DD`). The natural key
`(id_tenant, id_articulo, codigo)` MUST be unique among non-deleted rows
(`ux_lotes_articulo_codigo`), and it is the target get-or-create resolves
against.

#### Scenario: A lot is created with a server-derived codigo
- GIVEN a reception line for articulo 40 with `fecha_vencimiento = 2026-12-31`
  and no `codigo` supplied
- WHEN the lot is created
- THEN `lotes.codigo = "2026-12-31"` and `fecha_vencimiento = 2026-12-31`

#### Scenario: A blank codigo is unrepresentable
- GIVEN a raw write attempts `codigo = "   "` on a `lotes` row
- WHEN the insert executes
- THEN Postgres rejects it via `ck_lotes_codigo_no_vacio`

#### Scenario: A dated lot without an expiry is unrepresentable
- GIVEN a raw write attempts `es_sin_identificar = false, fecha_vencimiento = NULL`
- WHEN the insert executes
- THEN Postgres rejects it via `ck_lotes_vencimiento_segun_tipo`

#### Scenario: The same articulo and codigo cannot be created twice
- GIVEN an active lot `(articulo 40, codigo "L-001")` already exists
- WHEN a second lot with the same `(articulo, codigo)` is inserted
  concurrently by two reception requests
- THEN the get-or-create race self-resolves atomically — the
  `INSERT ... ON CONFLICT (id_tenant, id_articulo, codigo) DO UPDATE ...
  RETURNING` statement targets `ux_lotes_articulo_codigo` directly, so
  Postgres serializes the race on the conflict target and the "loser" reuses
  the winner's row with no exception surfacing on this path; both requests
  succeed and exactly one `lotes` row survives. (Amended at slice-5
  judgment-day — decision 14: the original wording claimed a `23505` +
  backstop translation here; empirically no `23505` is ever raised on the
  get-or-create path — that backstop belongs to the admin alta path instead
  (`ServicioDeLotes.CrearAsync`, `POST /api/stock/lotes`, a plain `INSERT`
  with no `ON CONFLICT`), which still raises a genuine `23505` on
  `ux_lotes_articulo_codigo` and is still translated to `409 lote_duplicado`
  by `ManejadorDeErrores` — proven by
  `ServicioDeLotesTests.DosPostConcurrentesAApiStockLotesConElMismoCodigoDanExactamenteUnCreadoYUnConflicto`.)

### Requirement: A Lot's Expiry Is Immutable Once Created

Once a `lotes` row exists, `fecha_vencimiento` MUST NEVER be silently
overwritten. A second reception for the same `(articulo, codigo)` that
carries a different `fecha_vencimiento` MUST be rejected with
`409 lote_vencimiento_incompatible` rather than updating the existing row —
retroactively changing the expiry would rewrite the meaning of every
movement already posted against that lot.

#### Scenario: A second reception with a matching expiry reuses the lot
- GIVEN an active lot `(articulo 40, codigo "L-001", fecha_vencimiento = 2026-12-31)`
- WHEN a second reception line arrives with the same articulo, codigo, and
  `fecha_vencimiento = 2026-12-31`
- THEN it resolves to the same `lotes` row and no new row is created

#### Scenario: A second reception with a conflicting expiry is refused
- GIVEN an active lot `(articulo 40, codigo "L-001", fecha_vencimiento = 2026-12-31)`
- WHEN a second reception line arrives with the same articulo and codigo but
  `fecha_vencimiento = 2027-01-15`
- THEN it is rejected with `409 lote_vencimiento_incompatible` and no
  `lotes`/`movimientos_stock` row is written

### Requirement: The Sin-Identificar Lot Is Unique Per Articulo

At most one `es_sin_identificar = true` lot MUST exist per non-deleted
`(id_tenant, id_articulo)` pair (`ux_lotes_sin_identificar`, a partial
unique index). It MUST be created lazily on first need by the
reconciliation, never eagerly.

#### Scenario: The sin-identificar lot is created once and reused
- GIVEN articulo 40 has no sin-identificar lot yet
- WHEN the reconciliation runs twice for that articulo across two puntos de
  venta
- THEN exactly one `es_sin_identificar = true` lot exists for articulo 40,
  reused by both runs

### Requirement: Stock Lotes Balance And Its Two Invariants

`stock_lotes` MUST be a PK-only cache keyed
`(id_articulo, id_punto_venta, id_lote)`, written exclusively via the same
`INSERT ... ON CONFLICT DO UPDATE ... RETURNING` row-lock-as-serialization
shape `stock` uses, with **no CHECK on `cantidad`** — a lot balance MAY go
negative at the counter, exactly like `stock` (legacy parity), and is
refused only on back-office paths. Two invariants MUST hold at all times:
(1) `stock_lotes.cantidad` for a given `(id_articulo, id_punto_venta, id_lote)`
equals `SUM(movimientos_stock.cantidad)` filtered to that same triple; (2)
for a lot-effective `(articulo, punto de venta)` pair after reconciliation,
`SUM(stock_lotes.cantidad over that pair's lots) = stock.cantidad` for that
pair — the two caches can never disagree by construction because every
lot-bearing movement is also an aggregate movement.

#### Scenario: A single lot's balance equals the sum of its own movements
- GIVEN a sequence of `compra +30`, `venta -5`, `transferencia -10` (origen),
  `decomiso -2` all carrying `id_lote = 7` for the same articulo and punto de
  venta
- WHEN `stock_lotes.cantidad` for `(articulo, punto de venta, 7)` is compared
  against the sum of those movements
- THEN both equal `13`

#### Scenario: The two caches reconcile for a lot-effective, reconciled pair
- GIVEN a lot-effective articulo at a punto de venta whose stock was
  reconciled into two lots, `L1 = 12` and `L2 = 28`, with `stock.cantidad = 40`
- WHEN `SUM(stock_lotes.cantidad)` for that `(articulo, punto de venta)` is
  compared against `stock.cantidad`
- THEN both equal `40`

#### Scenario: A lot balance may go negative at the counter
- GIVEN `stock_lotes.cantidad = 2` for a given lot at a punto de venta
- WHEN a sale of 5 units of that specific lot is checked out
- THEN the sale succeeds and `stock_lotes.cantidad = -3`

### Requirement: Effective Lot Control Is `controla_lote` AND `lotes_habilitado`

A punto de venta's stock movement of an articulo MUST run the lot-aware code
path if and only if BOTH `articulos.controla_lote = true` (tenant-wide) AND
the empresa's resolved `lotes_habilitado` parametro is `true`. When either
is `false`, the movement MUST run byte-identical to the pre-stage-12
aggregate-only path — no lot is required, no `stock_lotes` row is touched.

#### Scenario: A flagged articulo behaves aggregate-only where the module is off
- GIVEN articulo 40 has `controla_lote = true` tenant-wide, and empresa A has
  `lotes_habilitado = false`
- WHEN a sale of articulo 40 is checked out at a punto de venta of empresa A
- THEN the sale succeeds with no `idLote` required and no `stock_lotes` row
  is written

#### Scenario: An unflagged articulo behaves aggregate-only even where the module is on
- GIVEN articulo 41 has `controla_lote = false`, and empresa B has
  `lotes_habilitado = true`
- WHEN a sale of articulo 41 is checked out at a punto de venta of empresa B
- THEN the sale succeeds exactly as before this stage, with no lot dimension
  involved

#### Scenario: Effective control requires both flags together
- GIVEN articulo 42 has `controla_lote = true`, and empresa C has
  `lotes_habilitado = true`
- WHEN a sale of articulo 42 is checked out at a punto de venta of empresa C
- THEN the lot path runs: `idLote` is defaulted via FEFO if omitted, and a
  `stock_lotes` row is written

#### Scenario: A shared catalog articulo stays unaffected in an empresa that never enabled the module
- GIVEN articulo 43 is `DisponibleParaTodas = true` with `controla_lote = true`,
  shared by empresa D (`lotes_habilitado = true`) and empresa E
  (`lotes_habilitado = false`)
- WHEN articulo 43 is sold at a punto de venta of empresa E
- THEN the sale runs aggregate-only, unaffected by empresa D's activation

### Requirement: FEFO Is The Server-Computed Default, Honored When Supplied

For a lot-effective articulo line, `idLote` on the request MUST be optional.
When omitted, the server MUST select the FEFO lot in the decide-then-commit
read phase, before the transaction opens, ordering candidate lots
`ORDER BY es_sin_identificar DESC, fecha_vencimiento ASC, id_lote ASC`. When
`idLote` is supplied, the server MUST validate it (exists, belongs to that
articulo, not soft-deleted) and honour it rather than override it with the
FEFO pick. One lot per line MUST be enforced — if the selected lot's balance
does not cover the requested quantity, the operation MUST still proceed
against that lot (which may go negative at the counter), never silently
split across lots.

#### Scenario: An omitted idLote resolves to the nearest-expiry dated lot
- GIVEN articulo 40 has two lots with positive balance: `L1` expiring
  `2026-09-01` and `L2` expiring `2026-10-15`
- WHEN a sale line for articulo 40 omits `idLote`
- THEN the server selects `L1` and the item snapshot records `idLote = L1`

#### Scenario: The sin-identificar lot is offered before every dated lot
- GIVEN articulo 40 has a sin-identificar lot with positive balance and a
  dated lot `L1` expiring `2026-09-01`, both with positive balance
- WHEN a sale line for articulo 40 omits `idLote`
- THEN the server selects the sin-identificar lot, not `L1`

#### Scenario: A supplied idLote is honoured even when it is not the FEFO pick
- GIVEN articulo 40 has `L1` (expiring sooner) and `L2` (expiring later),
  both with positive balance
- WHEN a sale line explicitly supplies `idLote = L2`
- THEN the sale proceeds against `L2`, not `L1`

#### Scenario: An invalid supplied idLote is rejected
- GIVEN articulo 40 has no lot with id `999`
- WHEN a sale line supplies `idLote = 999`
- THEN the request is rejected before the transaction opens

#### Scenario: A lot running short still completes the line, never auto-splitting
- GIVEN the FEFO-selected lot has `stock_lotes.cantidad = 3`
- WHEN a sale line requests 5 units of that articulo with no `idLote`
  supplied
- THEN the sale proceeds entirely against the FEFO lot, leaving
  `stock_lotes.cantidad = -2`, and no second lot is touched by the same line

### Requirement: Reclasificación Reconciles Pre-Existing Stock Without Moving The Aggregate

When lot control becomes effective for an `(articulo, punto de venta)` pair
(the empresa's `lotes_habilitado` turns on for an already-flagged articulo,
or `articulos.controla_lote` turns on for an already-lot-enabled empresa),
the system MUST, synchronously and transactionally, write a net-zero pair of
`movimientos_stock` rows with `motivo = reclasificacion`:
`(id_lote = NULL, cantidad = -X)` and
`(id_lote = <sin-identificar>, cantidad = +X)`, where `X` is the pair's
residue (the aggregate `stock.cantidad` minus the sum already reconciled
into lots). `stock.cantidad` MUST be unaffected because the pair sums to
zero. The operation MUST be idempotent — a second run over an
already-reconciled pair MUST write zero rows — and MUST be re-runnable from
an explicit admin endpoint.

#### Scenario: Activation reconciles existing stock into the sin-identificar lot
- GIVEN `stock.cantidad = 40` for `(articulo 40, punto de venta 1)` and no
  `stock_lotes` row exists yet
- WHEN lot control becomes effective for that pair
- THEN two `movimientos_stock` rows are written, `motivo = reclasificacion`,
  summing to zero (`id_lote NULL, -40` and `id_lote = <sin-identificar>, +40`),
  `stock.cantidad` stays `40`, and `stock_lotes.cantidad` for the
  sin-identificar lot becomes `40`

#### Scenario: A second reconciliation run is a no-op
- GIVEN the pair above already ran and left zero residue
- WHEN the admin re-run endpoint is called again for the same
  `(articulo, punto de venta)`
- THEN no `movimientos_stock` row is written and no cache changes

#### Scenario: Reclasificación never uses motivo ajuste
- GIVEN a reconciliation run
- WHEN the resulting `movimientos_stock` rows are inspected
- THEN both carry `motivo = reclasificacion`, never `motivo = ajuste`

#### Scenario: A zero-cantidad reclasificación row never violates the non-zero CHECK
- GIVEN a reconciliation whose residue `X` is computed as `0`
- WHEN the reconciliation runs
- THEN no `movimientos_stock` row is inserted at all, exactly like
  `ContarAsync`'s zero-difference no-op, avoiding
  `ck_movimientos_stock_cantidad_no_cero`

### Requirement: Decomiso Is A First-Class, Admin-Only, Never-Negative Motivo

`POST /api/stock/decomiso` MUST be gated by `Politicas.GestionDeCatalogo`
stacked over `Politicas.OperacionDePos` (Admin-only), require a non-empty
`observaciones`, and, for a lot-effective articulo, require `idLote`. The
client MUST send a positive `cantidad`; the server MUST negate it before
writing the movement — no client-supplied signed delta is accepted, matching
`ContarAsync`'s discipline. A decomiso that would leave the target balance
(the lot's `stock_lotes.cantidad` when lot-effective, otherwise `stock.cantidad`)
negative MUST be refused with `409 stock_insuficiente_para_decomiso`.
`decomiso` MUST NOT be restricted to expired lots.

#### Scenario: A decomiso of a lot-effective articulo requires idLote
- GIVEN articulo 40 is lot-effective
- WHEN a decomiso request omits `idLote`
- THEN it is rejected before reaching the database

#### Scenario: A positive client cantidad is negated by the server
- GIVEN `stock_lotes.cantidad = 20` for a lot
- WHEN a decomiso of `cantidad = 5` is submitted for that lot
- THEN a `-5` `movimientos_stock` row with `motivo = decomiso` is inserted
  and `stock_lotes.cantidad = 15`

#### Scenario: A decomiso that would go negative is refused
- GIVEN `stock_lotes.cantidad = 3` for a lot
- WHEN a decomiso of `cantidad = 5` is submitted for that lot
- THEN it is rejected with `409 stock_insuficiente_para_decomiso` and no
  movement is written

#### Scenario: Decomiso applies to a non-expired lot too
- GIVEN a lot with `fecha_vencimiento` in the future and positive balance
- WHEN an Admin submits a decomiso for breakage with a non-empty
  `observaciones`
- THEN the request succeeds — decomiso is not gated on expiry

#### Scenario: Vendedor is blocked from decomiso
- GIVEN a user with role Vendedor
- WHEN they call `POST /api/stock/decomiso`
- THEN the request is rejected with `403`

#### Scenario: Decomiso without observaciones is rejected
- GIVEN a decomiso request with empty `observaciones`
- WHEN it is validated
- THEN it is rejected before reaching the database

### Requirement: Vencimientos Report Resolves "Hoy" In The Punto De Venta's Own Zona Horaria, With An Export Sibling

`GET /api/reportes/stock/vencimientos?idPuntoVenta&dias=` MUST return lot
rows at that punto de venta with a positive `stock_lotes.cantidad`,
classified into one of **four** states — `vencido` / `por_vencer` /
`vigente` / `sin_fecha` — ordered by `fecha_vencimiento` ascending (NULLS
LAST). `dias` MUST default to the resolved `dias_alerta_vencimiento`
parametro. A lot with `fecha_vencimiento = NULL` (the sin-identificar lot)
MUST classify `sin_fecha` and MUST be **included** in the report and its
totals — excluding it would make the report lie by omission: for a
reconciled lot-effective articulo the report's rows must sum to
`stock.cantidad`, and the sin-identificar residue is exactly the number
that should nag someone into identifying it (the same reasoning that put
the sin-identificar lot **first**, not last, in FEFO order). "Hoy" MUST be
resolved in the punto de venta's own `zona_horaria` parametro, never in
server/UTC time — **this is a binding verify criterion, not a nicety**. The
route MUST expose a `GET .../vencimientos/export?formato=xlsx` sibling under
the `exportacion-de-reportes` contract (co-located policy, no re-query,
figures equal to the JSON endpoint), gated by `Politicas.LecturaDeReportes`,
and a Tablero tile MUST surface the counts of `vencido`, `por_vencer`, and
`sin_fecha` for the punto de venta, linking to the report.
(Amended post-design: the proposal's three-class classification widens to
four — `sin_fecha` — because a report that silently excludes the
sin-identificar residue would understate its own totals.)

#### Scenario: A lot past its expiry classifies as vencido
- GIVEN a lot with `fecha_vencimiento = 2026-08-01` and positive balance at a
  punto de venta whose "hoy" (in its own zona_horaria) is `2026-08-12`
- WHEN the vencimientos report is requested
- THEN that lot's row is classified `vencido`

#### Scenario: A lot within the alert horizon classifies as por_vencer
- GIVEN `dias_alerta_vencimiento = 30`, a punto de venta whose "hoy" is
  `2026-08-12`, and a lot with `fecha_vencimiento = 2026-08-25`
- WHEN the vencimientos report is requested with no `dias` override
- THEN that lot's row is classified `por_vencer`

#### Scenario: A lot beyond the horizon classifies as vigente
- GIVEN the same setup, and a lot with `fecha_vencimiento = 2027-01-01`
- WHEN the vencimientos report is requested
- THEN that lot's row is classified `vigente`

#### Scenario: "Hoy" is resolved in the punto de venta's own zona horaria, not UTC
- GIVEN a punto de venta with `zona_horaria = "America/Argentina/Buenos_Aires"`
  (UTC-3), server time `2026-08-13T01:30:00Z`, and a lot with
  `fecha_vencimiento = 2026-08-12`
- WHEN the vencimientos report is requested
- THEN "hoy" resolves to `2026-08-12` in that zona_horaria (not `2026-08-13`
  as a naive UTC read would produce), and that lot classifies `por_vencer`,
  not `vencido` — the expiry date is inclusive: a lot expiring today is still
  sellable today, and `vencido` means `fecha_vencimiento < hoy` strictly. A
  naive UTC read would have tipped it into `vencido` a day early, which is
  exactly the bug this scenario pins. (Amended at slice-2 judgment-day:
  the original THEN said `vencido`, contradicting both design.md's boundary
  table and this scenario's own zone-resolution intent; orchestrator decision
  — retail semantics, conservative bias — resolved the boundary as strict
  `<`, implemented in `ReglaDeLotes.EstaVencido`.)

#### Scenario: The sin-identificar lot appears in the report as sin_fecha and counts toward the totals
- GIVEN a lot-effective articulo at a punto de venta has a sin-identificar
  lot (`fecha_vencimiento = NULL`) with `stock_lotes.cantidad = 12`, plus a
  dated lot with `cantidad = 28` (`stock.cantidad = 40` for the reconciled
  pair)
- WHEN the vencimientos report is requested
- THEN the sin-identificar lot's row appears classified `sin_fecha`, and
  the report's rows for that articulo sum to `40`, matching
  `stock.cantidad` — omitting it would understate the total by `12`

#### Scenario: A zero-balance lot never appears in the report
- GIVEN a lot whose `stock_lotes.cantidad = 0`
- WHEN the vencimientos report is requested
- THEN that lot's row does not appear

#### Scenario: The export sibling's figures equal the JSON endpoint's
- GIVEN `GET /api/reportes/stock/vencimientos?idPuntoVenta=7` returns 4 rows
  with a combined `cantidad` of `65`
- WHEN `GET /api/reportes/stock/vencimientos/export?formato=xlsx` is
  requested for the same `idPuntoVenta`
- THEN the workbook's rows sum to the same `65`

#### Scenario: A Vendedor is rejected from the vencimientos report and its export
- GIVEN a user with role Vendedor
- WHEN they call the vencimientos report or its export
- THEN the response is `403`

### Requirement: The Module Off Switch Costs The Checkout Hot Path Nothing

With `lotes_habilitado = false` for the empresa, `ServicioDeVentas.EmitirAsync`
MUST issue **no more round-trips than the pre-stage-12 baseline** — asserted
by a query-count test, not by inspection. With the module on and the cart
containing no lot-controlled articulo, the same MUST hold, because
`controla_lote` arrives inside the articulo map already loaded for pricing
(no probing query). With the module on and the cart containing at least one
lot-controlled articulo, the net round-trip delta versus the pre-stage-12
baseline MUST be zero.

#### Scenario: Module off issues one fewer parametro round-trip than the baseline
- GIVEN `lotes_habilitado = false` for the empresa
- WHEN checkout resolves `tolerancia_pago`, `vuelto_maximo` and
  `lotes_habilitado`
- THEN exactly one batched `parametros` query (`WHERE clave IN (...)`)
  executes — one fewer round-trip than the pre-stage-12 baseline of two
  separate parametro queries

#### Scenario: Module on with no lot-controlled articulo in the cart issues no FEFO query
- GIVEN `lotes_habilitado = true` and a cart containing only articulos with
  `controla_lote = false`
- WHEN checkout resolves parametros and plans stock writes
- THEN the batched parametros query runs once and no `stock_lotes`/FEFO
  query executes — net still one fewer round-trip than the baseline

#### Scenario: Module on with a lot-controlled articulo nets zero round-trip change
- GIVEN `lotes_habilitado = true` and a cart containing an articulo with
  `controla_lote = true` and no `idLote` supplied for its line
- WHEN checkout resolves parametros and plans the FEFO lot for that line
- THEN the batched parametros query runs once (one fewer than baseline) and
  exactly one additional `stock_lotes` read executes for the FEFO plan (one
  more than baseline), netting zero round-trip change
