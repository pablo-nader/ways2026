# Etiquetas y Carteles Specification

## Purpose

Defines the read-only gondola label / price poster print engine: the four
fixed format descriptors, the selection axes (artículo, categoría with
descendants, marca, con-oferta-vigente), the price resolved via the existing
oferta engine at `cantidad = 1`, the no-vigent-price exclusion, the explicit
lista de precios and its printed name, copies and the row cap, the no-cost
and no-barcode-symbology contracts, the authorization gate, and the print
calibration spike that gates the whole stage.

## Requirements

### Requirement: Fixed Format Descriptors As Data (OD3)

The system MUST expose exactly four fixed sheet format descriptors —
`A4-3x8` (70×37 mm, 24 per sheet), `A4-2x7` (99×38 mm, 14 per sheet),
`CARTEL-A4` (full sheet), `CARTEL-A5` (half sheet, two per A4) — each as an
internal `DescriptorDeFormato` value (page size, margins, columns, rows, cell
pitch, gutters, printed fields). No format geometry MUST be hardcoded inside
a rendering component; every mm value MUST originate from the selected
descriptor. No per-empresa format configuration exists in this stage.

#### Scenario: A descriptor drives the sheet geometry exactly
- GIVEN the `A4-3x8` descriptor is selected
- WHEN the sheet renders
- THEN it shows exactly 3 columns × 8 rows per page, at the descriptor's
  declared cell pitch and margins, with no value read from the component code

#### Scenario: A fifth format does not exist
- GIVEN the format selector
- WHEN its options are listed
- THEN exactly the four fixed descriptors appear, and no per-empresa format
  configuration screen or table is reachable

### Requirement: Print Calibration Spike Gates The Stage (OD1)

Before any label or poster feature ships, the system MUST prove a die-cut A4
grid prints in physical alignment, on at least one target browser, with a
recorded numeric verdict: every cell origin within ±1.0 mm of its nominal
die-cut position, AND the last row's cumulative drift within ±1.5 mm, AND the
existing `CajaZ` / `CuentaCorriente` report print views print unchanged after
the label sheet's own page-box mechanism (`@page`) is introduced.

#### Scenario: Spike passes both criteria
- GIVEN the calibration grid is printed at 100% scale on the reference
  die-cut sheet
- WHEN the physical result is measured
- THEN every cell origin is within ±1.0 mm of nominal, the last row's drift
  is within ±1.5 mm, and `CajaZ`/`CuentaCorriente` still print exactly as
  before the change

#### Scenario: Spike fails and the stage stops
- GIVEN the calibration grid's measured drift exceeds ±1.0 mm on a cell or
  ±1.5 mm on the last row
- WHEN the verdict is recorded
- THEN the label/poster feature does not proceed past the spike, and the
  QuestPDF licence question is escalated to the owner rather than silently
  adopted

#### Scenario: The label page box does not regress existing report prints
- GIVEN the label sheet's `@page` mechanism is in place
- WHEN `CajaZ` or `CuentaCorriente` is printed
- THEN their existing `d-print-none` descriptor tests stay green and the
  printed output is unchanged from before this stage

### Requirement: POST /api/etiquetas/datos Composes Selection And Price

`POST /api/etiquetas/datos` MUST be a read-only POST accepting
`idPuntoVenta`, `idListaPrecio`, and either `idsArticulo[]` or a filter
(`busqueda`, `idArea`, `idCategoria`, `idMarca`, `soloConOfertaVigente`). It
MUST resolve price and applied ofertas for the matched artículos via
`ServicioDeOfertas.ResolverAsync` at `cantidad = 1` — no second price/offer
matching implementation MUST exist anywhere in this endpoint.

#### Scenario: Filter selection resolves through the existing resolver
- GIVEN a filter by `idCategoria` matching 12 artículos
- WHEN `POST /api/etiquetas/datos` runs
- THEN price and applied ofertas for those 12 artículos come from
  `ServicioDeOfertas.ResolverAsync`, not from a separate price computation

#### Scenario: soloConOfertaVigente defers entirely to the resolver
- GIVEN a candidate set of artículos matched by the coarse filter
- WHEN `soloConOfertaVigente=true` is requested
- THEN only artículos for which `ResolverAsync` reports `Aplicadas.Count > 0`
  at `cantidad = 1` for the chosen lista and current momento are returned —
  no independent oferta-matching logic decides this

### Requirement: Category Selection Includes Descendants

Selecting by `idCategoria` MUST match the category and all of its
descendants, the same hierarchical semantics `idCategoria` already has on
`GET /api/articulos`.

#### Scenario: A parent category selection reaches child-category artículos
- GIVEN categoría "Bebidas" is the parent of "Gaseosas"
- WHEN `idCategoria` = Bebidas is used in the etiquetas filter
- THEN artículos classified under "Gaseosas" are included in the result

### Requirement: No Cost Or Provider Field On The Printed Response

The `POST /api/etiquetas/datos` response DTO MUST NOT carry `costo_lista`,
`costo_nominal`, `descuento_proveedor`, or any provider field — absent from
the DTO, not merely hidden in the UI.

#### Scenario: Serialized response contains no cost field
- GIVEN any successful `POST /api/etiquetas/datos` call
- WHEN the JSON response is inspected
- THEN no cost, margin, or proveedor field is present anywhere in it

### Requirement: Offer Requiring cantidad_minima > 1 Never Prints Discounted

The label MUST resolve price at `cantidad = 1`. When the resolver's
`Aplicadas` is non-empty at that quantity, the cell prints `PrecioOriginal`
struck through and `PrecioFinal` prominent; when empty, it prints one price
with no strike. An offer whose `cantidad_minima > 1` therefore MUST NOT
produce a struck-through price on a label.

#### Scenario: A "llevando 3" offer prints no discount
- GIVEN an oferta with `cantidad_minima = 3` on an artículo, and no other
  applicable offer at `cantidad = 1`
- WHEN the label for that artículo is composed
- THEN the cell shows one price, with no struck-through original

#### Scenario: An offer applicable at cantidad 1 prints both prices
- GIVEN an oferta that applies at `cantidad = 1`
- WHEN the label for that artículo is composed
- THEN the cell shows `PrecioOriginal` struck through and `PrecioFinal`
  prominent

### Requirement: Artículo Without Vigent Price Never Prints, Exclusion Counted

An artículo whose resolved `PrecioOriginal`/`PrecioFinal` is `null` for the
selected (artículo, lista) pair MUST be excluded from the printed sheet, and
the selection screen MUST show the excluded count before printing.

#### Scenario: No-price artículo is excluded and counted
- GIVEN a selection of 20 artículos where 3 have no vigent price in the
  chosen lista
- WHEN the sheet is composed
- THEN the sheet contains 17 labels and the screen reports 3 excluded before
  printing

#### Scenario: No-price artículo never prints as zero
- GIVEN an artículo with no vigent price
- WHEN the sheet is composed
- THEN no label with a `$0` price is produced for it

### Requirement: Explicit Lista De Precios, Defaulted And Printed

The lista de precios MUST be an explicit, visible selector defaulted to the
tenant's `EsDefault` lista, and its name MUST be printed in the sheet header
— never inside an individual label cell.

#### Scenario: Selector defaults to the EsDefault lista
- GIVEN the etiquetas selection screen loads
- WHEN the lista selector renders
- THEN it is pre-set to the lista with `EsDefault = true`

#### Scenario: Sheet header prints the chosen lista's name
- GIVEN the operator selects a non-default lista and prints
- WHEN the sheet renders
- THEN the sheet header shows that lista's name, and no individual label
  cell repeats it

### Requirement: Copies Per Row And The 200-Artículo Cap

Copies per row MUST be selectable 1–99 (default 1), with an "aplicar a
todos" helper. The selection MUST be capped at 200 artículos — the existing
`ServicioDeArticulos` clamp — and the response MUST carry a `truncado` flag
when the filter matched more than the cap. The screen MUST show
"N etiquetas = M hojas" before printing.

#### Scenario: Copies multiply the printed label count
- GIVEN an artículo with 5 copies selected
- WHEN the sheet is composed
- THEN 5 labels for that artículo appear on the sheet

#### Scenario: A filter matching more than 200 sets truncado
- GIVEN a filter matching 350 artículos
- WHEN `POST /api/etiquetas/datos` runs
- THEN at most 200 rows are returned and `truncado = true`

### Requirement: No Barcode Symbology, Text Only (Decision 9)

The label MUST print `codigo_interno` (and `codigo_barra` when present) as
human-readable text. No barcode symbology MUST be rendered.

#### Scenario: Codes render as plain text
- GIVEN an artículo with a `codigo_barra`
- WHEN its label renders
- THEN the code appears as text, with no barcode image or symbology library
  invoked

### Requirement: OperacionDePos Authorization, Nothing Stacked

`POST /api/etiquetas/datos` and the etiquetas selection screen MUST be
gated by the existing `Politicas.OperacionDePos` (Vendedor + Supervisor +
Admin). No new policy MUST be introduced for this capability.

#### Scenario: Vendedor can compose and print a label sheet
- GIVEN a user with role Vendedor
- WHEN they call `POST /api/etiquetas/datos` for their tenant
- THEN the request succeeds (authorization-wise)

#### Scenario: Root is rejected
- GIVEN a user with `RolConocido.Root`
- WHEN they call `POST /api/etiquetas/datos`
- THEN the request is rejected with 403
