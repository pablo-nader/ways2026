# Delta for Lotes y Vencimientos

## MODIFIED Requirements

### Requirement: FEFO Is The Server-Computed Default, Honored When Supplied

For a lot-effective articulo line — a sale line or a remito line — `idLote` on the request MUST be
optional. When omitted, the server MUST select the FEFO lot in the decide-then-commit read phase,
before the transaction opens, ordering candidate lots
`ORDER BY es_sin_identificar DESC, fecha_vencimiento ASC, id_lote ASC`. When `idLote` is supplied,
the server MUST validate it (exists, belongs to that articulo, not soft-deleted) and honour it
rather than override it with the FEFO pick. One lot per line MUST be enforced — if the selected
lot's balance does not cover the requested quantity, the operation MUST still proceed against that
lot (which may go negative at the counter or at the remito), never silently split across lots. A
remito line MUST freeze the resolved `id_lote` onto `items_remito` exactly as a sale item freezes it
onto `items_comprobante_venta` — the ordering, the honour-when-supplied rule, and the one-lot-per-
line rule are byte-identical; only the subject widens.
(Previously: written over "a sale line" only — `ServicioDeRemitos` did not exist until stage 17.)

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

#### Scenario: A remito line resolves and freezes FEFO exactly as a sale line does
- GIVEN articulo 40 has two lots with positive balance: `L1` expiring
  `2026-09-01` and `L2` expiring `2026-10-15`
- WHEN a remito line for articulo 40 omits `idLote` and is emitted
- THEN the server selects `L1` in the decide-then-commit read phase and
  `items_remito.id_lote` records `L1`

#### Scenario: A remito line honours a supplied idLote over the FEFO pick
- GIVEN articulo 40 has `L1` (expiring sooner) and `L2` (expiring later),
  both with positive balance
- WHEN a remito line explicitly supplies `idLote = L2`
- THEN the remito's stock exit proceeds against `L2`, not `L1`
