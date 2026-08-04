# Verify Report — stage-5-pos-ventas

**Verdict**: PASS (verified PASS-WITH-WARNINGS; both warnings were documentation
staleness, reconciled into the spec/design before archive — see below)
**HEAD verified**: `4c73e3e` (doc reconciliation at `926d3df`). All 7 slice PRs merged:
#38 (auth), #40 (numeración+escaneo), #42 (schema), #44 (checkout), #46 (POS cart),
#48 (anulación+ajuste), #50 (POS payment).
**Date**: 2026-08-04

## Build & Tests

| Suite | Result |
|---|---|
| Ways.Domain.Tests | 271/271 |
| Ways.Application.Tests | 209/209 |
| Ways.IntegrationTests (real Postgres) | 391/391 (one transient Testcontainers flake, clean re-run green) |
| Vitest (Ways.Web) | 165/165 |
| `tsc -b` / `oxlint` / `vite build` | clean |
| `dotnet ef migrations has-pending-model-changes` | clean |

## Compliance

All scenarios across the 9 spec domains traced to passing runtime evidence:
payment rejection order (parametrized tolerancia/vuelto), all-or-nothing checkout with
8 fault points, gap semantics (número committed separately; ambiguous-commit idempotency
via the pre-committed número), concurrency serialization (stock, CC limit at the exact
boundary, numbering, double-anulación), ledger-cache invariants (stock == Σ movimientos,
saldo == Σ importes), the OperacionDePos auth matrix with two fail-closed guards +
LecturaDePuntosVenta, scan resolution (7-digit rule, N*codigo), NCX devoluciones,
anulación inverse semantics incl. NCX signs, EsProducto (services charged, never
stocked), dormant letter resolution, no-restaurar proven by test, and the POS screens'
contracts (rule-9 anti-duplicate-sale, availability contract per design decision 3).

## Deviations — all confirmed documented

Supervisor in OperacionDePos (legacy parity); catalogos-fiscales re-gate + front
narrowing; simple `id_empleado` FK (docs/10 note); `EstrategiaSinReintento` no-retry
posture for anulación/ajuste; availability contract (server-authoritative pricing);
`id_comprobante_compra` deferred to stage 8; rendezvous-deviation doc-comment;
numeración-committed-first (reconciled into design at verify).

## Pending gate item (explicit, non-blocking)

`ck_pagos_comprobante_importe_no_negativo` — schema defense-in-depth awaiting the
user's DB micro-gate. Domain rules 0/0b close every reachable path today; TODO
recorded in `ValidadorDePagos.cs`.

## Out-of-scope guard

Clean: no turnos/arqueos/movimientos_caja (stage 6), no CC management/reliquidación
beyond the narrow write slice (stage 7), no compras/transfers (stage 8), no AFIP,
no restaurar, no tickets-en-espera.

**Next**: sdd-archive.
