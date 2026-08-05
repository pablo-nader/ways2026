# Proposal: Stage 7 — Cuenta corriente y reliquidación a precio del día

## Intent

Implement Etapa 7 of `docs/10-modelo-de-datos.md` (stage table, doc-10:607) — "Cuenta
corriente + reliquidación a precio del día", the stage doc 10 marks as **paridad total con
el legacy**. Stage 5 made the ledger *grow*: a sale paid with a `cuenta_corriente` medio
writes a `Consumo` movement and bumps `clientes.saldo` atomically
(`ServicioDeVentas.cs:592-618`). But the account has **no way back down**:

1. **A client cannot pay.** `TipoMovimientoCc.Pago` and `TipoMovimientoCc.ActualizacionPrecios`
   exist as enum values explicitly reserved for stage 7
   (`src/Ways.Domain/CuentaCorriente/TipoMovimientoCc.cs:5-9`) with **no write path**. The
   spec `consumo-cuenta-corriente` closes with the requirement *"No Reliquidación, No CC
   Management, No Pagos De Cuenta"*. Debt only accumulates until the credit limit blocks the
   client from buying.
2. **There is no account screen.** `Ways.Web` has a `Clientes` ABM and nothing else; `saldo`
   is deliberately not editable in the client form (`src/Ways.Web/src/api/tipos.ts:306`),
   which is correct (doc 10 principle 7: *nada de saldos sin libro*) but leaves the cashier
   with no way to see or move an account. Legacy F4 (doc-01:373-407) is a whole screen —
   saldo / acuerdo / **disponibilidad**, movements with a running balance, and three actions.
3. **The system's most distinctive business rule is missing.** Legacy rule 9 (doc-01:481):
   *"Las ventas fiadas se reindexan a precio del día al momento de pagar."* Reliquidación
   (F4 "Actualizar precios") is an anti-inflation mechanism the business depends on. Without
   it, every peso fiado loses value silently and the rewrite is **not** at parity.

Stage 7 closes the account: money comes back in through the same audited machinery that let
it out, discretionary corrections are explicit and attributable, and fiado is re-indexed to
the price of the day — auditably, in **one** movement, instead of the legacy's ad-hoc
`ventas` row of `tipo = 4`.

## Scope

### In Scope

- **Pago a cuenta as a real comprobante** (decision 1): a new seeded `tipos_comprobante`
  row **`RC`** (clase `venta`, `es_fiscal = false`, `afecta_stock = false`,
  `discrimina_iva = false`, letra `NULL`, `signo = +1`). The pago flows through the existing
  `ServicioDeVentas` comprobante + `pagos_comprobante` machinery with **physical** (non-CC)
  medios, and writes **one negative `Pago`** movement in the same transaction.
- **Turno + arqueo integration for free**: because the pago is a comprobante pointing at the
  open turno, stage 6's `409 turno_no_abierto` guard, its `FOR SHARE` lock discipline and
  the `pagos(m)` term of `CalculadorDeArqueo` pick the cash up **with no new derivation**.
- **Ajuste manual** (legacy `tipo = 5`, decision 4): a signed `Ajuste` movement with a
  **required** `detalle`, exposed as an explicit action rather than as anulación plumbing.
- **Reliquidación a precio del día (F4)** — the stage centerpiece (decision 3): walks the
  client's not-yet-reliquidated `Consumo` movements, re-prices each underlying comprobante's
  items against the client's **current `id_lista_precio`** through the existing
  `listas_precio` / `precios` machinery, **annuls oferta discounts on those lines**
  (doc-01:398, verbatim: *"Las líneas de oferta se revierten (el descuento se anula)"*),
  writes **ONE** `ActualizacionPrecios` movement with an auditable detail, marks the
  consumos as reliquidated and updates `saldo` atomically.
- **Estado de cuenta API + screen** (legacy F4 parity): header with saldo, acuerdo
  (`limite_credito`, `-1`/`credito_ilimitado` → "ilimitado", doc-01:480) and
  **disponibilidad = acuerdo − saldo**; movement list with running balance, default last
  month, desde/hasta filter and "ver histórico".
- **Anulación symmetry** (decision 7): anulando an `RC` reverses its `Pago` movement.
  `AnularAsync`'s contramovimiento loop currently matches `Tipo == Consumo` only
  (`ServicioDeVentas.cs:413-414`) — it learns about `Pago`. Stage 6's closed-turno guard
  applies unchanged.
- **Authorization split** (decision 5): pago a cuenta + estado de cuenta reads under
  `Politicas.OperacionDePos`; **reliquidación and ajuste manual under a NEW
  Supervisor + Admin policy**.
- **Web**: estado de cuenta screen + the three actions, `react-async-state` compliant with
  `web-descriptor-tests` coverage. The legacy's `echo $listaCliente;` debug leak
  (doc-01:377) is not reproduced.

### Out of Scope

- **Imputación FIFO / invoice-level allocation** (decision 2) — a pago is one signed
  movement against the running saldo, exactly like the legacy.
- **Per-client pricing at checkout.** Doc-01 rule 4 (doc-01:473-476) is explicit: the
  `lista` distinction exists **only** inside reliquidación; the POS always charges the
  counter price. Stage 7 does **not** touch `ResolvedorDePrecios` at checkout.
- **Interest, recargos or punitorios on CC balances** (decision 6) — the legacy has none.
- **Cuenta corriente de proveedores** — stage 8.
- **Reversing a reliquidación** (decision 7) — irreversible by design, like the cierre; the
  correction path is a compensating `Ajuste`.
- **Editing `clientes.saldo` by hand** — stays impossible (the legacy's editable saldo,
  doc-01:365, is a bug we already refused to port).
- **Recargo por medio de pago** — still dormant, unrelated.
- **The D6 resumen-parcial enrichment** (stage-6 verify WARNING) — running as a separate
  follow-up PR, not part of this change.
- **Comprobantes de compra, transferencias, inventario** — stage 8.
- **Fiscal receipts / FE** — `RC` is explicitly non-fiscal, like `TX`/`NCX`.

## Capabilities

### New Capabilities

- `pagos-a-cuenta`: the `RC` comprobante shape (itemless, non-stock, non-fiscal), forbidden
  medios (a debt cannot pay a debt; CF clients have no account), the open-turno requirement,
  overpayment/saldo a favor, the single negative `Pago` movement, anulación symmetry.
- `reliquidacion-a-precio-del-dia`: which consumos are eligible, the re-pricing rule
  (current `id_lista_precio`, oferta discounts annulled), the single
  `ActualizacionPrecios` movement, the reliquidated marker, atomicity, irreversibility and
  the empty/no-op case.
- `ajustes-de-cuenta-corriente`: the signed manual `Ajuste` with required detail,
  authorization, and its distinction from the anulación contramovimiento.
- `estado-de-cuenta`: the read model — header (saldo / acuerdo / disponibilidad), running
  balance, date filtering, tenant + cliente scoping, empty state.

### Modified Capabilities

- `consumo-cuenta-corriente`: the requirement *"No Reliquidación, No CC Management, No Pagos
  De Cuenta"* is **removed**; `Pago` and `ActualizacionPrecios` gain write paths; the
  anulación contramovimiento rule extends to `Pago` rows; the saldo-cache invariant now
  covers four movement types.
- `comprobantes-venta`: `RC` joins the POS-emittable tipos with its own numeración series
  (`NumeracionComprobante` is keyed by `(IdPuntoVenta, TipoComprobante)`), and an `RC`
  comprobante carries **zero items**.
- `arqueo-de-cierre`: clarifies that `RC` pagos are ordinary `pagos(m)` in the per-medio
  derivation — no new formula, no new term.
- `operacion-de-pos`: pago a cuenta and estado de cuenta reads live under `OperacionDePos`;
  reliquidación and ajuste manual live under the new Supervisor + Admin policy.

## Approach

1. **Reliquidación is the centerpiece and ships pure-Domain first.** A DB-free
   re-pricer takes (items sold at their historical prices, current prices for the client's
   lista, oferta flags) and returns the per-line delta plus the aggregate — exhaustively
   unit-tested, mirroring `CalculadorDeArqueo` / `ValidadorDePagos` / `ResolvedorDePrecios`.
   Persistence is a thin transaction around it.
2. **Reuse the sale machinery instead of a parallel one.** The pago a cuenta is a
   comprobante, not a bare ledger INSERT: it inherits numeración, the turno guard, the
   `FOR SHARE` lock order, the arqueo derivation, the anulación path and
   `db-error-backstops`. A bare INSERT would silently reproduce the legacy's
   cash-invisibility gap and violate the one-derivation principle.
3. **One movement per business act.** Reliquidación writes exactly ONE
   `ActualizacionPrecios` row with the detail — not one per line, not one per comprobante
   (doc-10:534-536).
4. **Same posture as stages 5 and 6.** Decide-then-commit, `EstrategiaSinReintento` for
   manual rare operations, raw `UPDATE clientes SET saldo = saldo + $1 ... RETURNING saldo`
   (never a tracked `cliente.Saldo +=`), `ManejadorDeErrores` mapping, RLS + manual tenant
   filter on `movimientos_cuenta_corriente`.
5. **Lock order is pinned, not discovered.** Reliquidación and a concurrent sale for the
   same client both mutate `clientes.saldo`; design MUST pin the order (turno first — stage
   6's invariant — then cliente row lock) so the total order stays deadlock-free.
6. **DB CHANGE GATE (CLAUDE.md), exercised in autonomous mode.** Surface: the `RC` seed row
   in the **global** `tipos_comprobante` table — and the seed only runs when the table is
   empty (`InicializadorDeBaseDeDatos.cs:417`), so `RC` MUST ship as an **idempotent insert
   in the migration**, not as a seed-list edit — plus the reliquidated marker if design
   chooses a column. The gate summary groups by write path.

## Affected Areas

| Area | Impact | Description |
|---|---|---|
| `src/Ways.Domain/CuentaCorriente` | Modified | `TipoMovimientoCc` doc-comment loses its "reserved" clause; new pure `ReliquidadorDeConsumos` + `CalculadorDeEstadoDeCuenta` (running balance) |
| `src/Ways.Application/CuentaCorriente` (new) | New | `ServicioDeCuentaCorriente` (pago a cuenta, ajuste, estado de cuenta), `ServicioDeReliquidacion` |
| `src/Ways.Application/Ventas/ServicioDeVentas.cs` | Modified | `RC` path (itemless comprobante, negative `Pago` movement at :592-618); `AnularAsync` contramovimiento extended beyond `Tipo == Consumo` (:413-429); `InsertarMovimientoCcAsync` accepts a NULL `id_pago_comprobante` |
| `src/Ways.Domain/Ventas/ValidadorDePagos.cs` | Modified | `RC` forbids `cuenta_corriente` medios; the CF rule (5) and limit rule (6) do not apply to a payment |
| `src/Ways.Api/Endpoints` | New/Modified | `CuentaCorrienteEndpoints` (estado, pago, ajuste, reliquidación); `Politicas` gains the Supervisor + Admin constant |
| `src/Ways.Infrastructure` | Modified | Stage-7 migration (`CuentaCorrienteEtapa7`): `RC` idempotent insert + the reliquidated marker; EF config, backstops |
| `src/Ways.Web` | New/Modified | Estado de cuenta screen + 3 action modals; entry point from `Clientes.tsx`; `api/cuentaCorriente.ts` + descriptor tests |
| `docs/10-modelo-de-datos.md` | Modified | §8 status note (etapa 7 implemented) and any recorded deviation |

## Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| Reliquidación × sale race on the same cliente (double-counted or lost saldo) | High | Pinned total lock order (turno → cliente) analogous to stage 6's `FOR SHARE`; the reliquidated marker is set in the **same** transaction as the movement; rendezvous race test |
| Re-pricing arithmetic wrong → the client is charged an inflated debt (trust incident, irreversible) | High | Pure Domain re-pricer with exhaustive unit tests; **one** re-pricing implementation shared by the preview and the commit — never two formulas; the movement's `detalle` carries a per-line audit |
| Oferta semantics misread (excluding offer lines instead of annulling their discount) | Med-High | Pinned verbatim from doc-01:398 in the spec; a dedicated scenario per direction |
| `RC` with zero items breaking stage-5 invariants (item-count checks, totals, stock loop) | Med-High | The itemless path gets its own integration tests; the stock loop is a no-op by construction (`afecta_stock = false`) |
| `RC` seed missing on already-migrated databases (seed guarded by an emptiness check) | Med | Idempotent `INSERT ... WHERE NOT EXISTS` inside the migration + a test that asserts `RC` resolves on an existing DB |
| A second reliquidación double-charging the same consumos | Med | The marker is the invariant; concurrent reliquidación resolved by the same lock; a no-op reliquidación writes **no** movement |
| Overpayment semantics (saldo a favor vs vuelto) confusing the cashier | Med | Decision 9 pinned in the spec, surfaced in the UI as an explicit "saldo a favor" state |
| Reviewer overload (engine + write paths + greenfield screen) | Med-High | Chained PRs stacked-to-main; slice by write path; `judgment-day` before every PR |

## Rollback Plan

Almost fully additive. New Domain/Application namespaces, new endpoints, new web routes and
one new global catalog row — all removable without touching stage 1–6 data:

- The `RC` `tipos_comprobante` row can be deactivated (`activo = false`) instead of deleted;
  any `RC` comprobante already emitted stays readable and its `Pago` movement stays valid —
  the ledger is append-only by design, so nothing needs repair.
- The reliquidated marker (if a column) drops cleanly; without it, reliquidación simply
  cannot run again — no stage-6 or stage-5 behaviour depends on it.
- `AnularAsync` and `ValidadorDePagos` gain **branches conditioned on the `RC` tipo**;
  reverting restores stage-6 behaviour bit-for-bit for `TX`/`NCX`.
- No backfill and no rewrite of existing movements. Saldos already cached stay consistent
  because every stage-7 write goes through the same `UPDATE ... RETURNING` path.

One additive migration; reverting it plus the doc-10 edit restores stage 6 exactly.

## Dependencies

- Stages 5 and 6 (merged and archived): `comprobantes_venta` / `items` /
  `pagos_comprobante`, `movimientos_cuenta_corriente`, `ServicioDeVentas`
  (Emitir/Anular + numeración + `FOR SHARE` turno discipline), `turnos_caja` /
  `CalculadorDeArqueo`, `ValidadorDePagos`, `EstrategiaSinReintento`,
  `Politicas.OperacionDePos`, `listas_precio` / `precios` / `ResolvedorDePrecios`,
  `clientes` with `saldo` / `limite_credito` / `credito_ilimitado` / `id_lista_precio`.
- **DB Change Gate evaluation — exercised by the orchestrator in autonomous mode**
  (see `state.yaml`), before any migration is generated.
- `react-async-state` (mandatory) and `web-descriptor-tests` for the estado de cuenta
  screen; `db-error-backstops` per constraint; `judgment-day` before every PR.

## Success Criteria

- [ ] A client with debt pays cash, the saldo drops, and the cash appears in the turno's
      arqueo **without any new derivation term**
- [ ] A pago a cuenta with no open turno returns `409 turno_no_abierto`, exactly like a sale
- [ ] A pago a cuenta cannot be paid with a `cuenta_corriente` medio, and cannot target the
      Consumidor Final row
- [ ] Reliquidación re-prices every eligible consumo at the client's current lista, annuls
      oferta discounts on those lines, and writes **exactly one** `ActualizacionPrecios`
      movement whose `importe` equals the sum of the per-line deltas
- [ ] Running reliquidación twice in a row produces **one** movement — the second run is a
      no-op with no ledger write
- [ ] A failed reliquidación leaves saldo, marker and ledger untouched (fault-point test)
- [ ] `clientes.saldo` always equals the sum of the client's movement importes — proven over
      a scenario mixing consumo, pago, ajuste, reliquidación and anulación
- [ ] Anulando an `RC` restores the saldo and writes the reversing movement; anulación is
      still rejected when the turno is closed
- [ ] Reliquidación and ajuste manual return `403` for a Vendedor; pago a cuenta does not
- [ ] Estado de cuenta shows disponibilidad = acuerdo − saldo, and "ilimitado" when
      `credito_ilimitado`
- [ ] `RC` resolves on a database migrated from stage 6 (idempotent seed proven, not assumed)
- [ ] No endpoint accepts a saldo, a total or a computed delta from the client
- [ ] Every new constraint has its `db-error-backstops` mapping + race test

## Resolved product decisions (autonomous mode)

**All decisions below were resolved by the ORCHESTRATOR under the user's explicit autonomous
mandate (2026-08-05), including the DB Change Gate.** Provenance and rationale are recorded
per decision so the user can audit them in the final summary. Binding for spec/design/tasks.

1. **Pago a cuenta is a real `comprobante_venta` with a new tipo `RC`** (clase `venta`,
   `es_fiscal = false`, `afecta_stock = false`, `discrimina_iva = false`, letra `NULL`,
   `signo = +1` — money comes IN; the negative sign lives in the CC movement, not in the
   total). It flows through the existing `ServicioDeVentas` comprobante + `pagos_comprobante`
   machinery with **physical (non-CC)** pagos, plus ONE negative `Pago` movement in the same
   transaction. *Provenance*: legacy F4 creates a `venta` of `tipo = 3` (doc-01:391-392) —
   the legacy already models the pago as a sale row; doc 10 §4 splits `tipo 3/4/5` out of
   `ventas` into `movimientos_cuenta_corriente` (doc-10:349) but keeps the money in
   `pagos_comprobante`. *Rationale*: it inherits stage 6's turno guard and arqueo derivation
   **for free** — the physical cash lands in the cierre count through the existing `pagos(m)`
   join. A bare ledger INSERT would reproduce the legacy's cash-invisibility gap and violate
   the one-derivation principle. *Consequences*: a pago a cuenta **requires an open turno**;
   its efectivo/tarjeta means are counted in the arqueo; NCX-style anulación symmetry applies.
   *(orchestrator-resolved, autonomous mode)*
2. **No FIFO / no invoice-level imputación.** A pago is ONE signed movement against the
   running saldo, with `saldo_resultante` snapshotted per row. *Provenance*: legacy parity —
   `cuenta-corriente.php` subtracts from the balance and never allocates against invoices;
   doc 10 §8 has no allocation table. *Rationale*: allocation is a different product with
   partial-payment, over-payment and re-allocation rules; building it now is overbuild against
   a parity mandate. *(orchestrator-resolved, autonomous mode)*
3. **Reliquidación is batched and re-prices against the client's current `id_lista_precio`.**
   It walks the client's not-yet-reliquidated `Consumo` movements, re-prices each underlying
   comprobante's items through the existing `listas_precio` / `precios` machinery, **annuls
   the oferta discount on offer lines**, writes ONE `ActualizacionPrecios` movement with an
   auditable detail and updates saldo atomically. *Provenance*: doc-01:394-402 and
   doc-10:534-536; the `precio`/`precioEmp` binary of the legacy was already generalized into
   listas by doc 10 (doc-10:216-229). **Verified correction to the brief**: doc-01:398 says
   *"Las líneas de oferta se revierten (el descuento se anula)"* — offer lines are **NOT
   excluded**; they are re-priced at the full current price with the discount annulled, which
   makes their delta larger. The spec pins this verbatim. *Open for design*: the
   "already reliquidated" marker (a column on `movimientos_cuenta_corriente` vs. derivable
   from the `ActualizacionPrecios` detail). If a column is needed it goes through the gate —
   **pre-approved by the orchestrator**, logged here. *(orchestrator-resolved, autonomous mode)*
4. **Ajuste manual is a first-class action**, not just anulación plumbing: a signed `Ajuste`
   movement with a **required** `detalle`. *Provenance*: legacy `tipo = 5` shows `obs` on
   screen (doc-01:388, 393). *Rationale*: stage 5 already writes `Ajuste`-shaped rows as
   anulación contramovimientos; the spec must distinguish the two so the ledger stays
   readable. *(orchestrator-resolved, autonomous mode)*
5. **Authorization split — DELIBERATE TIGHTENING, flagged for the user's final summary.**
   Pago a cuenta and estado de cuenta reads sit under `Politicas.OperacionDePos` (Vendedor +
   Supervisor + Admin, legacy parity — the cashier takes payments all day). **Reliquidación
   AND ajuste manual sit under a NEW Supervisor + Admin policy.** *Provenance*: the legacy has
   no role gate at all. *Rationale*: both operations mutate balances **in bulk or at
   discretion** and are irreversible in practice; that is a different risk class from taking a
   payment. This is the stage's one deliberate departure from parity.
   *(orchestrator-resolved, autonomous mode)*
6. **No interest, recargos or punitorios on CC balances.** *Provenance*: the legacy has none.
   *Rationale*: reliquidación **is** this business's inflation mechanism (doc-01:401-402);
   adding interest on top would double-charge. *(orchestrator-resolved, autonomous mode)*
7. **Anulación symmetry, with one exception.** Anulando an `RC` reverses its `Pago` movement
   (stage 6's closed-turno gate applies unchanged). **Reliquidación movements are NOT
   anulable** — irreversible by design, like the cierre; the correction path is a compensating
   `Ajuste`. *Provenance*: doc-10:353-356 (anular never edits, it counter-moves) + stage 6's
   irreversible-close precedent. *Code hook*: `AnularAsync`'s contramovimiento loop filters
   `Tipo == Consumo` (`ServicioDeVentas.cs:413-414`) and must learn about `Pago`.
   *(orchestrator-resolved, autonomous mode)*
8. **Web**: an estado de cuenta screen (header saldo / acuerdo / disponibilidad, movement list
   with running balance and desde/hasta + histórico filters, per legacy F4) with the three
   actions gated per decision 5. The legacy's `echo $listaCliente;` debug leak (doc-01:377) is
   not reproduced. *(orchestrator-resolved, autonomous mode)*
9. **Overpayment produces saldo a favor** (a negative saldo), it is not rejected and it does
   not force a vuelto. Vuelto on an `RC`, if the cashier gives change, follows the existing
   parameterized rules (`vuelto_maximo`) through the same `ValidadorDePagos`. *Provenance*:
   the legacy simply subtracts from the balance; nothing rejects an overpayment.
   *(orchestrator-resolved, autonomous mode)*
10. **The credit-limit rules do not apply to a payment.** `ValidadorDePagos` rule 6 guards
    consumo, not repayment; rule 5 (CF cannot use cuenta corriente) becomes, for `RC`, "the
    Consumidor Final row has no account to pay". `RC` **forbids `cuenta_corriente` medios
    entirely** — a debt cannot pay a debt. *(orchestrator-resolved, autonomous mode)*
11. **`RC` gets its own numeración series** via the existing
    `(IdPuntoVenta, TipoComprobante)` counter (`NumeracionComprobante`) — no new mechanism,
    no shared series with `TX`. *(orchestrator-resolved, autonomous mode)*

### DB Change Gate — orchestrator evaluation (autonomous mode)

Model presented for the record, grouped by write path:

- **Pago a cuenta**: no new table. ONE new row in the **global** `tipos_comprobante`
  (`RC`, clase `venta`, letra `NULL`, `signo +1`, `discrimina_iva false`, `es_fiscal false`,
  `afecta_stock false`). Because `InicializadorDeBaseDeDatos.cs:417` seeds that table **only
  when it is empty**, the row MUST ship as an idempotent `INSERT ... WHERE NOT EXISTS` inside
  the migration; the seed list is updated too, for fresh databases.
- **Reliquidación**: the "already reliquidated" marker. Design chooses between a column on
  `movimientos_cuenta_corriente` (e.g. `id_movimiento_reliquidacion integer NULL` self-FK,
  which also gives per-consumo traceability) and a derivable form. **Pre-approved**: if a
  column is required, it must be nullable, additive, indexed for the eligibility scan, and
  covered by RLS and `db-error-backstops` like every other column in this repo.
- **Ajuste / estado de cuenta**: no schema change — `movimientos_cuenta_corriente` already
  has `tipo`, `importe`, `saldo_resultante` and `detalle` (doc-10:519-528).
- **Conformity**: strict doc-10 §8 shape; no new enum values (`pago` and
  `actualizacion_precios` already exist in `tipo_movimiento_cc`); operativa scoping and RLS
  unchanged. Migration name per convention: `CuentaCorrienteEtapa7`.

**Evaluation: APPROVED by the orchestrator under the autonomous mandate**, conditional on
(a) the `RC` insert being idempotent and proven on a stage-6 database, and (b) any marker
column being additive + nullable + RLS-covered. Recorded in `state.yaml` for the user's
final summary.

## Note for sdd-tasks

Slice by **write path**. Indicative order:

1. **Schema + seed gate**: the `RC` idempotent insert + the reliquidated marker + EF config
   + RLS + backstops + the migration `CuentaCorrienteEtapa7`.
2. **Pago a cuenta write path**: the `RC` branch in `ServicioDeVentas` (itemless comprobante,
   forbidden medios, the negative `Pago` movement) + the `AnularAsync` extension. Touches the
   project's most-guarded transaction → **its own full judgment-day round**.
3. **Reliquidación engine** — the centerpiece: pure Domain re-pricer first (exhaustive unit
   tests, no DB), then the one transaction (marker + movement + saldo).
4. **Ajuste manual + estado de cuenta API** + the new Supervisor + Admin policy.
5. **Web**: estado de cuenta screen + the three action modals (splittable if the forecast
   demands it).

Apply the Review Workload Forecast discipline (400-line budget; exact guard lines
`Decision needed before apply`, `Chained PRs recommended`, `400-line budget risk`).
Delivery is chained PRs **stacked-to-main** per `protocolo-pr-solo-dev`, with `judgment-day`
before every PR.

## Deferred / adjacent (recorded, not in scope)

- **Cuenta corriente de proveedores** — stage 8.
- **Recargo por medio de pago** — still dormant since stage 5.
- **D6 resumen-parcial enrichment** (stage-6 verify WARNING: áreas, tickets, primer/último
  ticket, egresos por categoría) — running as a **separate follow-up PR**, explicitly not
  part of this change.
- **`ServicioDeArticulos` `articulos_empresas` replace-set concurrency gap** — still open,
  carried since stage 4, unrelated to this stage.
