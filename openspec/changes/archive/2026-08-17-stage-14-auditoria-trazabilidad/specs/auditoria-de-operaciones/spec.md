# Auditoria De Operaciones Specification

## Purpose

Owns end to end (doc-11:201-217, proposal decisions 1-8): the `auditoria`
row's meaning and immutability, the twelve-action catalog across six
services and its coverage guarantee (one row per operation, `stock.transferencia`
structurally excluded), the same-transaction/fail-closed write rule, the
bounded per-action payload contract with its key-subset invariant and secret
denylist, the filtrable/paginated query, the `/export` sibling over the
etapa-11 contract, and the `LecturaDeAuditoria` policy. Every "operation X
writes exactly one audit row" requirement lives here, not in the audited
domain's own spec.

## Requirements

### Requirement: Auditoria Row Is An Append-Only, Tenant-Scoped Fact

`auditoria.id_tenant` MUST carry the tenant of the audited **subject**, not
of the acting user. `id_punto_venta` MUST be `NULL` for tenant-wide actions
(`precio.*`, `usuario.*`) and MUST carry the operation's own punto de venta
for per-PV actions (`venta.*`, `compra.*`, `stock.*`, `cc.reliquidacion`).
No UPDATE or DELETE endpoint MUST exist over `auditoria` — a written row is
immutable.

#### Scenario: A tenant-wide action carries no punto de venta
- GIVEN a price change on articulo 30 (`precio.cambio`)
- WHEN the audit row is written
- THEN `id_punto_venta` is `NULL`

#### Scenario: A per-PV action carries its own punto de venta
- GIVEN a stock ajuste at punto de venta 5 (`stock.ajuste`)
- WHEN the audit row is written
- THEN `id_punto_venta = 5`

#### Scenario: A platform actor editing a tenant's user files under the tenant, not the platform
- GIVEN a root/platform actor changes tenant 3's admin user's rol
- WHEN the `usuario.actualizacion` row is written
- THEN `id_tenant = 3` — the subject's tenant, not a platform sentinel

#### Scenario: No mutation endpoint exists over auditoria
- GIVEN an existing `auditoria` row
- WHEN any client attempts to call an update or delete endpoint against it
- THEN no such endpoint exists (404)

### Requirement: Tenant Isolation Enforced By RLS On The ways_app Connection

`auditoria` MUST enforce standard RLS (`HabilitarRlsDeTenant`) — the
`ways_app` role, which has no `BYPASSRLS`, MUST only see and write rows
matching `app_es_plataforma() OR id_tenant = app_tenant_actual()`.

#### Scenario: RLS blocks a cross-tenant read over the ways_app connection
- GIVEN `auditoria` rows exist for tenant 1 and tenant 2, and `ways_app` has
  no `BYPASSRLS`
- WHEN a raw SQL query reads `auditoria` while `app.tenant_id = 1`
- THEN only tenant 1's rows are returned — tenant 2's rows are invisible

#### Scenario: An INSERT with a foreign id_tenant is refused
- GIVEN the `ways_app` connection has `app.tenant_id = 1`
- WHEN an INSERT into `auditoria` supplies `id_tenant = 2`
- THEN Postgres refuses it with `42501` under `WITH CHECK`

### Requirement: Same-Transaction, Fail-Closed Write

Every audited write path MUST insert its `auditoria` row inside the same
database transaction as the business operation it records. When the audit
insert fails, the whole transaction MUST roll back — the business operation
MUST NOT be observable as having occurred. The checkout emission path MUST
NOT be touched: emitting a sale is out of scope by decision, not by
technique, and its existing query-count guard MUST stay unchanged.

#### Scenario: A forced audit-insert failure blocks a price change
- GIVEN `AbrirNuevoPrecioAsync` runs with the audit writer forced to fail
- WHEN the transaction is attempted
- THEN no new `precios` row exists, the previous row's `vigente_hasta` is
  unchanged, and no `auditoria` row exists

#### Scenario: A forced audit-insert failure blocks a venta anulación
- GIVEN `EjecutarAnulacionAsync` runs on an `emitido` comprobante with the
  audit writer forced to fail
- WHEN the transaction is attempted
- THEN the comprobante's `estado` remains `emitido`, no inverse
  `movimientos_stock` row was inserted, and no `auditoria` row exists

#### Scenario: Checkout emission writes no audit row and the query-count guard stays at 16
- GIVEN a standard TX checkout with 2 items and full efectivo payment
- WHEN checkout completes
- THEN zero `auditoria` rows are written for the emission, and
  `VentasCheckoutTests`'s query-count guard still asserts exactly `16`

### Requirement: The Twelve-Action Catalog Covers Six Services, One Row Per Operation

The action catalog MUST be: `precio.cambio` (articulo); `venta.anulacion`
(comprobante_venta); `compra.anulacion` (comprobante_compra);
`stock.ajuste`, `stock.decomiso`, `stock.conteo` (articulo); `cc.reliquidacion`
(cliente); `usuario.alta`, `usuario.actualizacion`, `usuario.baja`,
`usuario.desbloqueo`, `usuario.password` (usuario). Each operation MUST
write **exactly one** row, including a price change that both closes a
predecessor row and opens a new one. `stock.transferencia` MUST NOT be
audited — a transfer has both an origin and a destination punto de venta,
which a single `id_punto_venta` column cannot express without lying, and
both legs are already actor-stamped in `movimientos_stock`. A
zero-difference conteo, which writes no `movimientos_stock` row, MUST write
no `auditoria` row either.

#### Scenario: A price change that closes a predecessor writes exactly one row
- GIVEN a price change that both closes the currently vigente row and opens
  a new one (the close/reopen dance)
- WHEN the operation completes
- THEN exactly one `auditoria` row with `accion = "precio.cambio"` exists
  for it — not two

#### Scenario: A 100%-servicio anulación without cuenta corriente is attributable
- GIVEN a TX comprobante composed entirely of service lines (`id_articulo
  NULL` on every item) with no cuenta corriente pago
- WHEN it is anulado
- THEN an `auditoria` row with `accion = "venta.anulacion"`, `entidad =
  "comprobante_venta"`, naming the acting user is written — the stage's
  flagship scenario, since no reversal ledger row exists for this
  composition

#### Scenario: A zero-difference conteo writes no audit row
- GIVEN a conteo whose counted quantity equals the current `stock.cantidad`
- WHEN the conteo is submitted
- THEN no `movimientos_stock` row is written and no `auditoria` row is
  written either

#### Scenario: stock.transferencia is excluded by scope, not by defect
- GIVEN a stock transfer between two puntos de venta
- WHEN the transfer completes
- THEN no `auditoria` row is written for it, while both `movimientos_stock`
  rows (origin and destination legs) still carry their own `id_empleado`

### Requirement: The Payload Is A Bounded, Per-Action Field Set

Each `accion` MUST persist only its documented field set — never a full
entity dump. Every key present in `valor_anterior` MUST also be present in
`valor_nuevo` (`valor_nuevo` MAY carry additional operation metadata,
e.g. `id_movimiento_stock`). `valor_anterior` MUST be `NULL` exactly when
there was no prior state (first price, `usuario.alta`) or the action is a
pure fact (`usuario.password`). No `usuarios` payload MUST ever contain
`hash_password`, a token, or a session artifact.

#### Scenario: valor_anterior is a strict key subset of valor_nuevo
- GIVEN a `precio.cambio` row with `valor_anterior = {id_lista_precio: 1,
  monto: 100, vigente_desde: "2026-01-01"}`
- WHEN the row is validated against the key-subset invariant
- THEN every key in `valor_anterior` is present in `valor_nuevo`

#### Scenario: valor_anterior is NULL for a first-ever price
- GIVEN articulo 41 has no previous price in the General lista
- WHEN its first `precio.cambio` is audited
- THEN `valor_anterior IS NULL`

#### Scenario: valor_anterior is NULL for a pure-fact action
- GIVEN a user changes their own password (`usuario.password`)
- WHEN the row is written
- THEN `valor_anterior IS NULL` and `valor_nuevo` states only the fact

#### Scenario: No usuarios payload ever contains hash_password
- GIVEN all five `usuario.*` actions (`alta`, `actualizacion`, `baja`,
  `desbloqueo`, `password`)
- WHEN their payload field lists are inspected
- THEN none contains `hash_password`, a token, or a session artifact —
  asserted by a denylist test

### Requirement: GET /api/auditoria Is Filtered, Paginated, And Admin-Only

`GET /api/auditoria` MUST filter by date range, `accion`, actor, `entidad` +
`id_entidad`, and punto de venta, and MUST paginate its results. It MUST be
gated by a new `Politicas.LecturaDeAuditoria`, admitting `RolConocido.Admin`
only — `Supervisor`, `Vendedor`, and `Root` MUST be rejected, and the policy
MUST NOT be stacked over `LecturaDeReportes`. Within a tenant, an Admin MUST
see rows from **every** punto de venta — the PV is a filter, not a
boundary.

#### Scenario: Admin reads across every punto de venta of the tenant
- GIVEN an Admin and audit rows across puntos de venta 1 and 2
- WHEN they call `GET /api/auditoria` with no `idPuntoVenta` filter
- THEN rows from both puntos de venta are returned

#### Scenario: A Supervisor is rejected
- GIVEN a user with role Supervisor
- WHEN they call `GET /api/auditoria`
- THEN the response is `403`

#### Scenario: A Vendedor is rejected
- GIVEN a user with role Vendedor
- WHEN they call `GET /api/auditoria`
- THEN the response is `403`

#### Scenario: Tenant-wide rows appear under "todos" punto de venta
- GIVEN a `usuario.actualizacion` row with `id_punto_venta IS NULL`
- WHEN the query is filtered with `idPuntoVenta` unset ("todos")
- THEN that row is included in the result set

#### Scenario: Filtering by entidad + id_entidad returns only that aggregate's history
- GIVEN 3 `auditoria` rows for articulo 41 and 2 rows for articulo 42
- WHEN `GET /api/auditoria?entidad=articulo&idEntidad=41` is requested
- THEN exactly 3 rows are returned

### Requirement: The Export Sibling Follows The Etapa-11 Contract Verbatim

`GET /api/auditoria/export` MUST follow the `exportacion-de-reportes`
contract: policy inherited from co-location under `LecturaDeAuditoria`, row
cap that refuses rather than truncates, and figures equal to the JSON
endpoint's for identical filters.

#### Scenario: Export figures equal the endpoint's for identical filters
- GIVEN `GET /api/auditoria?accion=precio.cambio` returns 12 rows
- WHEN `GET /api/auditoria/export?formato=xlsx&accion=precio.cambio` is
  requested
- THEN the workbook contains 12 rows

#### Scenario: The export refuses rather than truncates at cap
- GIVEN filters matching more rows than the export cap
- WHEN the export is requested
- THEN it is rejected with `400 exportacion_demasiado_grande`, and no file
  is generated

#### Scenario: A Supervisor is rejected on the export too, inherited from the source route
- GIVEN a user with role Supervisor
- WHEN they call `GET /api/auditoria/export?formato=xlsx`
- THEN the response is `403`, with no separate policy declared on the
  export route
