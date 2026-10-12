# Cuentas de tesorería

Locator: `odd/tasks/cuentas-de-tesoreria.md` · Engram: `odd/cuentas-de-tesoreria/tasks`
Origin: task T7 of `odd/tasks/ajustes-ui-pos.md` (owner, 2026-10-12).

## Objective

Let the owner see the real balance of each digital account (Mercado Pago, bank): each
digital payment method points to an account, carries the platform fee and the tax
withholding percentages (IIBB, IVA, Ganancias), and what is collected enters the account
net of them.

## Problem / why

Treasury is a cash-only ledger (`movimientos_tesoreria`, one chain per empresa) with no
account concept; digital collections never reach it, so the owner's platform balance
never matches what was sold. The owner chose accounts with their own running balance over
a report-only estimate (2026-10-12).

## Decisions

- **Accounts**: `cuentas_tesoreria`, per empresa, with `saldo_inicial`; soft delete only
  without references.
- **Separate ledger** `movimientos_cuenta`, chained per account (inicio/ingreso/egreso/
  final) under its own advisory lock. `movimientos_tesoreria` stays physical cash: its
  chain, lock, CHECK and reports assume one chain per empresa
  (`EscriturasDeTesoreria.cs:30-60`).
- **Fee and withholdings** live on `medios_pago`: `id_cuenta_tesoreria` (nullable) and
  four percentages `numeric(6,3) NOT NULL DEFAULT 0` (each 0..100, sum ≤ 100), allowed only
  for `comportamiento = Electronico`. A method with `id_empresa` can only use an account of
  that empresa; a tenant-wide method can be assigned an account only in single-empresa
  tenants (per-empresa mapping table deferred).
- **Money enters at shift close**, per digital method, inside
  `InsertarArqueosYTesoreriaAsync` (shared by both close modes, under the turn lock and the
  non-retrying strategy). One row per component: gross in, fee out, each withholding out.
  Base = gross collected by the method in the shift (the data
  `LectorDeMovimientosDelTurno` already reads; credit notes are negative, account receipts
  included). Percentages are read under lock and snapshotted on each row.
- **Lock order**: turn row → empresa advisory lock (cash) → account advisory locks in
  ascending id. Idempotency: `turno_ya_cerrado` first, unique partial index last.
- A recalculated closed shift does not touch account movements.
- The new fields do not travel to the POS snapshot.

### Business defaults taken (owner can change)

1. Percentages apply to the shift's gross per method, not per operation.
2. A credit note returns fee and withholdings proportionally (gross is netted).
3. A digital method with no account does not block the close; it is flagged.

## Constraints

- The owner works concurrently on `medios_pago` (`porcentaje_facturacion`): rebase on the
  latest migration; the EF model snapshot will conflict if both land at once.
- Skills: db-error-backstops, rls-migration-backfills, dto-contract-honesty,
  structural-guard-tests, single-read-under-lock, ef-retry-safe-writes, update-404-first,
  dangling-fk-read-models, web-descriptor-tests.
- Each PR slice passes judgment-day; RDD is off (clone-local).

## Delivery

Strategy `auto-chain`, chain `stacked-to-main`, one PR per task.

## Tasks

- [ ] **C1 — Schema** (delegated): entities, configurations, enum, migration, RLS,
  CHECKs, docs/10. Tests: migration and RLS under `ways_app`, structural guards, CHECKs.
- [ ] **C2 — Accounts ABM** (delegated): service, endpoints, delete only without
  references, web screen/descriptor.
- [ ] **C3 — Payment-method fields** (delegated): account + four percentages, validation,
  reject on non-electronic, clear on behaviour change, form; POS snapshot unchanged.
- [ ] **C4 — Pure calculator** (delegated or inline): gross → components, rounding.
- [ ] **C5 — Write at close** (delegated): per-account lock and writer, hook into both
  close modes, 23505 backstop, recalculation leaves accounts untouched.
- [ ] **C6 — Read side and screen** (delegated): balances per account, book per account,
  export, Tesorería block.
- [ ] **C7 — Manual adjustment** (delegated): signed adjustment with mandatory concept.
- [ ] **C8 — Transfers / deposits** (optional, later).

## Progress

| Task | Route | Commit | PR | Checks |
|------|-------|--------|----|--------|

## Next step

C1.
