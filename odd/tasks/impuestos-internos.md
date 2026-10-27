# Impuestos internos en compras y artículos

## Objective

Let purchases and articles carry Argentine "impuestos internos" (excise on beverages, etc.) so
that supplier invoices with that tax can be loaded as printed and the article cost includes it.

## Problem / why

On a supplier invoice with impuestos internos the IVA base excludes the excise:
`total = neto + impuestos internos + IVA(neto)`. Ways assumes `total = neto × (1 + IVA)`
everywhere in purchases, so the invoice total never matches, the excise is lost from the
article cost (and therefore from the suggested price), and the Libro IVA compras shows a
non-zero difference. The owner resells (does not manufacture or import), so impuestos internos
are a cost for them, never a fiscal credit, and sales are unaffected (their IVA de venta is
computed over their whole net price; `ImpTrib = 0` stays correct).

## Decisions (owner, 2026-10-27)

- Impuestos internos are discriminated **per purchase line** (amount as printed on the invoice).
- Articles must allow loading them: the article gets a rate (percentage over the net cost) used
  to compute its cost from a list cost without IVA and to prefill the purchase line amount.
  The printed per-line amount always wins.
- Sales and ARCA invoicing are out of scope (reseller assumption).

## Model summary (evidence for the DB change)

- `articulos.porcentaje_impuesto_interno numeric(7,4) NOT NULL DEFAULT 0` — rate over the net
  cost. Category/scope follows `articulos` (no new table, no new RLS).
- `items_comprobantes_compra.impuesto_interno numeric(14,2) NOT NULL DEFAULT 0` — amount for the
  whole line, as printed. Not part of the IVA base.
- `comprobantes_compra.impuestos_internos numeric(14,2) NOT NULL DEFAULT 0` — sum of the lines,
  part of `total`.
- Defaults make the migration backfill-free (existing rows = 0).

## Rules

- Line `Total` keeps its meaning (bruto − descuento, with or without IVA per
  `PreciosIncluyenIva`). The excise is added on top, never inside it, and never enters the IVA
  base: invoice total = subtotal − descuento + IVA + impuestos internos + percepciones.
- Effective unit cost (with IVA) = (line amount with IVA + impuesto interno) / cantidad.
- Article nominal cost from a list cost **without** IVA = neto + neto × %II + neto × %IVA (the
  IVA never applies over the excise). A list cost **with** IVA is taken as final (already
  includes everything) — unchanged.
- A non-zero line excise on an invoice that does not discriminate IVA is rejected (400).
- Negative amounts/rates are rejected.

## Scope

- Backend: domain fields, EF config + migration, `CalculadorDeCompra` (total, costo efectivo),
  `CalculadorDeCostoNominal`, purchase and article contracts/services, Libro IVA compras column,
  MCP `crear_borrador_de_compra` line field, docs 10 §03/§05 and §11 (conector MCP).
- Web: purchase line column (prefilled from article rate, editable), invoice totals, article
  form field, Libro IVA column.

## Delivery strategy

`ask-on-risk` with the standing chain preference `stacked-to-main`: one PR per task.
Forecast ~1,000 authored lines over two tasks.

## Tasks

- [x] T1 — Backend: model + migration + calculation + contracts + Libro IVA + MCP + docs + tests.
  Route: delegated (2+ non-trivial files).
- [ ] T2 — Web: purchase line, totals, article form, Libro IVA column + tests.
  Route: delegated (2+ non-trivial files).

## Progress

- 2026-10-27: exploration done (no concept of impuestos internos anywhere: code, docs, legacy).
- 2026-10-27: T1 done (delegated writer, commit recorded below). Evidence: `dotnet build` 0 errors; Domain.Tests 1886 ok;
  Application.Tests 1074 ok (includes structural guards); full Ways.IntegrationTests 4958/4959 on first run, the one failure
  (`ReportesLibroIvaTests.ElExportDeComprasEsIgualAlEndpointJsonCeldaPorCelda`, new export column) fixed and `ReportesLibroIva` 29/29;
  new integration tests 49/49 (compras, articulos, MCP). Mutation check: dropping the excise from the total or the cost fails 3 / 2 tests.
  Decisions: `porcentaje_impuesto_interno` is an OWN field (not family-shared; Familias surfaces untouched), so editing only the rate of a
  family member changes the shared `costo_nominal` and asks for the scope; no CHECK constraints (service validates, per model summary);
  a concept line with excise is rejected; supplier discount applies over the total with IVA and excise. Doc 11 holds only the OAuth store:
  the tool field docs live in `docs/07-despliegue.md` (not updated, outside T1 surfaces).
- Contract fields for T2 (JSON camelCase): line request `impuestoInterno` (decimal, default 0); line response `impuestoInterno` (null for
  vendedor); invoice response `impuestosInternos`; article create `porcentajeImpuestoInterno` (default 0), edit `porcentajeImpuestoInterno`
  (null keeps), article response `porcentajeImpuestoInterno` (null for vendedor); Libro IVA compras fila and totales `impuestosInternos`
  (also export column "Impuestos internos" after "Percepción IIBB"). Error codes: `impuesto_interno_invalido`,
  `impuesto_interno_sin_discriminar`, `concepto_con_impuesto_interno`, `porcentaje_impuesto_interno_invalido`.

## Next step

T2.
