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

- [x] **C1 — Schema** (delegated): entities, configurations, enum, migration, RLS,
  CHECKs, docs/10. Tests: migration and RLS under `ways_app`, structural guards, CHECKs.
- [x] **C2 — Accounts ABM** (delegated): service, endpoints, delete only without
  references, web screen/descriptor.
- [x] **C3 — Payment-method fields** (delegated): account + four percentages, validation,
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
| C1 | delegated (writer trigger: schema + tests, 35 files) | 8a145b66, 6dc997cb | #422 (merged) | build 0 errors; Domain.Tests 1514; Application.Tests 896; full integration suite 4017 passed on 8a145b66; after the fix: new classes + AislamientoDeTenant + Schema/Migracion classes 329 passed (full suite left to CI); has-pending-model-changes clean. judgment-day: round 1 no severe (implicit IX_ index, over-broad closing unique index, saldo_inicial semantics fixed in 6dc997cb), scoped re-judgment no severe |
| C2 | delegated (writer trigger: server + web, 18 files) | c038890f, 3fadd761, efa43ffe, 52c09646 | #424 (merged) | build 0 errors; Domain.Tests 1533; Application.Tests 900; integration: CuentasTesoreria* + guards + BajasDeCatalogos 220 passed, then CuentasTesoreria* + binding guards 34 passed (full suite left to CI); tsc clean; oxlint clean on touched files; vitest 224 files / 6412. judgment-day: round 1 no severe (omitted body fields defaulted — fixed), scoped re-judgment no severe. Open follow-ups: stale delete-404 message if the user opens the form during the reload; save-404 leaves the modal open; "400 before 409" order not pinned by a test |
| C3 | delegated (writer trigger: server + web, 17+ files) | c7d33085, 6c5da07f, aab4379a, 7e2d8c64 | this PR | build 0 errors; Domain.Tests 1566; Application.Tests 904; integration: filtered classes 185 passed after round 1, 94 after the polish (full suite left to CI); tsc clean; vitest 229 files / 6855. judgment-day: round 1 no severe (fields exposed to every role; form sent idEmpresa null — fixed), scoped re-judgment no severe, polish 7e2d8c64. Admin-only visibility of account/fee/withholdings. Out of scope, pre-existing: every other generic catalog descriptor sends idEmpresa null on edit (an empresa-scoped row edited from the UI becomes tenant-wide) |

## Next step

C4 + C5 (pure calculator and write at close).

Rules carried to later tasks:
- C3: the account must belong to the method's empresa (tenant-wide method: only in a
  single-empresa tenant); clear account and percentages before changing `comportamiento`
  away from `electronico` (otherwise 23514); translate 23514 on the three `ck_medios_pago_*`
  CHECKs and 23503 on the account; the account select needs a read of accounts (today the
  accounts endpoints are Admin-only, same policy as the payment-method catalog); an inactive
  account is not offered for new assignments but existing assignments keep working; the new
  fields stay out of the POS snapshot.
- C5: the writer reads `saldo_inicial` inside its transaction AFTER locking the account row
  with a lock that conflicts with the ABM's FOR UPDATE; lock order turn row → empresa
  advisory lock → account locks in ascending id; always send `id_medio_pago` (the unique
  closing index does not dedupe NULL); a negative gross is `cobro` on the egreso side;
  translate 23505 on `ux_movimientos_cuenta_cierre`; decide whether an inactive account
  still receives movements (default: yes, deactivation only stops new assignments).
- C6: Supervisor reads treasury today; the accounts read for the treasury screen must be
  under the reports policy, not the catalog one.
