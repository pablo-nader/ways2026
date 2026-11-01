# Lista de compras: proveedor por fantasía, fechas, estado de pago y filtros

## Objective
The purchases list shows the supplier by trade name, both load and invoice dates, an always-visible payment chip, and grid filters.

## Problem
- Supplier shown by razón social in the grid and the selector; the rest of the system uses nombre de fantasía (fallback razón social).
- "Fecha de recepción" is really the load/confirmation date (`FechaRecepcion`, written by `ConfirmarAsync`).
- The invoice date (`FechaComprobante`) is not listed.
- "Estado de pago" only renders when a supplier is selected: the list DTO has no payment state; the web fetches `/proveedores/{id}/saldo` separately.
- The grid lacks column filters (número, tipo, estado de pago, fecha del comprobante).

## API contract (shared by T1 and T2)
`GET /api/compras` new query params (existing ones unchanged; `desde`/`hasta` keep filtering `FechaRecepcion`):
- `numero` (string): case-insensitive contains on `NumeroExterno`.
- `idTipoComprobante` (int).
- `estadoPago` (`pagada` | `pendiente`): only Confirmada purchases match; pagada = saldo pendiente <= 0, pendiente = saldo > 0. Applied in the query so `total` and paging stay correct. Invalid value -> 400 like other filters.
- `fechaComprobanteDesde`, `fechaComprobanteHasta` (yyyy-MM-dd, inclusive) on `FechaComprobante`.

`CompraListada` new fields:
- `nombreProveedor` (string): trimmed nombre de fantasía, fallback razón social (soft-deleted supplier still shows its name).
- `fechaComprobante` (DateOnly?, `yyyy-MM-dd`).
- `estadoPago` (`"Pagada"` | `"Pendiente"` | null): null when not Confirmada. Parcial counts as Pendiente.

Export: same new columns; "Fecha de recepción" header becomes "Fecha de carga".

## Tasks
- [x] T1 (delegated writer, backend): filters + DTO fields + export + integration/unit tests.
- [x] T2 (delegated writer, web): column renames, fecha del comprobante column, proveedor by fantasía (grid + selector), payment chip Pagado/Pendiente from the list, grid filter row, Vitest tests.
- [ ] T3: judgment-day per PR slice, stacked PRs (backend then web).

## Delivery
Strategy: stacked-to-main, two PRs (backend, web).

## Progress / evidence
- Route: T1 and T2 delegated (writer trigger, 2+ non-trivial files each), in parallel on disjoint surfaces.
- Contract deviation (accepted): `nombreProveedor` stays null for non-Admin roles and soft-deleted suppliers (existing rule, dangling-fk-read-models); the web keeps the `Proveedor #id` fallback.
- estadoPago filter: correlated subquery mirroring `LectorDePagadoPorCompra` (aplicaciones minus redondeo); mutation evidence: removing the redondeo term fails `ElFiltroPorEstadoDePagoCoincideConLaFilaYElTotalEsElDelFiltro`.
- T1: Application.Tests (Exportacion|ReferenciasDeListado|OrdenDeDocumentos) 67/67; Integration (Compras|SuperficieDeAutorizacion|OrdenDeDocumentos|ReferenciasDeListado) 480/480; Domain ReglaDePagoDeCompra 19/19.
- T2: tsc clean; vitest Compras + api 1871 passed; full suite 284/285 (GrillaDeItemsDeCompra load-sensitive, passes alone 134/134).

## Next step
Judgment-day per slice, then stacked PRs.
