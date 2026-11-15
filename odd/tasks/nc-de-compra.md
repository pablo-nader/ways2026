# Notas de crédito de compra

## Objective

Let the owner load a supplier credit note (NC) in Compras. Today the purchase editor rejects
negative quantities and there is no NC voucher type for purchases.

## Problem / why

`tipos_comprobante` already models `Signo` (-1 for NC), but only C-FA, C-FB, C-FC and C-RM are
seeded for purchases (all +1). `CalculadorDeCompra` rejects `cantidad <= 0` and the item table has
a CHECK `cantidad > 0`. Supplier balance (`ServicioDeSaldoDeProveedor`) and the purchase VAT book
(`ServicioDeLibroIva.ObtenerComprasAsync`) ignore the sign. Owner decision 2026-11-09: returns go as
their own NC document (one sign per voucher), never as negative lines.

## Scope (authorized)

- New purchase voucher types C-NCA, C-NCB, C-NCC (signo -1, letra A/B/C, registers in VAT book).
- Quantities and amounts are entered POSITIVE (same convention as sales NCX); the sign is applied
  by every consumer.
- Article lines of a confirmed NC take stock OUT; concept lines move no stock.
- An NC never updates article costs or prices.
- Optional association to a confirmed invoice (FA/FB/FC) of the same supplier; never to a remito
  or another NC.
- A confirmed NC lowers the supplier balance; annulling it reverts stock and balance.
- Purchase VAT book shows the NC with negative amounts.
- No payments on an NC (it is a credit); applying an NC against specific invoices is out of scope.
- Web: the purchase editor offers the NC types and the optional associated invoice.

## Constraints

- Follow docs/10 naming; new columns/types documented in docs/10-modelo-de-datos/05-comprobantes-de-compra.md.
- Every consumer of purchase totals (reports, treasury, MCP connector reads) must respect the sign
  or reject NC explicitly.
- Delivery strategy: ask-on-risk; stacked-to-main slices (backend, then web).

## Tasks

- [x] T1 Backend: NC types, sign in stock/balance/VAT book, cost guard, association, tests. Route: delegated (writer, 2+ non-trivial files). Evidence: commits feat(compras): notas de crédito de compra + fix(compras): pagos y cuenta corriente respetan notas de crédito. Domain 2647/2647, Application 1707/1707, Integration (Compra|LibroIva|NotaDeCredito|Proveedor|Pago|CuentaCorriente|ConectorMcp|FiscalSchema|SuperficieDeAutorizacion|AislamientoDeTenant) 2502/2503; the one failure (CuentaCorrienteEtapa7BackstopTests catalog count) was fixed to 19 tipos and that class rerun 3/3, the broad filter was not rerun. Mutation evidence: cost guard, association (draft-time and confirm-time), pagos guard (BloquearCompraAsync) each turn their test red.
- [x] T2 Web: NC types in the purchase editor, associated invoice selector, tests. Route: delegated (writer). Evidence: feat(compras): carga de notas de crédito en el editor de compras. `npm run build` OK, `npm run lint` 0 errors (4 pre-existing warnings), `npx vitest run` 332 files / 10337 tests passed. RED observed first on comprasNotaDeCredito.test.ts (4 failing). Server messages are shown verbatim, so no new error translations. The selector lists the latest 200 confirmed invoices with an explicit hint when there are more.

## Acceptance criteria

- An NC with an article line confirms, takes stock out and lowers supplier balance by its total.
- The VAT book lists the NC with negative neto/IVA/total.
- Article cost is unchanged after confirming an NC.
- Associating an NC to a remito, to another NC or to another supplier's invoice is rejected (400).
- Payments on an NC are rejected.
- Annulling the NC restores stock and balance.

## Checks

- `dotnet test` unit + integration for Compras, LibroIva, saldo, structural guard tests.
- Ways.Web vitest for the purchase editor.

## Progress

- 2026-11-10: worktree `.claude/worktrees/nc-de-compra` on `pnader/nc-de-compra` from origin/main 0f5f8af5.
