# Delta for Stock

## ADDED Requirements

### Requirement: Writing Reorder Parameters Creates The Stock Row Without A Movement

Writing `minimo`/`reposicion` via `PUT /api/stock/minimos` MUST create the
`stock` row with `cantidad = 0` when none exists for the `(id_articulo,
id_punto_venta)` pair, and MUST NOT insert any `movimientos_stock` row — a
reorder parameter is not a ledger fact. This holds under `Cantidad Is
Always The Sum Of Its Movimientos`: a row created at `cantidad = 0` with
zero movements satisfies the invariant trivially (`0 = SUM(∅) = 0`).

#### Scenario: A minimo write for an articulo with no stock row creates it at zero with no movement
- GIVEN no `stock` row exists for `(articulo 20, punto de venta 1)`
- WHEN `PUT /api/stock/minimos` sets `minimo = 10` for that pair
- THEN a `stock` row is created with `cantidad = 0, minimo = 10` and zero
  `movimientos_stock` rows are inserted

#### Scenario: A minimo write for an articulo with an existing stock row touches no movement and no cantidad
- GIVEN `stock.cantidad = 45` for `(articulo 21, punto de venta 1)`
- WHEN `PUT /api/stock/minimos` sets `minimo = 10, reposicion = 60` for
  that pair
- THEN `stock.cantidad` stays `45` and zero `movimientos_stock` rows are
  inserted
