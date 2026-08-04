# Proposal: Stage 6 — Turnos de caja, arqueos, tesorería y gastos

## Intent

Implement Etapa 6 of `docs/10-modelo-de-datos.md` — "Turnos de caja + arqueos + tesorería
+ gastos", the stage doc 10 marks as **Cerrar caja**. Stage 5 made the system *sell*:
`ServicioDeVentas.EmitirAsync` writes comprobantes, pagos, stock and cuenta corriente in
one transaction. But the money it moves is **unaccountable**:

1. **Nothing owns a shift.** `comprobantes_venta.id_turno_caja` is hardcoded `null`
   (`src/Ways.Application/Ventas/ServicioDeVentas.cs:459`), documented as a declared
   deviation in `ComprobanteVenta`'s doc-comment and in the stage-5 proposal (decision 1).
   Sales exist in an unbounded stream: there is no "today's cash", no first/last ticket, no
   way to hand the drawer to the next cashier.
2. **Cash that never was a sale has nowhere to go.** Retiros de efectivo, refuerzos,
   apertura de cajón (legacy F12) and gastos have no table. The legacy encoded retiros as
   `gastos` with the magic `tipo = 95` and área `1` (doc-01 D5) — a number that dies here.
3. **The legacy close is the worst code in the system.** Doc-01 D7: all totals travel by
   POST in **readonly inputs manipulable from the browser** (⚠ D7), the close writes
   `cajas` + `cajaz` + two mass `UPDATE`s **without a transaction**, and a half-failed close
   leaves the caja closed and the gastos open. Doc 10 §7's answer is categorical: *"Cerrar
   caja deja de ser un INSERT de 30 columnas"* — totals are **derived** from the
   comprobantes and gastos pointing at the turno.

Stage 6 closes the loop: a shift is opened, every sale and expense is attached to it, and
the close counts physical money per medio de pago against a server-derived expectation.

## Scope

### In Scope

- **`turnos_caja`** (doc 10 §7), operativa scope: `id_empleado_apertura` /
  `id_empleado_cierre NULL`, `fecha_apertura` / `fecha_cierre NULL`, `fondo_inicial
  numeric(14,2) NOT NULL DEFAULT 0`, `estado_turno` enum (`abierto | cerrado`),
  `observaciones`. **One open turno per punto de venta**, enforced by a partial unique
  index (decision 7).
- **`movimientos_caja`**: physical cash outside the sale — `tipo_movimiento_caja` enum
  (`retiro | refuerzo | apertura_cajon`), `importe` (0 for `apertura_cajon`), `motivo`,
  per turno.
- **`arqueos_turno`**: **one row per medio de pago with activity** in the turno.
  `importe_esperado` **derived server-side** from the ledgers, `importe_declarado` from the
  cashier, `diferencia` (the legacy's per-caja `saldo`, now per medio).
- **`movimientos_tesoreria`** (ex `cajaz`): `tipo_movimiento_tesoreria` enum
  (`retiro_caja | deposito | gasto | ajuste`), chained `inicio → ingreso / egreso → final`
  per punto de venta. Stage 6 writes **exactly one row automatically at cierre**
  (decision 4).
- **`gastos`** (doc 10 §5, minus `id_comprobante_compra` — decision 1): `fecha`,
  `id_punto_venta`, `id_turno_caja`, `id_empleado`, `categoria_gasto` enum
  (`proveedor | sueldos | viaticos | impuestos | servicios | otros`), `id_proveedor NULL`,
  `id_area NULL`, `concepto`, `detalle`, `id_medio_pago NOT NULL`, `numero_factura NULL`,
  `importe`.
- **Checkout wiring**: `EmitirAsync` resolves the open turno for the punto de venta
  **early** (fail-fast `409 turno_no_abierto` before any pricing work), carries
  `IdTurnoCaja` in the frozen `PlanDeVenta`, and populates the column. The
  `IdTurnoCaja = null` hardcode dies.
- **Anulación gate**: `AnularAsync` rejects a comprobante whose turno is `cerrado`
  (legacy D2's `cerrada = 1` block, preserved). Stage-5 comprobantes with
  `id_turno_caja NULL` stay anulable (decision 5).
- **Cierre in ONE transaction**: derive expected per medio → write `arqueos_turno` with
  the declared counts → `estado = cerrado` + `fecha_cierre` + `id_empleado_cierre` →
  chain one `movimientos_tesoreria` row. All or nothing.
- **Resumen parcial** (legacy D6 parity): live turno summary — ingresos por área and por
  medio de pago (efectivo neto de vuelto, tickets, primer/último ticket) and egresos por
  categoría de gasto + retiros — all read from the same derivation the cierre uses.
- **Caja screens in `Ways.Web`** (greenfield): apertura, movimientos (retiro / refuerzo /
  apertura de cajón), resumen parcial, and cierre with per-medio count inputs; plus the
  `Pos.tsx` gate seam (server `409 turno_no_abierto` → prompt to open the turno).
  `react-async-state` compliant with `web-descriptor-tests` coverage.

### Out of Scope

- **Manual tesorería operations** (`deposito`, `gasto`, `ajuste` entered by hand, plus
  tesorería reporting/UI) — only the automatic `retiro_caja` chain at cierre ships
  (decision 4).
- **Caja Virtual: `arqueos_recargas` / `arqueos_recargas_canales`** — doc 10 §7 keeps
  them as a separate concern.
- **`gastos.id_comprobante_compra`** — the column and its FK ship together in stage 8 with
  `comprobantes_compra` (decision 1, deferred-FK precedent: `movimientos_stock`).
- **Cuenta corriente management, reliquidación (F4), pagos de cuenta** — stage 7.
- **Comprobantes de compra, transferencias, inventario** — stage 8.
- **Reapertura de turno / edición de un arqueo** — a close is irreversible by design
  (legacy D7's warning becomes a real invariant).
- **Backfill of stage-5 comprobantes with `id_turno_caja NULL`** — none (decision 8,
  pre-production).
- **Cash-drawer hardware, ticket printing of the Z report** — the endpoint returns the
  data; physical printing is not this stage.
- **Reasignar cliente de un ticket (legacy D4)** — not reproduced.

## Capabilities

### New Capabilities

- `turnos-de-caja`: apertura/cierre lifecycle, one-open-per-punto-de-venta invariant,
  `fondo_inicial`, estado transitions, the closed-turno effects on sales and anulación.
- `movimientos-de-caja`: retiro / refuerzo / apertura de cajón, motivo rules, importe 0
  for apertura de cajón, membership in an open turno.
- `arqueo-de-cierre`: the server-side per-medio expected derivation, declared counts,
  diferencia, which medios get a row, cierre atomicity and irreversibility.
- `gastos`: expense capture against an open turno, categorías, medio de pago, proveedor /
  área optional links, effect on the arqueo.
- `tesoreria`: the chained `inicio → final` ledger and the automatic `retiro_caja` row
  written at cierre.

### Modified Capabilities

- `comprobantes-venta`: `id_turno_caja` stops being NULL — it is resolved server-side from
  the open turno and is **required** for new sales; anulación is rejected when the turno is
  closed; historical NULL-turno comprobantes remain anulable.
- `operacion-de-pos`: checkout gains a fail-fast open-turno precondition
  (`409 turno_no_abierto`); the caja surface (apertura, cierre, movimientos, gastos,
  resumen) lives under `Politicas.OperacionDePos`.

## Approach

1. **The cierre derivation is the centerpiece.** `importe_esperado` per medio is computed
   **only** from ledger rows that point at the turno — `pagos_comprobante` of non-anulados
   comprobantes, their `vuelto`, `gastos`, `movimientos_caja` and `fondo_inicial`. The
   client sends **only the declared counts**. There is no request shape in which a total
   can be supplied, which is the structural answer to legacy bug D7. Design pins the exact
   per-medio formula, including the cash asymmetry (fondo, refuerzos, retiros and vuelto
   are physical cash; card/CC medios only accumulate pagos).
2. **Reuse the stage-5 posture.** Decide-then-commit (no advisory locks),
   `EstrategiaSinReintento` from `Ways.Application/Abstracciones` for apertura, cierre,
   retiro, refuerzo and gasto (manual, rare, no natural idempotency key),
   `db-error-backstops` per constraint, RLS helper + operativa scoping, `ManejadorDeErrores`
   mapping.
3. **The apertura race is settled by the database.** Partial unique index
   `ux_turnos_caja_abierto (id_punto_venta) WHERE estado = 'abierto'` + a plain INSERT;
   `23505` maps to a domain `409 turno_ya_abierto`. No read-then-insert window.
4. **No server-side POS session** (stage-5 decision 3 preserved): every request carries
   `idPuntoVenta` and the server resolves the open turno per request. The turno is never
   client-supplied.
5. **Pure Domain first.** The per-medio expected calculation and the arqueo/diferencia
   arithmetic ship as DB-free Domain classes with exhaustive unit tests, mirroring
   `ValidadorDePagos` / `ResolvedorDePrecios`.
6. **Surgical checkout touch.** The `EmitirAsync` change is one early resolution + one
   field on `PlanDeVenta` + one assignment. It is the smallest possible diff on the
   project's most-guarded transaction, and it gets a **full judgment-day round of its own**.
7. **DB CHANGE GATE (CLAUDE.md), blocking**: 5 tables (`turnos_caja`, `movimientos_caja`,
   `arqueos_turno`, `movimientos_tesoreria`, `gastos`) + 4 enums (`estado_turno`,
   `tipo_movimiento_caja`, `tipo_movimiento_tesoreria`, `categoria_gasto`) + the partial
   unique index + the `comprobantes_venta.id_turno_caja` FK + RLS. The gate summary MUST
   group by **write path** and MUST surface the two flagged tightenings (decisions 2
   and 9) and the deferred-FK decision (1).

## Affected Areas

| Area | Impact | Description |
|---|---|---|
| `src/Ways.Domain/Caja` (new) | New | `TurnoCaja`, `MovimientoCaja`, `ArqueoTurno`, `MovimientoTesoreria`, enums, pure `CalculadorDeArqueo` |
| `src/Ways.Domain/Gastos` (new) | New | `Gasto`, `CategoriaGasto` |
| `src/Ways.Application/Caja` (new) | New | `ServicioDeTurnos` (apertura, cierre, movimientos), `ServicioDeResumenDeTurno`, `ServicioDeGastos` |
| `src/Ways.Application/Ventas/ServicioDeVentas.cs` | Modified | Early open-turno resolution in `EmitirAsync` (~line 63), `IdTurnoCaja` in `PlanDeVenta` (kills the `null` at ~line 459), closed-turno guard in `AnularAsync` |
| `src/Ways.Domain/Ventas/ComprobanteVenta.cs` | Modified | `IdTurnoCaja` doc-comment: the stage-5 promise is fulfilled |
| `src/Ways.Infrastructure` | Modified | EF configs, the stage-6 migration (`TurnosCajaYGastosEtapa6`), RLS policies, partial unique index, new backstop mappings |
| `src/Ways.Api/Endpoints/*` | New/Modified | `CajaEndpoints`, `GastosEndpoints` (new) under `OperacionDePos`; `VentasEndpoints` error surface gains `turno_no_abierto` / `turno_cerrado` |
| `src/Ways.Web` | New/Modified | Caja screens (apertura / movimientos / resumen / cierre) + `Pos.tsx` gate seam + descriptor/component tests |
| `docs/10-modelo-de-datos.md` | Modified | The §6 deferred-FK note gains its `gastos` twin; any deviation recorded so doc 10 does not drift |

## Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| Regression in the checkout transaction — the most-guarded code in the project | High | Keep the diff surgical (resolution + one field + one assignment); the checkout-wiring slice gets a **dedicated full judgment-day round**; the entire stage-5 integration suite must stay green unchanged except for the turno precondition |
| Derived totals disagreeing with what the cashier physically counted (trust incident) | High | One derivation used by **both** resumen parcial and cierre — never two formulas; per-medio unit tests over a synthetic turno covering vueltos, anulados, gastos, retiros and refuerzos |
| Anulados leaking into the arqueo (or being wrongly excluded) | Med-High | Spec pins anulación-inside-turno scenarios; the derivation reads `estado = emitido` only, and the anulación itself is blocked once the turno closes |
| Apertura race creating two open turnos | Med | Partial unique index + `23505` backstop + rendezvous race test (stage-4/5 precedent) |
| Sales blocked because nobody opened a turno (day-one usability shock) | Med | Fail-fast `409 turno_no_abierto` with a dedicated code, and a POS gate seam that offers to open the turno instead of surfacing a raw error |
| Half-closed turno (the exact legacy D7 failure) | Med | One transaction covering arqueos + estado + tesorería, with a fault-point integration test per step |
| Cierre role too permissive (any Vendedor can close) | Med | Legacy parity default (decision 2), explicitly **flagged at the gate** as a one-line flip to Supervisor + Admin |
| Reviewer overload (5 tables + engine + API + greenfield web) | Med-High | Chained PRs stacked-to-main; `sdd-tasks` slices by write path; the web slice is expected to be the largest |

## Rollback Plan

Fully additive. Five new tables, four new enums, two new Domain namespaces, new endpoints
and new web routes — all droppable without touching stage 1–5 data. The only non-additive
edges are narrow and revert cleanly:

- `comprobantes_venta.id_turno_caja` **already exists and is nullable** (shipped NULL in
  stage 5). Stage 6 only adds the FK and starts populating it; dropping the FK returns the
  column to its dormant state and stage-5 rows are untouched.
- `EmitirAsync` gains a precondition and one assignment; reverting restores the stage-5
  behaviour exactly (sales with NULL turno), with no data repair needed.
- `AnularAsync` gains a guard; reverting only removes a restriction.
- No backfill runs (decision 8), so there is no migration of existing rows to undo.

One additive migration; reverting it plus the doc-10 edit restores stage 5 exactly.

## Dependencies

- Stage 5 (merged and archived): `comprobantes_venta` / `items` / `pagos_comprobante`,
  `movimientos_stock`, `movimientos_cuenta_corriente`, `ServicioDeVentas` (Emitir/Anular),
  `EstrategiaSinReintento`, `Politicas.OperacionDePos` (Vendedor + Supervisor + Admin),
  `MedioPago` with `Comportamiento` / `AdmiteVuelto`, seeded áreas and proveedores.
- **DB Change Gate approval — blocking**, before any migration is generated.
- `react-async-state` (mandatory) and `web-descriptor-tests` for the caja screens;
  `db-error-backstops` per constraint; `judgment-day` before every PR.

## Success Criteria

- [ ] A cashier opens a turno, sells, records a gasto and a retiro, and closes counting
      cash per medio — end to end
- [ ] Selling with no open turno returns `409 turno_no_abierto` **before** any pricing work
      runs, and the POS offers to open the turno
- [ ] Every comprobante emitted after this stage carries a non-NULL `id_turno_caja`
- [ ] Two concurrent aperturas on the same punto de venta produce exactly one open turno
      and one `409 turno_ya_abierto`
- [ ] No request body anywhere accepts a total or an expected amount — grep proves the
      cierre payload carries only declared counts
- [ ] `importe_esperado` equals the ledger derivation for every medio, including vueltos,
      gastos, retiros, refuerzos and fondo; anulados are excluded
- [ ] A failed cierre leaves the turno **open** with no arqueo and no tesorería row
- [ ] Anulación is rejected once the turno is closed; stage-5 NULL-turno comprobantes stay
      anulable
- [ ] `arqueos_turno` has no row for a medio with no activity, and never a row for a
      `cuenta_corriente` medio
- [ ] No `tipo = 95` equivalent exists anywhere: retiros are `movimientos_caja`, gastos are
      `gastos`
- [ ] Every new constraint has its `db-error-backstops` mapping + race test

## Resolved product decisions (auto-mode question round)

Question round resolved in **auto mode** (2026-08-04): legacy defaults and the
exploration's recommendations were adopted and are documented here as assumptions the user
reviews at the DB Change Gate. Binding for spec/design/tasks unless corrected at the gate.
Decisions **2** and **9** are explicitly **flagged for user decision at the gate**.

1. **`gastos` ships in stage 6, WITHOUT `id_comprobante_compra`.** *Provenance*: doc 10
   places `gastos` in §5 (compras) but stages it in etapa 6 — this resolves the ambiguity.
   The compra FK follows the deferred-FK pattern already used for
   `movimientos_stock.id_comprobante_compra` (doc 10 §6 note): stage 8 adds column **and**
   FK together. *Rationale*: an `int NULL` without an FK is a reference without a
   guarantee; and without `gastos`, legacy D6's "egresos por tipo" cannot be reproduced.
2. **Roles: apertura AND cierre under `Politicas.OperacionDePos`** (Vendedor + Supervisor
   + Admin). *Provenance*: legacy parity — the legacy has **no** role gate on caja.
   *Rationale*: D7's real bug is client-submitted totals, which server derivation fixes
   regardless of who clicks. **FLAGGED AT GATE**: tightening cierre to Supervisor + Admin
   is a one-line flip and is offered to the user, not assumed.
3. **`fondo_inicial` DEFAULT 0, exposed at apertura.** *Provenance*: doc 10 §7 default;
   parity-plus — the legacy has no explicit fondo, so exposing the field is a small,
   safe improvement that makes the cash expectation honest.
4. **Tesorería: the cierre auto-writes exactly ONE `movimientos_tesoreria` row** in the
   same transaction (parity with the legacy `cajaz` chain: `inicio` = last `final`,
   `ingreso` = retiros, `egreso` = gastos). Manual `deposito` / `ajuste` / `gasto`
   tesorería entries are **deferred** — no slice this stage.
5. **Anulación gate (new rule, legacy D2 parity).** `AnularAsync` rejects when the
   comprobante's turno is `cerrado`. Comprobantes with `id_turno_caja NULL` (stage-5 era)
   remain anulable — the guard only fires when a turno exists and is closed.
6. **`arqueos_turno` rows only for medios with nonzero activity** in the turno (legacy D6
   parity: the summary lists what moved). Medios with
   `Comportamiento = cuenta_corriente` are **excluded from the arqueo entirely** — there is
   nothing physical to count and the legacy never counts it; CC income appears in the
   cierre summary, not as a countable arqueo row.
7. **Apertura race handled by a partial unique index**
   (`ux_turnos_caja_abierto (id_punto_venta) WHERE estado = 'abierto'`) + plain INSERT;
   `23505` → domain `409 turno_ya_abierto` per `db-error-backstops`. No advisory lock, no
   read-then-insert window.
8. **No backfill** for stage-5 comprobantes with `id_turno_caja NULL` (pre-production,
   confirmed). They stay NULL forever and are excluded from every turno derivation.
9. **`motivo` REQUIRED for `retiro` and `refuerzo`** — a deliberate tightening: money
   moving physically deserves a recorded reason. **FLAGGED AT GATE.** For
   `apertura_cajon`, `motivo` follows legacy F12 (doc-01:157), which requires a motivo of
   at least 5 characters; the spec mirrors that rule rather than inventing one.
10. **Checkout wiring**: `EmitirAsync` resolves the open turno for the punto de venta
    **early** (fail-fast `409 turno_no_abierto` before pricing/oferta resolution), carries
    `IdTurnoCaja` in the frozen `PlanDeVenta`, and populates the column at insert time.
    `gastos` writes require an open turno too, for symmetry.
11. **Caja Virtual (`arqueos_recargas` / `arqueos_recargas_canales`) OUT of this stage** —
    doc 10 §7 keeps it as a separate concern.

## Note for sdd-tasks

Slice by **write path**. Indicative order:

1. **Schema gate**: 5 tables + 4 enums + partial unique index + the
   `comprobantes_venta.id_turno_caja` FK + RLS + EF configs + backstops (the DB Change Gate
   slice; `size:exception` candidate).
2. **Turno lifecycle**: apertura (with the 23505 race test) + cierre skeleton +
   `movimientos_caja` (retiro / refuerzo / apertura de cajón).
3. **Gastos** write path (small, independent once the schema lands).
4. **Derivation + cierre**: pure `CalculadorDeArqueo` + resumen parcial endpoint + the one
   cierre transaction (arqueos + estado + tesorería). This is the stage's centerpiece.
5. **Checkout wiring** — deliberately **small and surgical**, its own slice, its own full
   judgment-day round: `EmitirAsync` precondition + `PlanDeVenta.IdTurnoCaja` +
   `AnularAsync` guard + error mappings.
6. **Web caja screens** (greenfield, likely the biggest slice and splittable: apertura +
   movimientos / resumen + cierre) and the `Pos.tsx` gate seam.

Apply the Review Workload Forecast discipline (400-line budget; exact guard lines
`Decision needed before apply`, `Chained PRs recommended`, `400-line budget risk`).
Delivery is chained PRs **stacked-to-main** per `protocolo-pr-solo-dev`, with
`judgment-day` before every PR.
