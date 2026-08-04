# Delta for Codigos de Barra

## MODIFIED Requirements

### Requirement: Barcode Add/Remove/List Management

Tenant admins MUST be able to add, remove, and list the barcodes of an
artículo independently of editing the artículo's other fields. Add/remove
stay gated by `GestionDeCatalogo`. Listing MUST return only active barcodes —
the same `BajaLogica`/global soft-delete filter used across the rest of the
ABM — and is now also reachable under `Politicas.OperacionDePos` (Vendedor +
Admin), since the POS needs it for scan lookup. A listing request against a
nonexistent or cross-tenant `id_articulo` MUST return the same uniform 404
(ADR-8) used by the add/remove paths.
(Previously: listing was gated by `GestionDeCatalogo` only — Vendedor was
blocked from every barcode operation, including read.)

#### Scenario: Admin removes a barcode without affecting the articulo

- GIVEN an artículo with two barcodes
- WHEN a tenant admin removes one
- THEN the artículo persists unchanged and the remaining barcode still
  resolves it

#### Scenario: Listing returns only active barcodes

- GIVEN an artículo with two barcodes, one of which is later removed
- WHEN a tenant admin lists the artículo's barcodes
- THEN only the remaining barcode is returned, with its persisted `codigo`;
  the removed one is excluded

#### Scenario: Listing barcodes of a nonexistent or cross-tenant articulo returns 404

- GIVEN an `id_articulo` that does not exist, or that belongs to another
  tenant
- WHEN a tenant admin requests that artículo's barcode listing
- THEN the response is 404, the same uniform ADR-8 result the add/remove
  endpoints return for a nonexistent or cross-tenant `id_articulo`

#### Scenario: Vendedor blocked from adding or removing barcodes

- GIVEN a user with the `vendedor` role
- WHEN they call the barcode add or remove endpoint
- THEN the request is rejected with an authorization error

#### Scenario: Vendedor can list barcodes for POS scan lookup

- GIVEN a user with the `vendedor` role
- WHEN they call the barcode listing endpoint for an articulo of their tenant
- THEN the request succeeds

## ADDED Requirements

### Requirement: Scan Resolution Rule

The POS scan-resolution parser MUST resolve an input code by length:
`< 7` digits resolves against `articulos`' internal code (`codigo_interno`);
`>= 7` digits resolves against `codigos_barra.codigo`. Both paths MUST filter
`activo = true` only. The parser MUST accept `<cantidad>*<codigo>` syntax
(e.g. `3*7790001` loads 3 units), and MUST default an empty or `0` cantidad
to `1`. Re-scanning a code already in the cart MUST sum quantities rather
than adding a new line.

#### Scenario: Short code resolves by codigo_interno

- GIVEN an articulo with `codigo_interno = "42"` and no matching 7+ digit
  barcode
- WHEN the scan input `"42"` is resolved
- THEN it resolves to that articulo via `codigo_interno`

#### Scenario: Long code resolves by codigos_barra

- GIVEN an articulo with barcode `"7790001234567"`
- WHEN the scan input `"7790001234567"` is resolved
- THEN it resolves to that articulo via `codigos_barra`

#### Scenario: Quantity-prefixed syntax loads the given quantity

- GIVEN the scan input `"3*7790001234567"`
- WHEN it is resolved
- THEN 3 units of the matching articulo are added to the cart

#### Scenario: Re-scanning sums quantity instead of duplicating the line

- GIVEN a cart already containing 2 units of an articulo
- WHEN the same code is scanned again for 1 more unit
- THEN the cart shows a single line with quantity 3, not two lines

#### Scenario: Inactive articulo is not resolved

- GIVEN an articulo with `activo = false` and a matching barcode
- WHEN that barcode is scanned
- THEN resolution fails to find an active match

#### Scenario: Unknown code is rejected

- GIVEN a scan input matching no `codigo_interno` or `codigos_barra` row
- WHEN it is resolved
- THEN the scan is rejected with a "not found" result and no line is added
