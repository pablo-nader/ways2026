# Excedente a cuenta corriente

## Objective

When the customer of a sale has cuenta corriente, a payment above the sale total is not rejected: the excess is credited
to the customer's CC as a pago a cuenta (RC). Without CC, the strict rule from `cobro-sin-excedente` (#636) stays.

## Problem

Owner's request: "que una venta en cuenta corriente sí permita pagos por más y [...] sugerir de alguna manera que esa
diferencia se compute como pago a cuenta (en el caso de esta factura le quedarían esos 10 como un cobro de la cuenta)".

## Decisions

- Explicit intent: `SolicitudDeVenta` gets an opt-in flag (`ExcedenteComoPagoACuenta`). Without it, rule 9 rejects as in
  #636. The POS suggests it (a visible, editable option) only when the excess is non-cash or the cashier chooses it
  instead of vuelto, and only for a customer with CC.
- Split in one transaction: the sale (TX) keeps cobros for exactly its total; the excess becomes an RC (pago a cuenta) of
  the same customer with the same medio(s), reusing the RC / aplicaciones_cobro model. Example: total 990, QR 1000 → TX
  cobro QR 990 + RC QR 10. Never TX 1000 + RC 10 (the cierre counts both).
- Rejected alternative: a negative CC row (breaks rule 0, `ck_cobros_importe_no_negativo`, cierre; not a recibo).
- Online only: an offline sale (pre-assigned number) with the flag is rejected; the POS offers the option only with a
  connection, like the quick customer creation.
- Anulación of the sale does not undo the RC: the customer did pay that money, it stays as credit in the CC.
- Without CC customer (or Consumidor Final) the flag is rejected with a clear code.

No schema change expected.

## Tasks

- [x] T1 Domain + application: flag, split TX/RC in one transaction, guards, unit + integration tests (route: delegated writer)
- [x] T2 Web/POS: suggestion option, request flag, Vitest (route: delegated writer, same)
- [x] T3 Structural guards, judgment-day, stacked PR

## Checks

- Domain tests for the new rule branch
- Integration tests POST /api/ventas: CC customer split, without CC rejected, offline rejected, cierre counts total once
- Vitest for the POS suggestion

## Implementation notes (T1/T2)

- `RepartoDeExcedente.Repartir` (Ways.Domain/Ventas, pure): excess = Σ(importe − vuelto) − total; taken from rows without
  vuelto first, then cash, last row first in each group. Non-divisible rows: cuenta corriente, a medio with proveedor, a pago
  with `IdCobroMercadoPago` or manual approval. Errors: `pago_a_cuenta_sin_excedente` (excess <= 0, any excess > 0 is moved,
  also within tolerance), `pago_a_cuenta_no_divisible`.
- Guards in `ServicioDeVentas.EmitirAsync`: `pago_a_cuenta_sin_conexion` (NumeroPreasignado), `pago_a_cuenta_no_admitido`
  (signo <= 0), `pago_a_cuenta_sin_cuenta_corriente` (Consumidor Final — same predicate as the RC endpoint: every customer
  except CF has CC). With the flag the first validation skips rule 9; the reduced pagos are re-validated with rule 9 and the
  RC pagos with `ValidadorDePagoACuenta`.
- RC number reserved after the sale number, before the write transaction (retryable raw numbering). RC written inside the
  sale's retrying lambda (form (a)) as step 7, after the sale CC step: lock order turno → comprobantes → cliente. Sale and RC
  commit together, so the sale idempotency guard covers the RC. RC observaciones: "Excedente de la venta <numero>".
- Shared writer `EscriturasDeRecibo` (InsertarAsync + AcreditarAsync) extracted from `ServicioDeCuentaCorriente`; it judges
  `ReglaDeCobroEnVenta.ExigirFormaDelPago` itself.
- Response: `ComprobanteEmitido.PagoACuenta` (id, numeroVisible, importe), only on the emission that wrote it; mirrored in
  `PagoACuentaRegistrado` (always null for an RC).
- POS: option shown with a CC customer, online, not presupuesto, no medio with proveedor and an excess the server can cede;
  default ON when no row admits vuelto or a row without vuelto exceeds total + tolerance. ON: vuelto 0, rule 9 skipped
  locally, request flag, sale skips the local queue (online path), confirmation and "Venta finalizada" show the amount.
- No schema change.

## Progress

- Base: branch `pnader/cobro-sin-excedente-392163` (#636).
- T1/T2 verification and mutation evidence: see the writer report (dotnet build, domain, filtered integration, Application
  structural guards, Vitest, tsc, oxlint).
- Commits 980248f7 (backend) and 51b0410e (web). Parent spot check: domain filter 63 passed. Judgment day: APPROVED in
  round 0. Info: a retried ambiguous commit returns the sale without `PagoACuenta` (documented in the contract); no
  end-to-end test for a CC row plus excess from another medio, or for anulación leaving the RC; the step 7 comment
  overstates the lock order when the sale also pays with CC (INSERT only, no cycle).
