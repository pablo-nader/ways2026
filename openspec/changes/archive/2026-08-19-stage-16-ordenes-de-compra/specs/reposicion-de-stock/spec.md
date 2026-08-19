# Delta for Reposición de Stock

## MODIFIED Requirements

### Requirement: Reposición Report Is The Alert And The Purchase Suggestion, Grouped By Proveedor Habitual, Never Dropping Unassigned Rows

`GET /api/reportes/stock/reposicion?idPuntoVenta=` MUST return every
`stock` row at that punto de venta where `minimo IS NOT NULL AND cantidad
<= minimo`, joined to `articulos`, grouped by
`articulos.id_proveedor_habitual`. Rows whose articulo has no
`id_proveedor_habitual` MUST be grouped under a `"Sin proveedor"` group and
MUST NEVER be omitted. Each row's suggested purchase quantity MUST be
`sugerido = reposicion IS NULL ? null : max(0, reposicion - cantidad)` —
`sugerido` MUST be `null`, never `0`, when `reposicion` is unset. The
formula MUST NOT subtract any "stock en tránsito" term. As of Etapa 16,
`ordenes_compra` gives orders an `estado` and an expected arrival
(`fecha_esperada`), and the pending quantity per artículo is now derivable
from that capability — so the omission is no longer a structural absence.
It remains a **deliberate deferral**: subtracting it is out of scope for
this formula, with the reopen condition being the first customer who
over-orders because the report ignores what is already on the way.
(Previously: justified the omission with "that concept is structurally
absent from this system's model (no order-with-state entity exists)" — no
longer true once Etapa 16 ships; the formula and every scenario below stay
byte-identical.)

#### Scenario: An articulo with no proveedor habitual appears under Sin proveedor
- GIVEN articulo 12 is below its minimo and has `id_proveedor_habitual =
  NULL`
- WHEN the reposición report runs
- THEN articulo 12 appears grouped under `"Sin proveedor"`, not omitted

#### Scenario: sugerido is null, never zero, when reposicion is unset
- GIVEN articulo 13 has `minimo = 10, reposicion = NULL, cantidad = 3`
- WHEN the reposición report runs
- THEN articulo 13's `sugerido` field is `null`

#### Scenario: sugerido computes the gap to the restock target
- GIVEN articulo 14 has `minimo = 10, reposicion = 50, cantidad = 20`
- WHEN the reposición report runs
- THEN articulo 14's `sugerido = 30`

#### Scenario: A Vendedor is rejected from the reposición report and its export
- GIVEN a user with role Vendedor
- WHEN they call the reposición report or its export
- THEN the response is `403`

#### Scenario: The formula stays byte-identical after Etapa 16 ships
- GIVEN the same stock/minimo/reposicion inputs as before Etapa 16, and an
  `ordenes_compra` row now derivable for that proveedor
- WHEN the reposición report runs
- THEN `sugerido` computes exactly as before — no "stock en tránsito" term
  is subtracted
